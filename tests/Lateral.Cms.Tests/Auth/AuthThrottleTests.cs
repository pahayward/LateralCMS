using System.Net;
using Lateral.Cms.Auth;
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
    public async Task A_blocked_address_cannot_lock_out_the_CMS_whose_secret_cannot_be_guessed()
    {
        await FailThreeTimesThenTryAgain(_factory.ClientFor(CmsFactory.UserName, "wrong-password-123"));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _factory.User().GetAsync("/api/entities")).StatusCode);

        // The same address, with the CMS account's right credential: through (a 404 for a batch that does not exist, not a 429).
        var cms = await _factory.Cms().GetAsync($"/cms/batches/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, cms.StatusCode);

        // The CMS account's WRONG password gets no such pass.
        var wrong = await _factory.ClientFor(CmsFactory.CmsUser, "00000000-0000-0000-0000-000000000009").GetAsync($"/cms/batches/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.TooManyRequests, wrong.StatusCode);
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

public class AuthThrottleUnitTests
{
    private static (AuthFailureThrottle Throttle, ManualTimeProvider Time) Make(int max = 3)
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = Microsoft.Extensions.Options.Options.Create(new AuthOptions { MaxFailuresPerMinute = max });
        return (new AuthFailureThrottle(options, time), time);
    }

    [Fact]
    public void Blocks_at_the_limit_and_says_how_long()
    {
        var (throttle, _) = Make();
        for (var i = 0; i < 3; i++)
        {
            Assert.False(throttle.IsBlocked("a", out _));
            throttle.RecordFailure("a");
        }

        Assert.True(throttle.IsBlocked("a", out var wait));
        Assert.InRange(wait, TimeSpan.FromSeconds(59), TimeSpan.FromSeconds(60));
        Assert.False(throttle.IsBlocked("b", out _));
    }

    [Fact]
    public void A_new_window_starts_counting_from_one_even_after_the_old_one_was_looked_at()
    {
        var (throttle, time) = Make();
        for (var i = 0; i < 3; i++)
        {
            throttle.RecordFailure("a");
        }

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(throttle.IsBlocked("a", out _)); // expired
        throttle.RecordFailure("a");
        throttle.RecordFailure("a");
        Assert.False(throttle.IsBlocked("a", out _)); // two in the new window, not three
        throttle.RecordFailure("a");
        Assert.True(throttle.IsBlocked("a", out _));
    }

    [Fact]
    public void A_limit_of_zero_switches_the_throttle_off()
    {
        var (throttle, _) = Make(0);
        for (var i = 0; i < 50; i++)
        {
            throttle.RecordFailure("a");
        }

        Assert.False(throttle.IsBlocked("a", out _));
    }

    [Fact]
    public async Task Concurrent_failures_are_all_counted()
    {
        var (throttle, _) = Make(1000);
        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => throttle.RecordFailure("a"))));
        for (var i = 0; i < 799; i++)
        {
            throttle.RecordFailure("a");
        }

        Assert.False(throttle.IsBlocked("a", out _)); // 999
        throttle.RecordFailure("a");
        Assert.True(throttle.IsBlocked("a", out _)); // 1000
    }
}
