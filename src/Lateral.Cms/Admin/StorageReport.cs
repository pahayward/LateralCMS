using Lateral.Cms.Assets;
using Lateral.Cms.Auth;
using Lateral.Cms.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Admin;

/// <summary>
/// Where everything the service keeps lives, how big it is, how much room is left, and what to back up. Read from the database
/// and the file system; nothing here changes anything.
/// </summary>
public sealed class StorageReport(
    DatabaseConnections database, IOptions<AssetOptions> assets, IOptions<AuthOptions> auth, IHostEnvironment environment,
    InitialPasswordFile initial)
{
    /// <summary>Folders with more files than this are reported as "at least" so the page cannot make the disk work for long.</summary>
    private const int WalkLimit = 50_000;

    public async Task<object> BuildAsync(CmsReadDbContext db, CancellationToken cancellationToken)
    {
        var dbFile = database.DataSource;
        var assetRoot = Path.IsPathRooted(assets.Value.Root)
            ? assets.Value.Root
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, assets.Value.Root));
        var keys = auth.Value.KeysPath is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(Path.GetDirectoryName(dbFile)!, "keys");

        var byState = await db.Assets.GroupBy(a => a.State)
            .Select(g => new { State = g.Key, Count = g.Count(), Bytes = g.Sum(a => a.Size) })
            .ToListAsync(cancellationToken);

        var counts = new
        {
            entities = await db.Entities.CountAsync(cancellationToken),
            tombstones = await db.Tombstones.CountAsync(cancellationToken),
            events = await db.Events.CountAsync(cancellationToken),
            batches = await db.Batches.CountAsync(cancellationToken),
            assets = byState.Sum(s => s.Count),
            users = await db.Users.CountAsync(cancellationToken),
            audit = await db.AuditLog.CountAsync(cancellationToken),
        };

        var stored = byState.Where(s => s.State != AssetState.Rejected).Sum(s => s.Bytes);
        return new
        {
            database = new
            {
                path = dbFile,
                bytes = Size(dbFile),
                walBytes = Size(dbFile + "-wal"),
                disk = Disk(dbFile),
                engine = "SQLite (one file, WAL mode)",
            },
            assets = new
            {
                root = assetRoot,
                quotaBytes = assets.Value.MaxBytes,
                storedBytes = stored,
                byState = byState.Select(s => new { state = SnakeCaseNames<AssetState>.ToText(s.State), count = s.Count, bytes = s.Bytes }).ToList(),
                folders = new[] { "blobs", "quarantine", "parts" }.Select(f => Folder(Path.Combine(assetRoot, f), f)).ToList(),
                disk = Disk(assetRoot),
                store = "local file system (blobs are named by their SHA-256, so identical content is kept once)",
            },
            sessionKeys = new { path = keys, files = Directory.Exists(keys) ? Directory.GetFiles(keys).Length : 0 },
            firstRunPasswordFile = new { path = initial.Path, exists = File.Exists(initial.Path) },
            counts,
            logs = "standard output, as JSON lines (the service keeps no log files)",
            backup = new[]
            {
                new { what = "the database", path = dbFile, how = "sqlite3 <db> \".backup '<copy>'\" while running, or copy the file with its -wal after stopping" },
                new { what = "the asset store", path = assetRoot, how = "copy the whole folder (rsync -a); blobs never change once written" },
                new { what = "the session keys", path = keys, how = "optional: without them everyone signs in again" },
                new { what = "the configuration", path = environment.ContentRootPath, how = "appsettings.Production.json and any environment file holding the CMS secret" },
            },
        };
    }

    private static long? Size(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static object? Disk(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null)
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return new { volume = drive.Name, freeBytes = drive.AvailableFreeSpace, totalBytes = drive.TotalSize };
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static object Folder(string path, string name)
    {
        long bytes = 0;
        var files = 0;
        var truncated = false;
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (files++ >= WalkLimit)
                    {
                        truncated = true;
                        break;
                    }

                    bytes += file.Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file removed while counting: the figure is approximate anyway.
        }

        return new { name, files, bytes, atLeast = truncated };
    }
}
