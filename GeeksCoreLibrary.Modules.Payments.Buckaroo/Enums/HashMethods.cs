namespace GeeksCoreLibrary.Modules.Payments.Buckaroo.Enums;

/// <summary>
/// The hash method used by Buckaroo to sign the push request.
/// </summary>
public enum HashMethods
{
    Sha1 = 1,
    Sha256 = 2,
    Sha512 = 3
}