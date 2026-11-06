using System.Reflection;
using Lateral.Cms.Domain;

namespace Lateral.Cms.Tests.Domain;

/// <summary>The rules must stay free of persistence and web types, so they can be tested with nothing but the BCL.</summary>
public class DomainArchitectureTests
{
    [Fact]
    [Trait("req", "EV-09")]
    public void Domain_types_use_nothing_from_entity_framework_or_asp_net()
    {
        var domainTypes = typeof(Reducer).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(Reducer).Namespace && !t.Name.StartsWith('<'))
            .ToList();
        Assert.NotEmpty(domainTypes);

        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in domainTypes)
        {
            var used = type.GetFields(All).Select(f => f.FieldType)
                .Concat(type.GetProperties(All).Select(p => p.PropertyType))
                .Concat(type.GetMethods(All).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)))
                .Concat(type.GetConstructors(All).SelectMany(c => c.GetParameters().Select(p => p.ParameterType)))
                .Concat(new[] { type.BaseType ?? typeof(object) }.Concat(type.GetInterfaces()));

            foreach (var used1 in used)
            {
                var ns = used1.Namespace ?? "";
                Assert.False(
                    ns.StartsWith("Microsoft", StringComparison.Ordinal) || ns.StartsWith("System.Data", StringComparison.Ordinal),
                    $"{type.Name} uses {used1.FullName}");
            }
        }
    }

    [Fact]
    [Trait("req", "EV-09")]
    public void Domain_source_files_import_no_microsoft_namespaces()
    {
        var folder = Path.Combine(RepositoryRoot(), "src", "Lateral.Cms", "Domain");
        var files = Directory.GetFiles(folder, "*.cs");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var offending = File.ReadAllLines(file).Where(l => l.TrimStart().StartsWith("using Microsoft", StringComparison.Ordinal));
            Assert.Empty(offending);
        }
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lateral.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Lateral.sln not found above the test binaries.");
    }
}
