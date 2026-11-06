using System.Net;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lateral.Cms.Tests.Api;

/// <summary>The size limits on the webhook, with deliberately small limits configured.</summary>
[Trait("req", "IN-06")]
[Trait("req", "ST-07")]
public class IngestionLimitsTests : IClassFixture<IngestionLimitsTests.SmallLimitsFactory>
{
    public sealed class SmallLimitsFactory : CmsFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
        [
            new("Ingestion:MaxBatchSize", "3"),
            new("Ingestion:MaxBodyBytes", "2000"),
            new("Ingestion:MaxPayloadBytes", "200"),
            new("Ingestion:MaxJsonDepth", "8"),
        ];
    }

    private readonly SmallLimitsFactory _factory;

    public IngestionLimitsTests(SmallLimitsFactory factory) => _factory = factory;

    [Fact]
    public async Task A_batch_at_the_limit_is_accepted_and_one_over_is_refused()
    {
        var cms = _factory.Cms();

        using var ok = await _factory.SendAsync(Evt.Publish(Evt.Id(), 1, 1), Evt.Publish(Evt.Id(), 1, 1), Evt.Publish(Evt.Id(), 1, 1));
        var tooMany = await cms.PostAsync(Evt.Publish(Evt.Id(), 1, 1), Evt.Publish(Evt.Id(), 1, 1), Evt.Publish(Evt.Id(), 1, 1), Evt.Publish(Evt.Id(), 1, 1));

        Assert.Equal(3, ok.RootElement.GetProperty("received").GetInt32());
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooMany.StatusCode);
        using var problem = await tooMany.ReadJsonAsync();
        Assert.Equal("batch_too_large", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_oversized_body_is_refused_before_it_is_parsed()
    {
        var huge = Evt.Raw("[" + new string(' ', 5000) + "]");

        var response = await _factory.Cms().PostAsync("/cms/events", huge);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var problem = await response.ReadJsonAsync();
        Assert.Equal("payload_too_large", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_declared_ten_gigabyte_body_is_refused_without_being_read()
    {
        // The sender declares 10 GiB, sends two bytes and then stalls. Answering at all proves the server judged the
        // request by its declared length and did not wait for (or buffer) the body.
        using var stalled = new StalledContent(10L * 1024 * 1024 * 1024);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // No redirect handler: it would copy the request body so it could replay it, and this one never ends.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = CmsFactory.Basic(CmsFactory.CmsUser, CmsFactory.CmsPassword);

        var response = await client.PostAsync("/cms/events", stalled, timeout.Token);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var problem = await response.ReadJsonAsync();
        Assert.Equal("payload_too_large", problem.RootElement.GetProperty("code").GetString());
    }

    /// <summary>Declares <c>length</c> bytes, writes two, then never finishes.</summary>
    private sealed class StalledContent(long length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await stream.WriteAsync("[ "u8.ToArray(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        protected override bool TryComputeLength(out long computed)
        {
            computed = length;
            return true;
        }
    }

    [Fact]
    public async Task An_oversized_payload_rejects_that_event_only()
    {
        var big = new { text = new string('x', 500) };

        using var result = await _factory.SendAsync(Evt.Publish(Evt.Id(), 1, 1, big), Evt.Publish(Evt.Id(), 1, 1));

        Assert.Equal(["rejected", "applied"], result.Outcomes());
        Assert.Equal(["payload_too_large", ""], result.Reasons());
    }

    [Fact]
    public async Task Excessively_nested_json_is_refused()
    {
        var nested = new string('[', 50) + new string(']', 50);

        var response = await _factory.Cms().PostAsync("/cms/events", Evt.Raw(nested));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
