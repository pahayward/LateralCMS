using System.Security.Claims;
using Lateral.Cms.Auth;

namespace Lateral.Cms.Entities;

public static class EntityEndpoints
{
    public static IServiceCollection AddCmsEntities(this IServiceCollection services)
    {
        services.AddOptions<ApiOptions>()
            .BindConfiguration(ApiOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<EntityQueries>();
        services.AddScoped<EntityAdminService>();
        return services;
    }

    public static IEndpointRouteBuilder MapEntityEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/entities").RequireAuthorization(Policies.ApiRead);

        api.MapGet("", ListAsync).WithName("ListEntities");
        api.MapGet("{id}", GetAsync).WithName("GetEntity");

        // There is deliberately no way to change an entity's content through this API.
        api.MapPost("{id}/disable", (string id, ClaimsPrincipal user, EntityAdminService admin, CancellationToken ct) =>
                SetDisabledAsync(id, true, user, admin, ct))
            .RequireAuthorization(Policies.AdminOnly)
            .WithName("DisableEntity");
        api.MapPost("{id}/enable", (string id, ClaimsPrincipal user, EntityAdminService admin, CancellationToken ct) =>
                SetDisabledAsync(id, false, user, admin, ct))
            .RequireAuthorization(Policies.AdminOnly)
            .WithName("EnableEntity");

        return app;
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user, EntityQueries queries, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (!EntityQueries.TryDecodeCursor(cursor, out var afterId))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "The cursor is not valid. Use the nextCursor value returned by a previous page.",
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_cursor" });
        }

        return Results.Ok(await queries.ListAsync(user.IsInRole(Roles.Admin), afterId, limit, cancellationToken));
    }

    private static async Task<IResult> GetAsync(
        string id, ClaimsPrincipal user, EntityQueries queries, CancellationToken cancellationToken)
    {
        // Hidden and missing look identical, so the API does not confirm that a restricted entity exists.
        var entity = await queries.GetAsync(user.IsInRole(Roles.Admin), id, cancellationToken);
        return entity is null ? NotFound() : Results.Ok(entity);
    }

    private static async Task<IResult> SetDisabledAsync(
        string id, bool disabled, ClaimsPrincipal user, EntityAdminService admin, CancellationToken cancellationToken)
    {
        var state = await admin.SetDisabledAsync(id, disabled, user.Identity?.Name ?? "unknown", cancellationToken);
        return state is null ? NotFound() : Results.Ok(state);
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "No such entity.");
}
