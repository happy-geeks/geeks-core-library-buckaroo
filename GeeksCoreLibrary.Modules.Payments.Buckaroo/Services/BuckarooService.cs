﻿using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BuckarooSdk.DataTypes;
using BuckarooSdk.DataTypes.ParameterGroups.InThree;
using BuckarooSdk.DataTypes.RequestBases;
using BuckarooSdk.Services;
using BuckarooSdk.Services.CreditCards.BanContact.Request;
using BuckarooSdk.Services.CreditCards.Request;
using BuckarooSdk.Services.Ideal.TransactionRequest;
using BuckarooSdk.Services.InThree;
using BuckarooSdk.Services.PayPal;
using BuckarooSdk.Transaction;
using GeeksCoreLibrary.Components.OrderProcess.Models;
using GeeksCoreLibrary.Components.ShoppingBasket;
using GeeksCoreLibrary.Components.ShoppingBasket.Interfaces;
using GeeksCoreLibrary.Components.ShoppingBasket.Models;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using GeeksCoreLibrary.Core.Enums;
using GeeksCoreLibrary.Core.Extensions;
using GeeksCoreLibrary.Core.Helpers;
using GeeksCoreLibrary.Core.Models;
using GeeksCoreLibrary.Modules.Databases.Interfaces;
using GeeksCoreLibrary.Modules.Payments.Buckaroo.Enums;
using GeeksCoreLibrary.Modules.Payments.Enums;
using GeeksCoreLibrary.Modules.Payments.Interfaces;
using GeeksCoreLibrary.Modules.Payments.Models;
using GeeksCoreLibrary.Modules.Payments.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BuckarooSettingsModel = GeeksCoreLibrary.Modules.Payments.Buckaroo.Models.BuckarooSettingsModel;
using BuckarooConstants = GeeksCoreLibrary.Modules.Payments.Buckaroo.Models.Constants;
using Constants = GeeksCoreLibrary.Components.OrderProcess.Models.Constants;

namespace GeeksCoreLibrary.Modules.Payments.Buckaroo.Services;

/// <inheritdoc cref="IPaymentServiceProviderService" />
public class BuckarooService(
    ILogger<BuckarooService> logger,
    IOptions<GclSettings> gclSettings,
    IShoppingBasketsService shoppingBasketsService,
    IDatabaseConnection databaseConnection,
    IDatabaseHelpersService databaseHelpersService,
    IHttpContextAccessor? httpContextAccessor = null)
    : PaymentServiceProviderBaseService(databaseHelpersService, databaseConnection, logger, httpContextAccessor), IPaymentServiceProviderService, IScopedService
{
    private readonly GclSettings gclSettings = gclSettings.Value;
    private readonly IHttpContextAccessor? httpContextAccessor = httpContextAccessor;
    private readonly IDatabaseConnection databaseConnection = databaseConnection;

    /// <inheritdoc />
    public async Task<PaymentRequestResult> HandlePaymentRequestAsync(ICollection<(WiserItemModel Main, List<WiserItemModel> Lines)> shoppingBaskets, WiserItemModel userDetails, PaymentMethodSettingsModel paymentMethodSettings, string invoiceNumber)
    {
        // https://github.com/buckaroo-it/BuckarooSdk_DotNet/blob/master/BuckarooSdk.Tests/Services

        var basketSettings = await shoppingBasketsService.GetSettingsAsync();
        var buckarooSettings = (BuckarooSettingsModel) paymentMethodSettings.PaymentServiceProvider;

        var totalPrice = 0M;
        foreach (var (main, lines) in shoppingBaskets)
        {
            totalPrice += await shoppingBasketsService.GetPriceAsync(main, lines, basketSettings, ShoppingBasket.PriceTypes.PspPriceInVat);
        }

        // Check if the test environment should be used.
        var useTestEnvironment = gclSettings.Environment.InList(Environments.Test, Environments.Development);

        var buckarooClient = new BuckarooSdk.SdkClient();

        var transaction = buckarooClient.CreateRequest()
            .Authenticate(buckarooSettings.WebsiteKey, buckarooSettings.SecretKey, !useTestEnvironment, CultureInfo.CurrentCulture)
            .TransactionRequest()
            .SetBasicFields(new TransactionBase
            {
                Currency = buckarooSettings.Currency,
                AmountDebit = totalPrice,
                Invoice = invoiceNumber,
                PushUrl = buckarooSettings.WebhookUrl,
                ReturnUrl = buckarooSettings.SuccessUrl,
                ReturnUrlCancel = buckarooSettings.FailUrl,
                ReturnUrlError = buckarooSettings.FailUrl,
                ReturnUrlReject = buckarooSettings.FailUrl,
                ContinueOnIncomplete = CheckIfContinueOnIncompleteIsAllowed(shoppingBaskets, paymentMethodSettings.ExternalName) ? ContinueOnIncomplete.RedirectToHTML : ContinueOnIncomplete.No
            });

        ConfiguredServiceTransaction serviceTransaction;
        switch (paymentMethodSettings.ExternalName.ToUpperInvariant())
        {
            case "IDEAL":
                serviceTransaction = InitializeIdealPayment(transaction, shoppingBaskets);
                break;
            case "MASTERCARD":
                serviceTransaction = InitializeMasterCardPayment(transaction);
                break;
            case "VISA":
                serviceTransaction = InitializeVisaPayment(transaction);
                break;
            case "PAYPAL":
                serviceTransaction = InitializePayPalPayment(transaction);
                break;
            case "BANCONTACT":
                serviceTransaction = InitializeBancontactPayment(transaction);
                break;
            case "IDEAL_IN_THREE":
                serviceTransaction = await InitializeIdealInThreePaymentAsync(transaction, shoppingBaskets, basketSettings);
                break;
            default:
                return new PaymentRequestResult
                {
                    Action = PaymentRequestActions.Redirect,
                    ActionData = buckarooSettings.FailUrl,
                    Successful = false,
                    ErrorMessage = $"Unknown or unsupported payment method '{paymentMethodSettings}'"
                };
        }

        var response = await serviceTransaction.ExecuteAsync();

        var successStatusCodes = new List<int> {190, 790, 791};
        if (response?.Status?.Code?.Code == null || !successStatusCodes.Contains(response.Status.Code.Code) || String.IsNullOrWhiteSpace(response.RequiredAction?.RedirectURL))
        {
            return new PaymentRequestResult
            {
                Successful = false,
                Action = PaymentRequestActions.Redirect,
                ActionData = buckarooSettings.FailUrl,
                ErrorMessage = response?.Status?.Code?.Description
            };
        }

        return new PaymentRequestResult
        {
            Successful = true,
            Action = PaymentRequestActions.Redirect,
            ActionData = response.RequiredAction.RedirectURL
        };
    }

    private async Task<ConfiguredServiceTransaction> InitializeIdealInThreePaymentAsync(ConfiguredTransaction transaction, ICollection<(WiserItemModel Main, List<WiserItemModel> Lines)> shoppingBaskets, ShoppingBasketCmsSettingsModel basketSettings)
    {
        var request = new InThreePayRequest
        {
            Articles = new ParameterGroupCollection<Article>("Article")
        };

        var firstBasket = shoppingBaskets.First().Main;

        var street = firstBasket.GetDetailValue("street");
        var streetNumber = firstBasket.GetDetailValue("housenumber");
        var streetNumberSuffix = firstBasket.GetDetailValue("housenumber_suffix");
        var zipcode = firstBasket.GetDetailValue("zipcode");
        var city = firstBasket.GetDetailValue("city");
        var countryCode = firstBasket.GetDetailValue("country").ToUpperInvariant();

        request.BillingCustomer = new BillingCustomer
        {
            CustomerNumber = firstBasket.Id.ToString(),
            FirstName = firstBasket.GetDetailValue("firstname"),
            LastName = firstBasket.GetDetailValue("lastname"),
            Email = firstBasket.GetDetailValue("email"),
            Phone = firstBasket.GetDetailValue("phone"),
            Street = street,
            StreetNumber = streetNumber,
            StreetNumberSuffix = streetNumberSuffix,
            PostalCode = zipcode,
            City = city,
            CountryCode = countryCode.ToUpperInvariant(),
            CompanyName = firstBasket.GetDetailValue("companyname"),
            Category = String.IsNullOrEmpty(firstBasket.GetDetailValue("companyname")) ? "B2C" : "B2B"
        };

        var shippingPrefix = String.Empty;
        if (!String.IsNullOrEmpty(firstBasket.GetDetailValue("shipping_zipcode")))
        {
            shippingPrefix = "shipping_";
        }

        request.ShippingCustomer = new ShippingCustomer()
        {
            Street = firstBasket.GetDetailValue($"{shippingPrefix}street") ?? street,
            StreetNumber = firstBasket.GetDetailValue($"{shippingPrefix}housenumber") ?? streetNumber,
            PostalCode = firstBasket.GetDetailValue($"{shippingPrefix}zipcode") ?? zipcode,
            StreetNumberSuffix = firstBasket.GetDetailValue($"{shippingPrefix}housenumber_suffix") ?? streetNumberSuffix,
            City = firstBasket.GetDetailValue($"{shippingPrefix}city") ?? city,
            CountryCode = firstBasket.GetDetailValue($"{shippingPrefix}country")?.ToUpperInvariant() ?? countryCode
        };

        foreach (var shoppingBasket in shoppingBaskets)
        {
            foreach (var basketLine in shoppingBasket.Lines)
            {
                var article = new Article
                {
                    Description = basketLine.GetDetailValue("Title") ?? basketLine.Title,
                    GrossUnitPrice = (await shoppingBasketsService.GetLinePriceAsync(shoppingBasket.Main, basketLine, basketSettings, singlePrice: true)).ToString("F2"),
                    Quantity = Convert.ToInt32(basketLine.GetDetailValue("quantity"))
                };
                request.Articles.Add(article);
            }
        }

        return transaction.InThree()
            .Pay(request);
    }

    private ConfiguredServiceTransaction InitializeIdealPayment(ConfiguredTransaction transaction, IEnumerable<(WiserItemModel Main, List<WiserItemModel> Lines)> shoppingBaskets)
    {
        // Bank name.
        var issuerValue = shoppingBaskets.First().Main.GetDetailValue(Constants.PaymentMethodIssuerProperty);
        var issuerName = GetIssuerName(issuerValue);

        return transaction.Ideal()
            .Pay(new IdealPayRequest
            {
                Issuer = String.IsNullOrWhiteSpace(issuerName) ? null : issuerName
            });
    }

    private ConfiguredServiceTransaction InitializePayPalPayment(ConfiguredTransaction transaction)
    {
        return transaction.PayPal()
            .Pay(new PayPalPayRequest());
    }

    private ConfiguredServiceTransaction InitializeMasterCardPayment(ConfiguredTransaction transaction)
    {
        return transaction.MasterCard().Pay(new CreditCardPayRequest());
    }

    private ConfiguredServiceTransaction InitializeVisaPayment(ConfiguredTransaction transaction)
    {
        return transaction.Visa().Pay(new CreditCardPayRequest());
    }

    private ConfiguredServiceTransaction InitializeBancontactPayment(ConfiguredTransaction transaction)
    {
        return transaction.Bancontact().Pay(new BancontactPayRequest());
    }

    private bool CheckIfContinueOnIncompleteIsAllowed(IEnumerable<(WiserItemModel Main, List<WiserItemModel> Lines)> shoppingBaskets, string paymentMethod)
    {
        if (!String.Equals(paymentMethod, "ideal", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var issuerValue = shoppingBaskets.First().Main.GetDetailValue("issuer");
        var issuerName = GetIssuerName(issuerValue);
        return String.IsNullOrWhiteSpace(issuerName);
    }

    /// <inheritdoc />
    public async Task<StatusUpdateResult> ProcessStatusUpdateAsync(OrderProcessSettingsModel orderProcessSettings, PaymentMethodSettingsModel paymentMethodSettings)
    {
        if (httpContextAccessor?.HttpContext == null)
        {
            return new StatusUpdateResult
            {
                Status = "Request not available; unable to process status update.",
                Successful = false
            };
        }

        // Try to get the invoice number from the form.
        var invoiceNumber = "";
        if (httpContextAccessor.HttpContext.Request.HasFormContentType)
        {
            invoiceNumber = httpContextAccessor.HttpContext.Request.Form[BuckarooConstants.WebhookInvoiceNumberProperty].ToString();
        }

        // If the invoice number is still empty, try to get it from the query string.
        if (String.IsNullOrEmpty(invoiceNumber))
        {
            invoiceNumber = httpContextAccessor.HttpContext.Request.Query[BuckarooConstants.WebhookInvoiceNumberProperty].ToString();
        }

        if (String.IsNullOrWhiteSpace(invoiceNumber))
        {
            // No invoice number found, so we can't process the status update.
            return new StatusUpdateResult
            {
                Status = "No invoice number in request found; unable to process status update.",
                Successful = false
            };
        }

        string? bodyJson = null;
        var result = new StatusUpdateResult();

        try
        {
            var buckarooSettings = (BuckarooSettingsModel) paymentMethodSettings.PaymentServiceProvider;
            switch (buckarooSettings.PushContentType)
            {
                case PushContentTypes.Json:
                {
                    // Read the entire body, which should be a JSON body from Buckaroo.
                    using var reader = new StreamReader(httpContextAccessor.HttpContext.Request.Body);
                    bodyJson = await reader.ReadToEndAsync();
                    result = HandleJsonStatusUpdate(buckarooSettings, invoiceNumber, bodyJson);
                    break;
                }
                case PushContentTypes.HttpPost:
                    result = HandleFormStatusUpdate(buckarooSettings);
                    break;
                default:
                    throw new ArgumentOutOfRangeException($"Unknown push content type '{buckarooSettings.PushContentType}'");
            }
        }
        catch (Exception exception)
        {
            // Log any exceptions that may have occurred.
            logger.LogError(exception, "Error processing Buckaroo status update");
        }
        finally
        {
            // Always log the incoming payment.
            await LogIncomingPaymentActionAsync(PaymentServiceProviders.Buckaroo, invoiceNumber, result?.StatusCode ?? 0, bodyJson);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<PaymentServiceProviderSettingsModel> GetProviderSettingsAsync(PaymentServiceProviderSettingsModel paymentServiceProviderSettings)
    {
        databaseConnection.AddParameter("id", paymentServiceProviderSettings.Id);

        var query = $@"SELECT
    buckarooWebsiteKeyLive.`value` AS buckarooWebsiteKeyLive,
    buckarooWebsiteKeyTest.`value` AS buckarooWebsiteKeyTest,
    buckarooSecretKeyLive.`value` AS buckarooSecretKeyLive,
    buckarooSecretKeyTest.`value` AS buckarooSecretKeyTest,
    buckarooPushContentType.`value` AS buckarooPushContentType,
    buckarooHashMethod.`value` AS buckarooHashMethod
FROM {WiserTableNames.WiserItem} AS paymentServiceProvider
LEFT JOIN {WiserTableNames.WiserItemDetail} AS buckarooWebsiteKeyLive ON buckarooWebsiteKeyLive.item_id = paymentServiceProvider.id AND buckarooWebsiteKeyLive.`key` = '{BuckarooConstants.BuckarooWebsiteKeyLiveProperty}'
LEFT JOIN {WiserTableNames.WiserItemDetail} AS buckarooWebsiteKeyTest ON buckarooWebsiteKeyTest.item_id = paymentServiceProvider.id AND buckarooWebsiteKeyTest.`key` = '{BuckarooConstants.BuckarooWebsiteKeyTestProperty}'
LEFT JOIN {WiserTableNames.WiserItemDetail} AS buckarooSecretKeyLive ON buckarooSecretKeyLive.item_id = paymentServiceProvider.id AND buckarooSecretKeyLive.`key` = '{BuckarooConstants.BuckarooSecretKeyLiveProperty}'
LEFT JOIN {WiserTableNames.WiserItemDetail} AS buckarooSecretKeyTest ON buckarooSecretKeyTest.item_id = paymentServiceProvider.id AND buckarooSecretKeyTest.`key` = '{BuckarooConstants.BuckarooSecretKeyTestProperty}'
LEFT JOIN {WiserTableNames.WiserItemDetail} AS buckarooPushContentType ON buckarooPushContentType.item_id = paymentServiceProvider.id AND buckarooPushContentType.`key` = '{BuckarooConstants.BuckarooPushContentTypeProperty}'
LEFT JOIN {WiserTableNames.WiserItemDetail} AS buckarooHashMethod ON buckarooHashMethod.item_id = paymentServiceProvider.id AND buckarooHashMethod.`key` = '{BuckarooConstants.BuckarooHashMethodProperty}'
WHERE paymentServiceProvider.id = ?id
AND paymentServiceProvider.entity_type = '{Constants.PaymentServiceProviderEntityType}'";


        var result = new BuckarooSettingsModel
        {
            Id = paymentServiceProviderSettings.Id,
            Title = paymentServiceProviderSettings.Title,
            Type = paymentServiceProviderSettings.Type,
            LogAllRequests = paymentServiceProviderSettings.LogAllRequests,
            OrdersCanBeSetDirectlyToFinished = paymentServiceProviderSettings.OrdersCanBeSetDirectlyToFinished,
            SkipPaymentWhenOrderAmountEqualsZero = paymentServiceProviderSettings.SkipPaymentWhenOrderAmountEqualsZero
        };

        var dataTable = await databaseConnection.GetAsync(query);
        if (dataTable.Rows.Count == 0)
        {
            return result;
        }

        var row = dataTable.Rows[0];

        var suffix = gclSettings.Environment.InList(Environments.Development, Environments.Test) ? "Test" : "Live";
        result.WebsiteKey = row.GetAndDecryptSecretKey($"buckarooWebsiteKey{suffix}");
        result.SecretKey = row.GetAndDecryptSecretKey($"buckarooSecretKey{suffix}");
        result.PushContentType = row.GetEnumValue<PushContentTypes>("buckarooPushContentType");
        result.HashMethod = row.GetEnumValue<HashMethods>("buckarooHashMethod");
        return result;
    }

    /// <inheritdoc />
    public Task<string> GetInvoiceNumberFromRequestAsync()
    {
        return Task.FromResult(HttpContextHelpers.GetRequestValue(httpContextAccessor?.HttpContext, BuckarooConstants.WebhookInvoiceNumberProperty));
    }

    /// <summary>
    /// Handles the status update using the JSON body.
    /// </summary>
    /// <param name="buckarooSettings">The settings for Buckaroo.</param>
    /// <param name="invoiceNumber">The payment's invoice number.</param>
    /// <param name="bodyJson">The request body as a string, in JSON format.</param>
    /// <returns>A <see cref="StatusUpdateResult"/> object.</returns>
    private StatusUpdateResult HandleJsonStatusUpdate(BuckarooSettingsModel buckarooSettings, string invoiceNumber, string bodyJson)
    {
        var bodyAsBytes = Encoding.UTF8.GetBytes(bodyJson);

        // Create nonce.
        var timeSpan = DateTime.UtcNow - DateTime.UnixEpoch;
        var requestTimeStamp = Convert.ToUInt64(timeSpan.TotalSeconds).ToString();

        var buckarooClient = new BuckarooSdk.SdkClient();
        var pushSignature = buckarooClient.GetSignatureCalculationService().CalculateSignature(bodyAsBytes, HttpMethods.Post, requestTimeStamp, Guid.NewGuid().ToString("N"), buckarooSettings.WebhookUrl, buckarooSettings.WebsiteKey, buckarooSettings.SecretKey);
        var authHeader = $"hmac {pushSignature}";

        BuckarooSdk.DataTypes.Push.Push push;

        try
        {
            push = buckarooClient.GetPushHandler(buckarooSettings.SecretKey).DeserializePush(bodyAsBytes, buckarooSettings.WebhookUrl, authHeader);
        }
        catch (System.Security.Authentication.AuthenticationException exception)
        {
            logger.LogError(exception, "Error processing Buckaroo status update");

            return new StatusUpdateResult
            {
                Status = "Signature was incorrect.",
                StatusCode = 0,
                Successful = false
            };
        }

        var successful = push.Status.Code.Code == BuckarooSdk.Constants.Status.Success;
        var statusMessage = push.Status.Code.Description;

        return new StatusUpdateResult
        {
            Status = statusMessage,
            StatusCode = push.Status.Code.Code,
            Successful = successful
        };
    }

    /// <summary>
    /// Handles the status update using form values.
    /// </summary>
    /// <param name="buckarooSettings">The settings for Buckaroo.</param>
    /// <returns>A <see cref="StatusUpdateResult"/> object.</returns>
    private StatusUpdateResult HandleFormStatusUpdate(BuckarooSettingsModel buckarooSettings)
    {
        if (httpContextAccessor?.HttpContext == null)
        {
            return new StatusUpdateResult
            {
                Status = "No HTTP context available; unable to process status update.",
                StatusCode = 0,
                Successful = false
            };
        }

        if (!Int32.TryParse(httpContextAccessor.HttpContext.Request.Form["brq_statuscode"].ToString(), out var statusCode))
        {
            return new StatusUpdateResult
            {
                Status = $"Invalid status code '{statusCode}'",
                StatusCode = statusCode,
                Successful = false
            };
        }

        // Get all form values that begin with "brq_", "add_" or "cust_", except "brq_signature".
        var formValues = httpContextAccessor.HttpContext.Request.Form.Where(kvp => !kvp.Key.Equals("brq_signature") && (kvp.Key.StartsWith("brq_") || kvp.Key.StartsWith("add_") || kvp.Key.StartsWith("cust_"))).ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());
        var buckarooSignature = httpContextAccessor.HttpContext.Request.Form["brq_signature"].ToString();

        // Sort the formValues dictionary alphabetically by key.
        formValues = formValues.OrderBy(kvp => kvp.Key).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var signatureBuilder = new StringBuilder();
        foreach (var formValue in formValues)
        {
            signatureBuilder.Append($"{formValue.Key}={formValue.Value}");
        }

        if (!String.IsNullOrWhiteSpace(buckarooSettings.SecretKey))
        {
            signatureBuilder.Append(buckarooSettings.SecretKey);
        }

        // Hash the signature builder with SHA1.
        var hash = buckarooSettings.HashMethod switch
        {
            HashMethods.Sha1 => SHA1.HashData(Encoding.UTF8.GetBytes(signatureBuilder.ToString())),
            HashMethods.Sha256 => SHA256.HashData(Encoding.UTF8.GetBytes(signatureBuilder.ToString())),
            HashMethods.Sha512 => SHA512.HashData(Encoding.UTF8.GetBytes(signatureBuilder.ToString())),
            _ => throw new ArgumentOutOfRangeException($"Hash method '{buckarooSettings.HashMethod}' is not supported.")
        };

        var signatureHash = BitConverter.ToString(hash).Replace("-", "").ToLower();

        // Compare hashes.
        if (String.Equals(buckarooSignature, signatureHash, StringComparison.OrdinalIgnoreCase))
        {
            return new StatusUpdateResult
            {
                Status = httpContextAccessor.HttpContext.Request.Form["brq_statusmessage"].ToString(),
                StatusCode = statusCode,
                Successful = statusCode.InList(190, 790)
            };
        }

        return new StatusUpdateResult
        {
            Status = "Signature was incorrect.",
            StatusCode = statusCode,
            Successful = false
        };
    }

    #region Helper functions

    private string? GetIssuerName(string issuerValue)
    {
        var buckarooIssuerConstants = typeof(BuckarooSdk.Services.Ideal.Constants.Issuers).GetFields(BindingFlags.Public | BindingFlags.Static);
        var issuerConstant = buckarooIssuerConstants.FirstOrDefault(mi => mi.Name.Equals(issuerValue, StringComparison.OrdinalIgnoreCase));

        if (issuerConstant != null)
        {
            return issuerConstant.GetValue(null) as string;
        }

        // Check for legacy types (which were numbers).
        return GetBuckarooIssuer(issuerValue);
    }

    private static string GetBuckarooIssuer(string issuerValue)
    {
        return (issuerValue switch
        {
            "1" => BuckarooSdk.Services.Ideal.Constants.Issuers.AbnAmro,
            "2" => BuckarooSdk.Services.Ideal.Constants.Issuers.AsnBank,
            "5" => BuckarooSdk.Services.Ideal.Constants.Issuers.IngBank,
            "6" => BuckarooSdk.Services.Ideal.Constants.Issuers.RaboBank,
            "7" => BuckarooSdk.Services.Ideal.Constants.Issuers.SnsBank,
            "9" => BuckarooSdk.Services.Ideal.Constants.Issuers.TriodosBank,
            "10" => BuckarooSdk.Services.Ideal.Constants.Issuers.VanLanschot,
            "11" => BuckarooSdk.Services.Ideal.Constants.Issuers.Knab,
            "12" => BuckarooSdk.Services.Ideal.Constants.Issuers.Bunq,
            _ => null
        })!;
    }

    #endregion
}