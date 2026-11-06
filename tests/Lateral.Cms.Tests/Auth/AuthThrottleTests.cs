using System.Net;
using Lateral.Cms.Tests.Infrastructure;

namespace Lateral.Cms.Tests.Auth;

[Trait("req", "SE-05")]
public class AuthThrottleTests : IClassFixture<AuthThrottleTests.ThrottledFactory>
{
    public sealed class ThrottledFactory : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
            [new("Auth:MaxFailuresPerMinute", "3")];
    }

    private readonly ThrottledFactory _factory;

    public AuthThrottleTests(ThrottledFactory factory)
    {
        _factory = factory;

        // The factory (and its throttle) is shared by the tests of this class; let any earlier test's window expire.
        _factory.Time.Advance(TimeSpan.FromMinutes(5));
    }

    private async Task<HttpResponseMessage> FailThreeTimesThenTryAgain(HttpClient attacker)
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.GetAsync("/api/entities")).StatusCode);
        }

        return await attacker.GetAsync("/api/entities");
    }

    [Fact]
    public async Task Repeated_failures_from_one_client_are_throttled_with_retry_after()
    {
        var throttled = await FailThreeTimesThenTryAgain(_factory.ClientFor(CmsFactory.UserName, "wrong-password-123"));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);

        // Even the right password is refused for the rest of the window, so guessing gets no feedback.
        var correct = await _factory.User().GetAsync("/api/entities");
        Assert.Equal(HttpStatusCode.TooManyRequests, correct.StatusCode);
    }

    [Fact]
    public async Task The_block_lifts_once_the_window_has_passed()
    {
        await FailThreeTimesThenTryAgain(_factory.ClientFor(CmsFactory.UserName, "wrong-password-123"));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _factory.User().GetAsync("/api/entities")).StatusCode);

        _factory.Time.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(HttpStatusCode.OK, (await _factory.User().GetAsync("/api/entities")).StatusCode);
    }

    [Fact]
    public async Task Retry_after_counts_down_with_the_clock()
    {
        await FailThreeTimesThenTryAgain(_factory.ClientFor(CmsFactory.UserName, "wrong-password-123"));
        var first = (await _factory.User().GetAsync("/api/entities")).Headers.RetryAfter!.Delta!.Value;

        _factory.Time.Advance(TimeSpan.FromSeconds(30));
        var later = (await _factory.User().GetAsync("/api/entities")).Headers.RetryAfter!.Delta!.Value;

        Assert.True(later < first, $"{later} should be shorter than {first}");
    }
}
