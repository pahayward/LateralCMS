using Lateral.Cms.Auth;
using Lateral.Cms.Persistence;

namespace Lateral.Cms.Admin;

/// <summary>Read-only views for the administrator: the effective configuration and where data is kept.</summary>
public static class AdminInfoEndpoints
{
    public static IServiceCollection AddCmsAdminInfo(this IServiceCollection services)
    {
        services.AddSingleton<SettingsReport>();
        services.AddSingleton<StorageReport>();
        return services;
    }

    public static IEndpointRouteBuilder MapAdminInfoEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/settings", (SettingsReport report) => Results.Ok(report.Build()))
            .RequireAuthorization(Policies.AdminOnly)
            .WithName("ShowSettings");

        app.MapGet("/api/admin/storage", async (StorageReport report, CmsReadDbContext db, CancellationToken cancellationToken) =>
                Results.Ok(await report.BuildAsync(db, cancellationToken)))
            .RequireAuthorization(Policies.AdminOnly)
            .WithName("ShowStorage");

        return app;
    }
}
