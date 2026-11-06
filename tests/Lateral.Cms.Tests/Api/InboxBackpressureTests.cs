using System.Net;
using Lateral.Cms.Tests.Infrastructure;

namespace Lateral.Cms.Tests.Api;

/// <summary>A small inbox, so a few batches fill it.</summary>
public sealed class SmallInboxFactory : CmsFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
    [
        new("Ingestion:MaxBodyBytes", "4000"),
        new("Inbox:MaxBytes", "6000"),
    ];
}

/// <summary>
/// A slow worker must push back on senders rather than let the queue grow without limit, and must do it
/// all-or-nothing so the sender can simply try again.
/// </summary>
[Trait("req", "AS-05")]
public class InboxBackpressureTests(SmallInboxFactory factory) : IClassFixture<SmallInboxFactory>
{
    // About 1.4 KB each, so two fit in a request and about four fit in the inbox.
    private static object Big(string id) => Evt.Publish(id, 1, 1, new { title = new string('x', 1400) });

    private static object[] Pair() => [Big(Evt.Id()), Big(Evt.Id())];

    [Fact]
    public async Task A_full_inbox_refuses_with_429_and_stores_nothing_of_the_refused_batch()
    {
        await factory.DrainAsync();
        await factory.AcceptAsync(Pair());
        await factory.AcceptAsync(Pair());
        var queuedBefore = factory.Count("SELECT COUNT(*) FROM Batches WHERE Status <> 'completed'");
        var ledgerBefore = factory.Count("SELECT COUNT(*) FROM Events");

        // A rejected event in the refused batch must not be recorded either: the whole request is declined.
        var refused = await factory.Cms().PostAsync([.. Pair(), new { type = "publish", id = Evt.Id(), version = 0, payload = new { }, timestamp = Evt.T(1) }]);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
        using (var body = await refused.ReadJsonAsync())
        {
            Assert.Equal("inbox_full", body.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(queuedBefore, factory.Count("SELECT COUNT(*) FROM Batches WHERE Status <> 'completed'"));
        Assert.Equal(ledgerBefore, factory.Count("SELECT COUNT(*) FROM Events"));
        Assert.Contains(factory.Logs.Entries, e => e.Message.Contains("the queue holds"));
    }

    [Fact]
    public async Task Once_the_worker_has_made_room_the_same_batch_is_accepted()
    {
        await factory.DrainAsync();
        await factory.AcceptAsync(Pair());
        await factory.AcceptAsync(Pair());
        object[] batch = Pair();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await factory.Cms().PostAsync(batch)).StatusCode);

        await factory.DrainAsync();

        Assert.Equal(HttpStatusCode.Accepted, (await factory.Cms().PostAsync(batch)).StatusCode);
        await factory.DrainAsync();
    }

    [Fact]
    public async Task Senders_arriving_together_cannot_squeeze_past_the_limit()
    {
        await factory.DrainAsync();

        var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            (await factory.Cms().PostAsync(Pair())).StatusCode));

        // The inbox holds a little over four events' worth: two full pairs fit, a third pair does not.
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.Accepted));
        Assert.Equal(6, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.True(factory.Count("SELECT COALESCE(SUM(PayloadBytes), 0) FROM Batches WHERE Status <> 'completed'") <= 6000);
        await factory.DrainAsync();
    }

    [Fact]
    public async Task Applying_batches_frees_the_space_they_used()
    {
        await factory.DrainAsync();
        await factory.AcceptAsync(Pair());
        Assert.True(factory.Count("SELECT COALESCE(SUM(PayloadBytes), 0) FROM Batches WHERE Status <> 'completed'") > 2000);

        await factory.DrainAsync();

        Assert.Equal(0, factory.Count("SELECT COALESCE(SUM(PayloadBytes), 0) FROM Batches WHERE Status <> 'completed'"));
    }
}
