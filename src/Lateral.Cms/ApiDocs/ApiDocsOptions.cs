namespace Lateral.Cms.ApiDocs;

/// <summary>
/// The interactive API reference (Swagger UI at <c>/swagger</c>) and the OpenAPI document behind it. Both need a signed-in caller,
/// like every other route, and neither carries any data: they describe the routes only.
/// </summary>
public sealed class ApiDocsOptions
{
    public const string SectionName = "ApiDocs";

    /// <summary>
    /// When false, <c>/swagger</c> and <c>/swagger/v1/swagger.json</c> are not served at all (404). On by default: the reference
    /// is behind sign-in and shows only the shape of the API.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
