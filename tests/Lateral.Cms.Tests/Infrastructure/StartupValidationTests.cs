using Microsoft.AspNetCore.Hosting;

namespace Lateral.Cms.Tests.Infrastructure;

/// <summary>Guideline 7.4: invalid settings stop startup with a message that names the setting.</summary>
[Trait("req", "SE-06")]
[Trait("req", "IN-02")]
[Trait("req", "ST-01")]
public class StartupValidationTests
{
    private sealed class MisconfiguredFactory(string key, string value) : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new(key, value)];
    }

    private static string AllMessages(Exception? exception)
    {
        var messages = new List<string>();
        var pending = new Stack<Exception>();
        if (exception is not null)
        {
            pending.Push(exception);
        }

        while (pending.TryPop(out var current))
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } next)
            {
                pending.Push(next);
            }
        }

        return string.Join("\n", messages);
    }

    [Theory]
    [InlineData("Auth:Cms:Username", "short", "Auth:Cms:Username")]
    [InlineData("Auth:Cms:Passwords:0", "not-a-guid", "Auth:Cms:Passwords:0")]
    [InlineData("Auth:Users:0:Role", "Root", "Auth:Users:0:Role")]
    [InlineData("Ingestion:ChecksumAlgorithm", "crc32", "ChecksumAlgorithm")]
    [InlineData("Ingestion:MaxBatchSize", "0", "MaxBatchSize")]
    [InlineData("Api:MaxPageSize", "0", "MaxPageSize")]
    [InlineData("Inbox:MaxAttempts", "0", "MaxAttempts")]
    [InlineData("Inbox:PollInterval", "00:00:00", "PollInterval")]
    [InlineData("Inbox:MaxBytes", "1000", "Inbox:MaxBytes")] // smaller than one maximal request could never be stored
    [InlineData("Inbox:RetryMaxDelay", "00:00:01", "RetryMaxDelay")] // shorter than the base delay
    public void An_invalid_setting_stops_startup_and_names_itself(string key, string value, string expectedInMessage)
    {
        using var factory = new MisconfiguredFactory(key, value);

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);
        Assert.Contains(expectedInMessage, AllMessages(failure));
    }

    private sealed class ProductionResetFactory : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new("Admin:AllowReset", "true")];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
        }
    }

    [Fact]
    public void The_reset_cannot_be_switched_on_in_production()
    {
        using var factory = new ProductionResetFactory();

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);
        Assert.Contains("Admin:AllowReset", AllMessages(failure));
    }

    [Fact]
    public void Missing_credentials_stop_startup()
    {
        // No Auth settings at all, as in a deployment that forgot to supply them.
        using var factory = new MisconfiguredFactory("Auth:Cms:Username", "") ;

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);
        Assert.Contains("Auth:Cms:Username", AllMessages(failure));
    }
}
