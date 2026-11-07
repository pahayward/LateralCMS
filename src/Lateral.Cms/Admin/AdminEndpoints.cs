using System.Security.Claims;
using Lateral.Cms.Auth;
using Lateral.Cms.Ingestion;
using Lateral.Cms.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Admin;

/// <summary>
/// Settings for the test-only reset. It is off unless switched on, and it cannot be switched on in Production.
/// </summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>
    /// When true, <c>POST /api/admin/reset</c> exists and lets an admin erase all data. When false (the default)
    /// the route is not mapped at all, so it answers 404 to everyone.
    /// </summary>
    public bool AllowReset { get; set; }

    /// <summary>
    /// Outside Development the reset needs a second, deliberate step: name this environment here (for example <c>Staging</c>). An
    /// environment that was merely not <c>Production</c> used to be enough, which made every unknown name a place to erase everything.
    /// </summary>
    public string? ResetAllowedEnvironment { get; set; }
}

/// <summary>Fails startup if the reset is enabled where it could destroy real data.</summary>
public sealed class AdminOptionsValidator(IHostEnvironment environment) : IValidateOptions<AdminOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminOptions options)
    {
        if (!options.AllowReset || environment.IsDevelopment())
        {
            return ValidateOptionsResult.Success;
        }

        if (environment.IsProduction())
        {
            return ValidateOptionsResult.Fail("Admin:AllowReset must not be enabled in the Production environment.");
        }

        return string.Equals(options.ResetAllowedEnvironment, environment.EnvironmentName, StringComparison.OrdinalIgnoreCase)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"Admin:AllowReset is only allowed in Development, or in '{environment.EnvironmentName}' if Admin:ResetAllowedEnvironment is set to '{environment.EnvironmentName}' on purpose.");
    }
}

public sealed record ResetRequest(string? Confirm);

public sealed record ResetResult(int Entities, int Tombstones, int LedgerRows, int Batches);

/// <summary>
/// Erases everything the service has stored, so a test can start again from nothing. Runs under the write gate
/// and in one transaction: it is all or nothing, and no batch can interleave with it. The queue is emptied too.
/// </summary>
public sealed class ResetService(CmsWriteDbContext db, WriteGate gate, TimeProvider time, ILogger<ResetService> logger)
{
    public async Task<ResetResult> ResetAsync(string admin, CancellationToken cancellationToken)
    {
        using var _ = await gate.AcquireAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The queue goes too: a batch left in it would bring the erased data back as soon as the worker next ran.
        var ledger = await db.Events.ExecuteDeleteAsync(cancellationToken);
        var batches = await db.Batches.ExecuteDeleteAsync(cancellationToken);
        var tombstones = await db.Tombstones.ExecuteDeleteAsync(cancellationToken);
        var entities = await db.Entities.ExecuteDeleteAsync(cancellationToken);

        db.Audit(time.GetUtcNow().UtcDateTime, admin, "admin.reset", "all-data", $"{entities} entities, {tombstones} tombstones, {ledger} ledger rows, {batches} batches");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        IngestionLog.AdminReset(logger, admin, entities, tombstones, ledger, batches);
        return new ResetResult(entities, tombstones, ledger, batches);
    }
}

public static class AdminEndpoints
{
    /// <summary>The exact text a caller must send, so a stray request cannot erase the data.</summary>
    public const string ConfirmationPhrase = "reset-all-data";

    public static IServiceCollection AddCmsAdmin(this IServiceCollection services)
    {
        services.AddOptions<AdminOptions>()
            .BindConfiguration(AdminOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AdminOptions>, AdminOptionsValidator>();

        services.AddScoped<ResetService>();
        return services;
    }

    public static IEndpointRouteBuilder MapAdminEndpoints(this WebApplication app)
    {
        // Read once the host is built, so configuration supplied by a test or a deployment is honoured.
        if (!app.Services.GetRequiredService<IOptions<AdminOptions>>().Value.AllowReset)
        {
            return app;
        }

        app.MapPost("/api/admin/reset", ResetAsync)
            .RequireAuthorization(Policies.AdminOnly)
            .WithName("ResetAllData");

        IngestionLog.ResetEnabled(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Lateral.Cms.Admin"));
        return app;
    }

    private static async Task<IResult> ResetAsync(
        ResetRequest? request, ClaimsPrincipal user, ResetService reset, CancellationToken cancellationToken)
    {
        if (!string.Equals(request?.Confirm, ConfirmationPhrase, StringComparison.Ordinal))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: $"Send {{\"confirm\":\"{ConfirmationPhrase}\"}} to erase all data. Nothing was changed.",
                extensions: new Dictionary<string, object?> { ["code"] = "confirmation_required" });
        }

        return Results.Ok(await reset.ResetAsync(user.Identity?.Name ?? "unknown", cancellationToken));
    }
}
