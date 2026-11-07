using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Lateral.Cms.TestConsole.Tests;

/// <summary>The console holds live credentials and can act as an admin, so it does not start in a deployed environment by accident.</summary>
public class EnvironmentGuardTests
{
    private sealed class EnvironmentFactory(string environment, bool allow) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Console:CmsBaseUrl"] = "http://cms.test",
                ["Console:AllowNonDevelopment"] = allow ? "true" : "false",
            }));
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Outside_development_the_console_refuses_to_start_and_says_how_to_override(string environment)
    {
        using var factory = new EnvironmentFactory(environment, allow: false);

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);
        Assert.Contains("Console:AllowNonDevelopment", failure.ToString());
    }

    [Theory]
    [InlineData("Development", false)]
    [InlineData("Staging", true)]
    public void It_starts_in_development_or_when_told_to_on_purpose(string environment, bool allow)
    {
        using var factory = new EnvironmentFactory(environment, allow);

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }
}
