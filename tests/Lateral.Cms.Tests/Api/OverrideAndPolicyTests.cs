using System.Net;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Lateral.Cms.Tests.Api;

/// <summary>Two guarantees that are easy to lose in a refactor: what the admin override survives, and deny by default.</summary>
public class OverrideAndPolicyTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    [Fact]
    [Trait("req", "EV-15")]
    public async Task A_publish_never_clears_the_admin_override_but_a_delete_and_re_create_in_one_batch_does()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1));
        (await factory.Admin().PostAsync($"/api/entities/{id}/disable", null)).EnsureSuccessStatusCode();

        // Later versions and an unpublish/republish cycle leave it alone.
        await factory.SendAsync(Evt.Publish(id, 2, 2), Evt.UnPublish(id, 3, 3), Evt.Publish(id, 4, 4));
        using (var still = await (await factory.Admin().GetEntityAsync(id)).ReadJsonAsync())
        {
            Assert.True(still.RootElement.GetProperty("status").GetProperty("isDisabledByAdmin").GetBoolean());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await factory.User().GetEntityAsync(id)).StatusCode);

        // A delete followed by a re-create is a new entity, and starts with the override cleared. Here both happen in
        // one batch, so the same stored row is reused: the cleared state has to be written over the old one.
        await factory.SendAsync(Evt.Delete(id, 5), Evt.Publish(id, 5, 6));

        using var recreated = await (await factory.Admin().GetEntityAsync(id)).ReadJsonAsync();
        Assert.False(recreated.RootElement.GetProperty("status").GetProperty("isDisabledByAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await factory.User().GetEntityAsync(id)).StatusCode);
    }

    [Fact]
    [Trait("req", "EV-15")]
    public async Task A_delete_and_re_create_in_separate_batches_also_clears_the_override()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1));
        (await factory.Admin().PostAsync($"/api/entities/{id}/disable", null)).EnsureSuccessStatusCode();
        await factory.SendAsync(Evt.Delete(id, 2));
        await factory.SendAsync(Evt.Publish(id, 2, 3));

        using var recreated = await (await factory.Admin().GetEntityAsync(id)).ReadJsonAsync();
        Assert.False(recreated.RootElement.GetProperty("status").GetProperty("isDisabledByAdmin").GetBoolean());
    }

    [Fact]
    [Trait("req", "SE-01")]
    public async Task Deny_by_default_a_fallback_policy_requires_a_signed_in_caller_for_anything_without_its_own_policy()
    {
        var provider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var fallback = await provider.GetFallbackPolicyAsync();

        Assert.NotNull(fallback);
        Assert.Contains(fallback.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }
}
