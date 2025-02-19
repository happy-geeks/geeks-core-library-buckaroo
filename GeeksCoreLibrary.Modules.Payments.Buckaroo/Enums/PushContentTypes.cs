namespace GeeksCoreLibrary.Modules.Payments.Buckaroo.Enums;

/// <summary>
/// The content type of the push request from Buckaroo.
/// </summary>
public enum PushContentTypes
{
    /// <summary>
    /// The push request is in JSON format.
    /// </summary>
    Json = 1,

    /// <summary>
    /// The push request is comprised of form data.
    /// </summary>
    HttpPost = 2,
    
    /// <summary>
    /// The push request is a get request with query data
    /// </summary>
    HttpGet = 3
}