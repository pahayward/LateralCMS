using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Lateral.Cms.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Auth;

public sealed record LoginRequest(string? Username, string? Password);

public sealed record ChangePasswordRequest(string? Current, string? NewPassword);

public sealed record CreateUserRequest(string? Username, string? Role, string? Password);

public sealed record UpdateUserRequest(string? Role, bool? Disabled);

public sealed record ResetPasswordRequest(string? Password, bool? MustChange);

/// <summary>How a person looks on the wire. Never includes the hash or the stamp.</summary>
public sealed record UserView(
    string Username, string Role, string Source, bool Disabled, bool MustChangePassword,
    DateTime? CreatedAt, DateTime? PasswordChangedAt, DateTime? LastSignInAt)
{
    public static UserView From(UserSnapshot person) =>
        new(person.Username, person.Role, "database", person.Disabled, person.MustChangePassword, person.CreatedAt, person.PasswordChangedAt, person.LastSignInAt);
}

/// <summary>
/// Signing in and out, who am I, changing your own password, and the administrator's user management. People are in the database;
/// the CMS service account and any <c>Auth:Users</c> are configuration, listed read-only.
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", LoginAsync).AllowAnonymous().WithName("SignIn");
        app.MapPost("/api/auth/logout", LogoutAsync).WithName("SignOut");
        app.MapGet("/api/auth/me", Me).WithName("WhoAmI");
        app.MapPost("/api/auth/password", ChangeOwnPasswordAsync).WithName("ChangeOwnPassword");

        var admin = app.MapGroup("/api/admin/users").RequireAuthorization(Policies.AdminOnly);
        admin.MapGet("", ListAsync).WithName("ListUsers");
        admin.MapPost("", CreateAsync).WithName("CreateUser");
        admin.MapPatch("{username}", UpdateAsync).WithName("UpdateUser");
        admin.MapPost("{username}/password", ResetPasswordAsync).WithName("ResetUserPassword");
        admin.MapPost("{username}/revoke-sessions", RevokeAsync).WithName("RevokeUserSessions");
        admin.MapDelete("{username}", DeleteAsync).WithName("DeleteUser");
        return app;
    }

    // ---------------------------------------------------------------- sign in and out

    private static async Task<IResult> LoginAsync(
        LoginRequest? request, HttpContext http, CredentialStore store, AccountService accounts, AuthFailureThrottle throttle,
        ILogger<AccountService> logger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request?.Username) || string.IsNullOrEmpty(request.Password))
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_request", "Send a username and a password.");
        }

        // Signing in is not something another site may do on the visitor's behalf (it would put them in the attacker's account).
        // Browsers say where a request came from; scripts and curl send nothing, and are unaffected.
        if (http.Request.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site")
        {
            return Problem(StatusCodes.Status403Forbidden, "cross_site", "Sign in from the page itself, not from another site.");
        }

        // Counted per address and per username, so one address guessing at many accounts and many addresses guessing at one
        // account are both slowed. (In memory, per instance: a brake, not a lock.)
        var client = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var byName = "user:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Username.ToUpperInvariant())));
        if (throttle.IsBlocked(client, out var wait) | throttle.IsBlocked(byName, out var waitForName))
        {
            http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(Math.Max(wait.TotalSeconds, waitForName.TotalSeconds))).ToString();
            return Problem(StatusCodes.Status429TooManyRequests, "too_many_attempts", "Too many failed sign-ins. Wait a moment and try again.");
        }

        ClaimsPrincipal? principal = null;
        var configured = store.Validate(request.Username, request.Password);
        if (configured is not null && !configured.IsInRole(Roles.CmsIngest))
        {
            principal = new ClaimsPrincipal(new ClaimsIdentity(configured.Claims, AuthExtensions.SessionScheme));
        }
        else if (configured is null && await accounts.VerifyAsync(request.Username, request.Password, cancellationToken) is { } person)
        {
            await accounts.RecordSignInAsync(person.Id, cancellationToken);
            principal = AccountClaims.Principal(person, AuthExtensions.SessionScheme);
        }

        if (principal is null)
        {
            throttle.RecordFailure(client);
            throttle.RecordFailure(byName);
            // Not the username: it is text an attacker chose.
            logger.LogWarning("A sign-in failed from {Client}", client);
            return Problem(StatusCodes.Status401Unauthorized, "invalid_credentials", "The username or password is not right.");
        }

        await http.SignInAsync(AuthExtensions.SessionScheme, principal, new AuthenticationProperties { IsPersistent = false });
        return Results.Ok(Describe(principal, AuthExtensions.SessionScheme));
    }

    private static async Task LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(AuthExtensions.SessionScheme);
        http.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static IResult Me(ClaimsPrincipal user) => Results.Ok(Describe(user, user.Identity?.AuthenticationType ?? ""));

    private static object Describe(ClaimsPrincipal user, string via) => new
    {
        username = user.Identity?.Name,
        role = user.FindFirst(ClaimTypes.Role)?.Value,
        source = user.FindFirst(AccountClaims.Source)?.Value,
        mustChangePassword = user.HasClaim(AccountClaims.MustChange, "true"),
        via = via == AuthExtensions.SessionScheme ? "session" : "basic",
    };

    private static async Task<IResult> ChangeOwnPasswordAsync(
        ChangePasswordRequest? request, HttpContext http, ClaimsPrincipal user, AccountService accounts,
        InitialPasswordFile initial, CancellationToken cancellationToken)
    {
        if (user.FindFirst(AccountClaims.Source)?.Value != "database" || !Guid.TryParse(user.FindFirst(AccountClaims.UserId)?.Value, out var id))
        {
            return Problem(
                StatusCodes.Status409Conflict, "managed_in_configuration",
                "This account is defined in configuration, so its password is changed there.");
        }

        var change = await accounts.ChangeOwnPasswordAsync(id, request?.Current ?? "", request?.NewPassword ?? "", cancellationToken);
        if (!change.Ok)
        {
            return Failure(change);
        }

        initial.DeleteIfFor(change.User!.Username);

        // A session carries on under the new password; a script using Basic credentials simply starts using the new one.
        if (user.Identity?.AuthenticationType == AuthExtensions.SessionScheme)
        {
            await http.SignInAsync(
                AuthExtensions.SessionScheme, AccountClaims.Principal(change.User, AuthExtensions.SessionScheme), new AuthenticationProperties { IsPersistent = false });
        }

        return Results.Ok(new { username = change.User.Username, mustChangePassword = false });
    }

    // ---------------------------------------------------------------- administration

    private static async Task<IResult> ListAsync(AccountService accounts, CredentialStore store, CancellationToken cancellationToken)
    {
        var people = (await accounts.ListAsync(cancellationToken)).Select(UserView.From);
        var configured = store.ConfiguredAccounts.Select(a =>
            new UserView(a.Username, a.Role, "config", false, false, null, null, null));
        return Results.Ok(new { users = configured.Concat(people).ToArray() });
    }

    private static async Task<IResult> CreateAsync(
        CreateUserRequest? request, ClaimsPrincipal admin, AccountService accounts, CancellationToken cancellationToken)
    {
        var change = await accounts.CreateAsync(
            Actor(admin), request?.Username ?? "", request?.Role ?? "", request?.Password, cancellationToken);
        return change.Ok
            ? Results.Json(new { user = UserView.From(change.User!), temporaryPassword = change.TemporaryPassword }, statusCode: StatusCodes.Status201Created)
            : Failure(change);
    }

    private static async Task<IResult> UpdateAsync(
        string username, UpdateUserRequest? request, ClaimsPrincipal admin, AccountService accounts, CancellationToken cancellationToken)
    {
        if (request is null || (request.Role is null && request.Disabled is null))
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_request", "Send a role, disabled, or both.");
        }

        AccountChange? last = null;
        if (request.Role is not null)
        {
            last = await accounts.SetRoleAsync(Actor(admin), username, request.Role, cancellationToken);
            if (!last.Ok)
            {
                return Failure(last);
            }
        }

        if (request.Disabled is { } disabled)
        {
            last = await accounts.SetDisabledAsync(Actor(admin), username, disabled, cancellationToken);
            if (!last.Ok)
            {
                return Failure(last);
            }
        }

        return Results.Ok(new { user = UserView.From(last!.User!) });
    }

    private static async Task<IResult> ResetPasswordAsync(
        string username, ResetPasswordRequest? request, ClaimsPrincipal admin, AccountService accounts, InitialPasswordFile initial,
        CancellationToken cancellationToken)
    {
        var change = await accounts.ResetPasswordAsync(Actor(admin), username, request?.Password, request?.MustChange, cancellationToken);
        if (!change.Ok)
        {
            return Failure(change);
        }

        initial.DeleteIfFor(change.User!.Username);
        return Results.Ok(new { user = UserView.From(change.User!), temporaryPassword = change.TemporaryPassword });
    }

    private static async Task<IResult> RevokeAsync(
        string username, ClaimsPrincipal admin, AccountService accounts, CancellationToken cancellationToken)
    {
        var change = await accounts.RevokeSessionsAsync(Actor(admin), username, cancellationToken);
        return change.Ok ? Results.Ok(new { user = UserView.From(change.User!) }) : Failure(change);
    }

    private static async Task<IResult> DeleteAsync(
        string username, ClaimsPrincipal admin, AccountService accounts, CancellationToken cancellationToken)
    {
        var change = await accounts.DeleteAsync(Actor(admin), username, cancellationToken);
        return change.Ok ? Results.NoContent() : Failure(change);
    }

    private static string Actor(ClaimsPrincipal user) => user.Identity?.Name ?? "unknown";

    private static IResult Failure(AccountChange change) => change.Problem switch
    {
        AccountProblem.NotFound => Problem(StatusCodes.Status404NotFound, "not_found", change.Message!),
        AccountProblem.Exists => Problem(StatusCodes.Status409Conflict, "username_taken", change.Message!),
        AccountProblem.LastAdmin => Problem(StatusCodes.Status409Conflict, "last_administrator", change.Message!),
        AccountProblem.Yourself => Problem(StatusCodes.Status409Conflict, "not_yourself", change.Message!),
        AccountProblem.WrongPassword => Problem(StatusCodes.Status400BadRequest, "wrong_password", change.Message!),
        _ => Problem(StatusCodes.Status400BadRequest, "invalid_request", change.Message!),
    };

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}

/// <summary>
/// The first administrator's temporary password, written once to a file beside the database (readable only by the account running
/// the service) and not to the log. It is deleted as soon as that password has been replaced. Reading it needs access to the
/// machine, which is the same authority as the command-line tool.
/// </summary>
public sealed class InitialPasswordFile(DatabaseConnections database)
{
    public const string FileName = "initial-admin-password.txt";

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(database.DataSource)!, FileName);

    public void Write(string username, string password)
    {
        // Created private from the first byte (on Unix), not written readable and then locked down.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var writer = new StreamWriter(Path, options);
        writer.Write(
            $"Lateral CMS first-run administrator{Environment.NewLine}username: {username}{Environment.NewLine}password: {password}{Environment.NewLine}"
            + $"Sign in and choose a new password; this file is deleted when you do.{Environment.NewLine}");
    }

    /// <summary>Removes the file if it names this user.</summary>
    public void DeleteIfFor(string username)
    {
        try
        {
            if (File.Exists(Path) && File.ReadLines(Path).Skip(1).FirstOrDefault() == $"username: {username}")
            {
                File.Delete(Path);
            }
        }
        catch (IOException)
        {
            // Best effort: it is a convenience file, and the password in it is already dead once it has been replaced.
        }
    }
}

public static class AccountBootstrap
{
    /// <summary>
    /// If nobody can sign in (no people in the database, none in configuration), makes the first administrator with a temporary
    /// password and tells the operator where to find it. The password is not logged.
    /// </summary>
    public static async Task RunAsync(WebApplication app)
    {
        var accounts = app.Services.GetRequiredService<AccountService>();
        var created = await accounts.BootstrapAsync(CancellationToken.None);
        if (created is null)
        {
            return;
        }

        var (username, password) = created.Value;
        var file = app.Services.GetRequiredService<InitialPasswordFile>();
        file.Write(username, password);
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Lateral.Cms.Auth").LogWarning(
            "No accounts existed, so the administrator '{Username}' was created with a temporary password. It is in {File} (readable by this service's account only); sign in and choose a new one",
            username, file.Path);
    }
}
