using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lateral.Cms.Assets;
using Lateral.Cms.Auth;
using Lateral.Cms.Entities;
using Lateral.Cms.Ingestion;
using Lateral.Cms.Persistence;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Admin;

/// <summary>
/// What the service is actually configured with: every setting it reads, its value now, its default, where the value came from
/// (a file, an environment variable, the command line, or nowhere, meaning the default) and what it is for. The descriptions come
/// from <c>settings.json</c>; a test fails if a setting exists without one, so this page cannot fall behind the code. Secrets are
/// reported only as set or not set.
/// </summary>
public sealed partial class SettingsReport(IServiceProvider services, IConfiguration configuration, IHostEnvironment environment)
{
    /// <summary>Each section of configuration and the options class it binds to.</summary>
    public static readonly (string Section, Type Type)[] Sections =
    [
        (AuthOptions.SectionName, typeof(AuthOptions)),
        (DatabaseOptions.SectionName, typeof(DatabaseOptions)),
        (IngestionOptions.SectionName, typeof(IngestionOptions)),
        (InboxOptions.SectionName, typeof(InboxOptions)),
        (AssetOptions.SectionName, typeof(AssetOptions)),
        (ApiOptions.SectionName, typeof(ApiOptions)),
        (AdminOptions.SectionName, typeof(AdminOptions)),
        (ProxyOptions.SectionName, typeof(ProxyOptions)),
    ];

    private sealed record Described(string Key, string Group, string Description, bool Secret, bool Host, string? Env, string? Example);

    /// <summary>The key of every setting the options classes define, as <c>Section:Property:Sub</c> (lists count as one).</summary>
    public static IEnumerable<string> OptionKeys() => Sections.SelectMany(s => Walk(s.Type, s.Section));

    private static IEnumerable<string> Walk(Type type, string prefix)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.GetIndexParameters().Length == 0))
        {
            var key = $"{prefix}:{property.Name}";
            if (IsGroup(property.PropertyType))
            {
                foreach (var inner in Walk(property.PropertyType, key))
                {
                    yield return inner;
                }
            }
            else
            {
                yield return key;
            }
        }
    }

    private static bool IsGroup(Type type) =>
        type.IsClass && type != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(type);

    private static List<Described> Catalogue()
    {
        using var stream = typeof(SettingsReport).Assembly.GetManifestResourceStream("settings.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("settings").EnumerateArray().Select(e => new Described(
            e.GetProperty("key").GetString()!,
            e.GetProperty("group").GetString()!,
            e.GetProperty("description").GetString()!,
            e.TryGetProperty("secret", out var secret) && secret.GetBoolean(),
            e.TryGetProperty("kind", out var kind) && kind.GetString() == "host",
            e.TryGetProperty("env", out var env) ? env.GetString() : null,
            e.TryGetProperty("example", out var example) ? example.GetString() : null)).ToList();
    }

    /// <summary>The groups and descriptions, exactly as written (for the test that compares them with the options classes).</summary>
    public static (List<string> Keys, List<string> HostKeys, List<string> Groups) CatalogueKeys()
    {
        var all = Catalogue();
        return (all.Where(c => !c.Host).Select(c => c.Key).ToList(), all.Where(c => c.Host).Select(c => c.Key).ToList(), all.Select(c => c.Group).Distinct().ToList());
    }

    public object Build()
    {
        using var stream = typeof(SettingsReport).Assembly.GetManifestResourceStream("settings.json")!;
        using var document = JsonDocument.Parse(stream);
        var groups = document.RootElement.GetProperty("groups").EnumerateArray()
            .Select(g => (Id: g.GetProperty("id").GetString()!, Title: g.GetProperty("title").GetString()!, About: g.GetProperty("about").GetString()!))
            .ToList();
        var settings = Catalogue().Select(Report).ToList();

        return new
        {
            environment = environment.EnvironmentName,
            contentRoot = environment.ContentRootPath,
            note = "Configuration is read when the service starts. To change a value, set it in appsettings.Production.json or as an environment variable (the key with : written as __), then restart the service.",
            order = "Later sources win: appsettings.json, then appsettings.<Environment>.json, then environment variables, then the command line.",
            groups = groups.Select(g => new
            {
                id = g.Id,
                title = g.Title,
                about = g.About,
                settings = settings.Where(s => s.group == g.Id).ToList(),
            }).ToList(),
        };
    }

    private SettingView Report(Described setting)
    {
        var (value, fallback) = setting.Host ? (HostValue(setting.Key), null) : Effective(setting.Key);
        var source = SourceOf(setting.Key);
        var isDefault = source is null;

        object? shown = setting.Secret ? (value is ICollection { Count: > 0 } || (value is string { Length: > 0 }) ? "set" : "not set") : Redact(value);
        object? defaultShown = setting.Secret ? null : Redact(fallback);
        return new SettingView(
            setting.Key,
            setting.Group,
            setting.Description,
            shown,
            defaultShown,
            isDefault,
            source ?? (setting.Host ? "not set" : "default"),
            setting.Env ?? setting.Key.Replace(":", "__"),
            setting.Secret,
            setting.Example);
    }

    public sealed record SettingView(
        string key, string group, string description, object? value, object? @default, bool isDefault, string source,
        string env, bool secret, string? example);

    private object? HostValue(string key) => key switch
    {
        "Urls" => configuration["urls"] ?? configuration["ASPNETCORE_URLS"] ?? "http://localhost:5000 (the default)",
        _ => configuration[key],
    };

    /// <summary>The value the running service uses, and the class's own default, found by walking the key down the options object.</summary>
    private (object? Value, object? Default) Effective(string key)
    {
        var parts = key.Split(':');
        var type = Sections.First(s => s.Section == parts[0]).Type;
        var wrapper = typeof(IOptions<>).MakeGenericType(type);
        object? current = wrapper.GetProperty(nameof(IOptions<object>.Value))!.GetValue(services.GetRequiredService(wrapper));
        object? defaults = Activator.CreateInstance(type);

        foreach (var name in parts.Skip(1))
        {
            var property = current!.GetType().GetProperty(name)!;
            current = property.GetValue(current);
            defaults = property.GetValue(defaults);
        }

        return (Plain(current), Plain(defaults));
    }

    private static object? Plain(object? value) => value switch
    {
        TimeSpan span => span.ToString("c"),
        _ => value,
    };

    /// <summary>The last configuration source that supplies this key (or any key beneath it), or null if none does.</summary>
    private string? SourceOf(string key)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return null;
        }

        foreach (var provider in root.Providers.Reverse())
        {
            var has = provider.TryGet(key, out _) || provider.GetChildKeys([], key).Any()
                || (key == "Urls" && provider.TryGet("urls", out _));
            if (!has && key.Contains(':'))
            {
                var parent = key[..key.LastIndexOf(':')];
                has = provider.GetChildKeys([], parent).Contains(key[(key.LastIndexOf(':') + 1)..], StringComparer.OrdinalIgnoreCase);
            }

            if (has)
            {
                return provider switch
                {
                    JsonConfigurationProvider json => $"file {Path.GetFileName(json.Source.Path)}",
                    Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider => "environment variable",
                    Microsoft.Extensions.Configuration.CommandLine.CommandLineConfigurationProvider => "command line",
                    Microsoft.Extensions.Configuration.Memory.MemoryConfigurationProvider => "set in code",
                    _ => provider.GetType().Name.Replace("ConfigurationProvider", ""),
                };
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?i)(password|pwd)\s*=\s*[^;]*")]
    private static partial Regex SecretInText();

    /// <summary>A connection string can carry a password; nothing that looks like one is shown.</summary>
    private static object? Redact(object? value) => value is string text ? SecretInText().Replace(text, "$1=***") : value;
}
