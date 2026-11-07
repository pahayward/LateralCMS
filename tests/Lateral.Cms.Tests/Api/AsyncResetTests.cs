using System.Net;
using Lateral.Cms.Ingestion;
using Lateral.Cms.Tests.Infrastructure;

namespace Lateral.Cms.Tests.Api;

public sealed class AsyncResetFactory : CmsFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new("Admin:AllowReset", "true"), new("Admin:ResetAllowedEnvironment", "Testing")];
}

/// <summary>The reset must take the queue with it, or the worker would restore what the reset just erased.</summary>
public class AsyncResetTests(AsyncResetFactory factory) : IClassFixture<AsyncResetFactory>
{
    private Task<HttpResponseMessage> ResetAsync() =>
        factory.Admin().PostAsync("/api/admin/reset", Evt.Raw("{\"confirm\":\"reset-all-data\"}"));

    [Fact]
    [Trait("req", "DV-02")]
    public async Task A_reset_empties_the_queue_so_nothing_comes_back_afterwards()
    {
        await ResetAsync();
        var applied = Evt.Id();
        var waiting = Evt.Id();
        await factory.AcceptAsync(Evt.Publish(applied, 1, 1));
        await factory.DrainAsync();
        var queued = CmsFactory.BatchId(await factory.AcceptAsync(Evt.Publish(waiting, 1, 1, new { title = "reset-sentinel" })));

        var response = await ResetAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var result = await response.ReadJsonAsync())
        {
            Assert.Equal(1, result.RootElement.GetProperty("entities").GetInt32());
            // Both batches go: the completed one and the one still waiting.
            Assert.Equal(2, result.RootElement.GetProperty("batches").GetInt32());
        }

        Assert.Equal(0, factory.Count("SELECT COUNT(*) FROM Batches"));
        Assert.Equal(0, factory.Count("SELECT COUNT(*) FROM Events"));

        // The worker finds nothing to apply, and the queued entity never appears.
        Assert.Equal(ProcessStep.Idle, await factory.StepAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await factory.Admin().GetEntityAsync(waiting)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.Cms().GetAsync($"/cms/batches/{queued}")).StatusCode);
        Assert.Equal(0, factory.Count("SELECT COUNT(*) FROM Entities"));
    }
}
