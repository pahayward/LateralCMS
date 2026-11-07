using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Lateral.Cms.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Auth;

/// <summary>What a person looks like to the rest of the service: everything about the account except the password hash.</summary>
public sealed record UserSnapshot(
    Guid Id, string Username, string Role, bool MustChangePassword, bool Disabled, Guid Stamp,
    DateTime CreatedAt, DateTime PasswordChangedAt, DateTime? LastSignInAt)
{
    public static UserSnapshot From(UserRow row) =>
        new(row.Id, row.Username, row.Role, row.MustChangePassword, row.Disabled, row.SecurityStamp, row.CreatedAt, row.PasswordChangedAt, row.LastSignInAt);
}

public enum AccountProblem
{
    None,
    NotFound,
    Exists,
    Invalid,
    WrongPassword,
    LastAdmin,
    Yourself,
}

/// <summary>The outcome of a change to an account. <see cref="TemporaryPassword"/> is set once, when the service chose the password.</summary>
public sealed record AccountChange(AccountProblem Problem, string? Message = null, UserSnapshot? User = null, string? TemporaryPassword = null)
{
    public bool Ok => Problem == AccountProblem.None;

    public static AccountChange Fail(AccountProblem problem, string message) => new(problem, message);
}

/// <summary>
/// The people who sign in: creating them, checking a password, changing or resetting it, disabling and deleting. Every change is
/// made under the write gate and saved in one transaction with its audit record, so a change and its record exist together or not
/// at all. Nothing secret is ever written to the audit log or to the log.
/// </summary>
public sealed class AccountService
{
    private const int MaxVerifiedCredentials = 1_000;

    private readonly IServiceScopeFactory _scopes;
    private readonly WriteGate _gate;
    private readonly IOptions<AuthOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AccountService> _logger;
    private readonly Lazy<string> _decoy;

    private readonly ConcurrentDictionary<Guid, (UserSnapshot User, DateTime Expires)> _byId = new();
    private readonly ConcurrentDictionary<string, (Guid Id, Guid Stamp, DateTime Expires)> _verified = new();
    private readonly byte[] _cacheKey = RandomNumberGenerator.GetBytes(32);

    public AccountService(
        IServiceScopeFactory scopes, WriteGate gate, IOptions<AuthOptions> options, TimeProvider time, ILogger<AccountService> logger)
    {
        _scopes = scopes;
        _gate = gate;
        _options = options;
        _time = time;
        _logger = logger;
        _decoy = new Lazy<string>(() => PasswordHasher.Decoy(Iterations));
    }

    private AuthOptions Settings => _options.Value;

    private int Iterations => Settings.PasswordHashIterations;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ---------------------------------------------------------------- reading

    /// <summary>The person with this id, as the cache last saw them (see <see cref="AuthOptions.UserCacheSeconds"/>).</summary>
    public async Task<UserSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var ttl = TimeSpan.FromSeconds(Settings.UserCacheSeconds);
        if (ttl > TimeSpan.Zero && _byId.TryGetValue(id, out var cached) && cached.Expires > Now)
        {
            return cached.User;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsReadDbContext>();
        var row = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (row is null)
        {
            _byId.TryRemove(id, out _);
            return null;
        }

        var snapshot = UserSnapshot.From(row);
        if (ttl > TimeSpan.Zero)
        {
            _byId[id] = (snapshot, Now + ttl);
        }

        return snapshot;
    }

    public async Task<UserSnapshot?> FindAsync(string username, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsReadDbContext>();
        var row = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        return row is null ? null : UserSnapshot.From(row);
    }

    public async Task<List<UserSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsReadDbContext>();
        var rows = await db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync(cancellationToken);
        return rows.Select(UserSnapshot.From).ToList();
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CmsReadDbContext>().Users.CountAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- signing in

    /// <summary>
    /// Checks a username and password. Returns the person, or null for every kind of failure (no such person, wrong password,
    /// account disabled) so the caller cannot tell them apart. A failure costs one slow hash whichever it is.
    /// </summary>
    public async Task<UserSnapshot?> VerifyAsync(string username, string password, CancellationToken cancellationToken)
    {
        UserRow? row;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsReadDbContext>();
            row = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        }

        var matches = PasswordHasher.Verify(password, row?.PasswordHash ?? _decoy.Value, Iterations, out var stale);
        if (row is null || !matches || row.Disabled)
        {
            return null;
        }

        if (stale)
        {
            await RehashAsync(row.Id, password, cancellationToken);
        }

        return UserSnapshot.From(row);
    }

    /// <summary>
    /// <see cref="VerifyAsync"/> for Basic credentials, which arrive on every request. A pair that has just been checked is
    /// remembered for a few minutes (keyed by a keyed hash of it, never the password) so a script does not pay for a slow hash each
    /// time. The person is still looked up every time, so disabling the account or changing the password takes effect at once.
    /// </summary>
    public async Task<UserSnapshot?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
    {
        var minutes = Settings.VerifiedCredentialCacheMinutes;
        if (minutes <= 0)
        {
            return await VerifyAsync(username, password, cancellationToken);
        }

        var key = Convert.ToHexString(HMACSHA256.HashData(_cacheKey, Encoding.UTF8.GetBytes(username + "\0" + password)));
        if (_verified.TryGetValue(key, out var known) && known.Expires > Now)
        {
            var person = await GetAsync(known.Id, cancellationToken);
            if (person is { Disabled: false } && person.Stamp == known.Stamp)
            {
                return person;
            }

            _verified.TryRemove(key, out _);
        }

        var verified = await VerifyAsync(username, password, cancellationToken);
        if (verified is not null)
        {
            if (_verified.Count >= MaxVerifiedCredentials)
            {
                foreach (var (stale, entry) in _verified)
                {
                    if (entry.Expires <= Now)
                    {
                        _verified.TryRemove(stale, out _);
                    }
                }

                if (_verified.Count >= MaxVerifiedCredentials)
                {
                    _verified.Clear();
                }
            }

            _verified[key] = (verified.Id, verified.Stamp, Now + TimeSpan.FromMinutes(minutes));
        }

        return verified;
    }

    public async Task RecordSignInAsync(Guid id, CancellationToken cancellationToken)
    {
        using var _ = await _gate.AcquireAsync(cancellationToken);
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsWriteDbContext>();
        var row = await db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (row is null)
        {
            return;
        }

        row.LastSignInAt = Now;
        db.Audit(Now, row.Username, "auth.signin", row.Username);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RehashAsync(Guid id, string password, CancellationToken cancellationToken)
    {
        using var _ = await _gate.AcquireAsync(cancellationToken);
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsWriteDbContext>();
        var row = await db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (row is not null && PasswordHasher.Verify(password, row.PasswordHash, int.MaxValue, out var ignored))
        {
            row.PasswordHash = PasswordHasher.Hash(password, Iterations);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // ---------------------------------------------------------------- changing

    public Task<AccountChange> CreateAsync(
        string actor, string username, string role, string? password, CancellationToken cancellationToken) =>
        ChangeAsync(actor, async (db, ct) =>
        {
            username = (username ?? "").Trim();
            var problem = UsernameProblem(username);
            if (problem is not null)
            {
                return AccountChange.Fail(AccountProblem.Invalid, problem);
            }

            if (NormaliseRole(role) is not { } canonicalRole)
            {
                return AccountChange.Fail(AccountProblem.Invalid, "The role must be User or Admin.");
            }

            if (IsConfigured(username) || await db.Users.AnyAsync(u => u.Username == username, ct))
            {
                return AccountChange.Fail(AccountProblem.Exists, "That username is already taken.");
            }

            string? temporary = null;
            if (password is null)
            {
                temporary = password = PasswordPolicy.Generate();
            }
            else if (PasswordPolicy.Problem(password, username) is { } weak)
            {
                return AccountChange.Fail(AccountProblem.Invalid, weak);
            }

            var now = Now;
            var row = new UserRow
            {
                Id = Guid.NewGuid(),
                Username = username,
                Role = canonicalRole,
                PasswordHash = PasswordHasher.Hash(password, Iterations),
                MustChangePassword = temporary is not null,
                SecurityStamp = Guid.NewGuid(),
                CreatedAt = now,
                PasswordChangedAt = now,
            };
            db.Users.Add(row);
            db.Audit(now, actor, "user.create", username, $"role {canonicalRole}{(temporary is null ? "" : ", temporary password")}");
            return new AccountChange(AccountProblem.None, User: UserSnapshot.From(row), TemporaryPassword: temporary);
        }, cancellationToken);

    /// <summary>
    /// An administrator (or the command-line tool) sets someone's password. With none given the service makes a temporary one,
    /// returned once, which the person must replace at their next sign-in. Every session they had ends.
    /// </summary>
    public Task<AccountChange> ResetPasswordAsync(
        string actor, string username, string? password, bool? mustChange, CancellationToken cancellationToken) =>
        ChangeAsync(actor, async (db, ct) =>
        {
            var row = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
            if (row is null)
            {
                return AccountChange.Fail(AccountProblem.NotFound, "No such user.");
            }

            string? temporary = null;
            if (password is null)
            {
                temporary = password = PasswordPolicy.Generate();
            }
            else if (PasswordPolicy.Problem(password, row.Username) is { } weak)
            {
                return AccountChange.Fail(AccountProblem.Invalid, weak);
            }

            SetPassword(row, password, mustChange ?? temporary is not null);
            db.Audit(Now, actor, "user.password.reset", row.Username, temporary is null ? "chosen by the administrator" : "temporary password");
            return new AccountChange(AccountProblem.None, User: UserSnapshot.From(row), TemporaryPassword: temporary);
        }, cancellationToken);

    /// <summary>A person changes their own password, proving they know the current one.</summary>
    public Task<AccountChange> ChangeOwnPasswordAsync(
        Guid id, string current, string replacement, CancellationToken cancellationToken)
    {
        UserRow? actor = null;
        return ChangeAsync("self", async (db, ct) =>
        {
            actor = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (actor is null || actor.Disabled)
            {
                return AccountChange.Fail(AccountProblem.NotFound, "No such user.");
            }

            if (!PasswordHasher.Verify(current ?? "", actor.PasswordHash, int.MaxValue, out _))
            {
                return AccountChange.Fail(AccountProblem.WrongPassword, "The current password is not right.");
            }

            if (PasswordPolicy.Problem(replacement, actor.Username) is { } weak)
            {
                return AccountChange.Fail(AccountProblem.Invalid, weak);
            }

            if (string.Equals(current, replacement, StringComparison.Ordinal))
            {
                return AccountChange.Fail(AccountProblem.Invalid, "The new password must differ from the current one.");
            }

            SetPassword(actor, replacement, mustChange: false);
            db.Audit(Now, actor.Username, "user.password.change", actor.Username);
            return new AccountChange(AccountProblem.None, User: UserSnapshot.From(actor));
        }, cancellationToken);
    }

    public Task<AccountChange> SetRoleAsync(string actor, string username, string role, CancellationToken cancellationToken) =>
        ChangeAsync(actor, async (db, ct) =>
        {
            if (NormaliseRole(role) is not { } canonicalRole)
            {
                return AccountChange.Fail(AccountProblem.Invalid, "The role must be User or Admin.");
            }

            var row = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
            if (row is null)
            {
                return AccountChange.Fail(AccountProblem.NotFound, "No such user.");
            }

            if (row.Role == canonicalRole)
            {
                return new AccountChange(AccountProblem.None, User: UserSnapshot.From(row));
            }

            if (row.Role == Roles.Admin && !row.Disabled && await OtherEnabledAdminsAsync(db, row.Id, ct) == 0)
            {
                return AccountChange.Fail(AccountProblem.LastAdmin, "That is the only enabled administrator; make someone else an administrator first.");
            }

            db.Audit(Now, actor, "user.role", row.Username, $"{row.Role} to {canonicalRole}");
            row.Role = canonicalRole;
            return new AccountChange(AccountProblem.None, User: UserSnapshot.From(row));
        }, cancellationToken);

    public Task<AccountChange> SetDisabledAsync(string actor, string username, bool disabled, CancellationToken cancellationToken) =>
        ChangeAsync(actor, async (db, ct) =>
        {
            var row = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
            if (row is null)
            {
                return AccountChange.Fail(AccountProblem.NotFound, "No such user.");
            }

            if (string.Equals(row.Username, actor, StringComparison.OrdinalIgnoreCase) && disabled)
            {
                return AccountChange.Fail(AccountProblem.Yourself, "You cannot disable your own account.");
            }

            if (row.Disabled == disabled)
            {
                return new AccountChange(AccountProblem.None, User: UserSnapshot.From(row));
            }

            if (disabled && row.Role == Roles.Admin && await OtherEnabledAdminsAsync(db, row.Id, ct) == 0)
            {
                return AccountChange.Fail(AccountProblem.LastAdmin, "That is the only enabled administrator.");
            }

            row.Disabled = disabled;
            row.SecurityStamp = Guid.NewGuid();
            db.Audit(Now, actor, disabled ? "user.disable" : "user.enable", row.Username);
            return new AccountChange(AccountProblem.None, User: UserSnapshot.From(row));
        }, cancellationToken);

    /// <summary>Ends every session and remembered sign-in the person has, without changing the password.</summary>
    public Task<AccountChange> RevokeSessionsAsync(string actor, string username, CancellationToken cancellationToken) =>
        ChangeAsync(actor, async (db, ct) =>
        {
            var row = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
            if (row is null)
            {
                return AccountChange.Fail(AccountProblem.NotFound, "No such user.");
            }

            row.SecurityStamp = Guid.NewGuid();
            db.Audit(Now, actor, "user.sessions.revoke", row.Username);
            return new AccountChange(AccountProblem.None, User: UserSnapshot.From(row));
        }, cancellationToken);

    public Task<AccountChange> DeleteAsync(string actor, string username, CancellationToken cancellationToken) =>
        ChangeAsync(actor, async (db, ct) =>
        {
            var row = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
            if (row is null)
            {
                return AccountChange.Fail(AccountProblem.NotFound, "No such user.");
            }

            if (string.Equals(row.Username, actor, StringComparison.OrdinalIgnoreCase))
            {
                return AccountChange.Fail(AccountProblem.Yourself, "You cannot delete your own account.");
            }

            if (row.Role == Roles.Admin && !row.Disabled && await OtherEnabledAdminsAsync(db, row.Id, ct) == 0)
            {
                return AccountChange.Fail(AccountProblem.LastAdmin, "That is the only enabled administrator.");
            }

            db.Users.Remove(row);
            db.Audit(Now, actor, "user.delete", row.Username);
            return new AccountChange(AccountProblem.None, User: UserSnapshot.From(row));
        }, cancellationToken);

    /// <summary>Creates the first administrator if nobody can sign in yet. Returns the temporary password, or null if one was not needed.</summary>
    public async Task<(string Username, string Password)?> BootstrapAsync(CancellationToken cancellationToken)
    {
        if (Settings.Users.Count > 0 || await CountAsync(cancellationToken) > 0)
        {
            return null;
        }

        var change = await CreateAsync("system", Settings.BootstrapAdminUsername, Roles.Admin, null, cancellationToken);
        return change.Ok ? (change.User!.Username, change.TemporaryPassword!) : null;
    }

    // ---------------------------------------------------------------- plumbing

    private async Task<AccountChange> ChangeAsync(
        string actor, Func<CmsWriteDbContext, CancellationToken, Task<AccountChange>> change, CancellationToken cancellationToken)
    {
        AccountChange result;
        using (await _gate.AcquireAsync(cancellationToken))
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CmsWriteDbContext>();
            result = await change(db, cancellationToken);
            if (!result.Ok)
            {
                return result;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        // Whatever was cached about anybody is now in doubt; reading it again is cheap.
        _byId.Clear();
        _verified.Clear();
        _logger.LogInformation("Account change by {Actor} saved", actor);
        return result;
    }

    private void SetPassword(UserRow row, string password, bool mustChange)
    {
        var now = Now;
        row.PasswordHash = PasswordHasher.Hash(password, Iterations);
        row.MustChangePassword = mustChange;
        row.PasswordChangedAt = now;
        row.SecurityStamp = Guid.NewGuid();
    }

    private static async Task<int> OtherEnabledAdminsAsync(CmsWriteDbContext db, Guid except, CancellationToken cancellationToken) =>
        await db.Users.CountAsync(u => u.Role == Roles.Admin && !u.Disabled && u.Id != except, cancellationToken);

    private bool IsConfigured(string username) =>
        string.Equals(username, Settings.Cms.Username, StringComparison.OrdinalIgnoreCase)
        || Settings.Users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

    public static string? NormaliseRole(string? role) =>
        string.Equals(role, Roles.Admin, StringComparison.OrdinalIgnoreCase) ? Roles.Admin
        : string.Equals(role, Roles.User, StringComparison.OrdinalIgnoreCase) ? Roles.User
        : null;

    public static string? UsernameProblem(string username)
    {
        if (username.Length is < 1 or > 100)
        {
            return "The username must be 1 to 100 characters.";
        }

        if (username.Contains(':') || username.Any(char.IsControl))
        {
            return "The username must not contain ':' or control characters.";
        }

        return null;
    }
}
