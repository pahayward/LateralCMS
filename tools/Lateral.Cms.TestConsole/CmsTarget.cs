namespace Lateral.Cms.TestConsole;

/// <summary>
/// Turns a path the browser asked for into a CMS address. Only the CMS's own routes are reachable and the host
/// can never change, so the console cannot be used to reach anything else (an open relay).
/// </summary>
public static class CmsTarget
{
    private static readonly string[] AllowedPrefixes =
        ["cms/events", "cms/batches", "api/entities", "api/admin/reset", "api/admin/ingestion", "health"];

    public static bool TryBuild(Uri baseUri, string? path, string? query, out Uri target, out string error)
    {
        target = baseUri;
        error = "";

        if (string.IsNullOrEmpty(path))
        {
            error = "No path given.";
            return false;
        }

        var segments = path.Split('/');
        if (segments.Any(s => s.Length == 0 || s is "." or ".." || s.Any(c => char.IsControl(c) || c == '\\')))
        {
            error = "The path is not valid.";
            return false;
        }

        var normalised = string.Join('/', segments);
        if (!AllowedPrefixes.Any(p => normalised.Equals(p, StringComparison.OrdinalIgnoreCase)
                                      || normalised.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)))
        {
            error = "That path is not one of the CMS endpoints the console may call.";
            return false;
        }

        var root = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
        var escaped = string.Join('/', segments.Select(Uri.EscapeDataString));
        var suffix = string.IsNullOrEmpty(query) ? "" : (query.StartsWith('?') ? query : "?" + query);

        if (!Uri.TryCreate(root, escaped + suffix, out var built)
            || built.Scheme != root.Scheme
            || built.Authority != root.Authority
            || !built.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal))
        {
            error = "The path does not resolve to the CMS.";
            return false;
        }

        target = built;
        return true;
    }
}
