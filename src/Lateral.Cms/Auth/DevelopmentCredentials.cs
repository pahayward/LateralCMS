using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Auth;

/// <summary>
/// Gives <c>dotnet run</c> working accounts without any password being committed. In the Development environment, and only if
/// no credentials were supplied another way, random ones are generated on first run into
/// <c>App_Data/dev-credentials.json</c> (which is git-ignored) and added as a configuration source. Delete the file to rotate them.
/// </summary>
public static class DevelopmentCredentials
{
    public const string FileName = "dev-credentials.json";

    public static string? AddIfDevelopment(WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment() || !string.IsNullOrWhiteSpace(builder.Configuration["Auth:Cms:Username"]))
        {
            return null;
        }

        var path = Path.Combine(builder.Environment.ContentRootPath, "App_Data", FileName);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Generate());
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        builder.Configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
        return path;
    }

    /// <summary>The settings document: the same <c>Auth</c> shape as any other configuration source.</summary>
    public static string Generate()
    {
        static string Guid() => System.Guid.NewGuid().ToString();
        var document = new
        {
            _comment = "Generated development-only credentials. Git-ignored. Delete this file to generate new ones; never reuse them anywhere real.",
            Auth = new
            {
                Cms = new { Username = "cms-ingest-svc", Passwords = new[] { Guid() } },
                Users = new[]
                {
                    new { Username = "user@lateral.example", Password = Guid(), Role = "User" },
                    new { Username = "admin@lateral.example", Password = Guid(), Role = "Admin" },
                },
            },
        };
        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }
}

/// <summary>
/// Behind a reverse proxy every request seems to come from the proxy, which would make the failed-sign-in throttle count everyone
/// together. Name the proxies you trust and their <c>X-Forwarded-For</c> is believed; with none named (the default) the header is
/// ignored entirely, so a caller cannot spoof an address to dodge the throttle.
/// </summary>
public sealed class ProxyOptions
{
    public const string SectionName = "Proxy";

    /// <summary>Addresses of trusted proxies.</summary>
    public List<string> KnownProxies { get; set; } = [];

    /// <summary>Trusted networks in CIDR form, for example <c>10.0.0.0/8</c>.</summary>
    public List<string> KnownNetworks { get; set; } = [];

    public bool Enabled => KnownProxies.Count + KnownNetworks.Count > 0;

    public static void Apply(ForwardedHeadersOptions forwarded, IOptions<ProxyOptions> options)
    {
        var proxy = options.Value;
        if (!proxy.Enabled)
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.None;
            return;
        }

        forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        forwarded.KnownProxies.Clear();
        forwarded.KnownNetworks.Clear();
        forwarded.ForwardLimit = 1;
        foreach (var address in proxy.KnownProxies)
        {
            forwarded.KnownProxies.Add(IPAddress.Parse(address));
        }

        foreach (var network in proxy.KnownNetworks)
        {
            var parts = network.Split('/');
            forwarded.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse(parts[0]), int.Parse(parts[1])));
        }
    }
}

public sealed class ProxyOptionsValidator : IValidateOptions<ProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, ProxyOptions options)
    {
        var errors = new List<string>();
        errors.AddRange(options.KnownProxies.Where(p => !IPAddress.TryParse(p, out _)).Select(p => $"Proxy:KnownProxies: '{p}' is not an IP address."));
        errors.AddRange(options.KnownNetworks.Where(n => !TryNetwork(n)).Select(n => $"Proxy:KnownNetworks: '{n}' is not a CIDR network such as 10.0.0.0/8."));
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool TryNetwork(string text) =>
        text.Split('/') is [var address, var prefix] && IPAddress.TryParse(address, out var ip)
        && int.TryParse(prefix, out var bits) && bits >= 0 && bits <= (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
}
