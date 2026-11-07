using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Assets;

/// <summary>A file written by the store: its size and lower-case hex SHA-256.</summary>
public readonly record struct StoredFile(long Size, string Sha256);

public sealed class AssetTooLargeException(long limit) : Exception($"The content is larger than {limit} bytes.")
{
    public long Limit { get; } = limit;
}

public enum AssetArea
{
    Parts,
    Quarantine,
    Blobs,
}

/// <summary>
/// Where asset bytes live. The one seam for storage: today a folder on disk, tomorrow an object store, with no change to the
/// upload, scan or download code. Nothing here knows about scanning or access: it stores, moves and wipes files.
/// </summary>
public interface IAssetStore
{
    /// <summary>Streams <paramref name="source"/> to <paramref name="destination"/>: hashed as it goes, never buffered whole, flushed to disk, then renamed into place.</summary>
    /// <exception cref="AssetTooLargeException">The stream held more than <paramref name="maxBytes"/>; nothing was kept.</exception>
    Task<StoredFile> SaveAsync(Stream source, string destination, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Concatenates the parts in order into <paramref name="destination"/> the same way.</summary>
    Task<StoredFile> AssembleAsync(IReadOnlyList<string> partPaths, string destination, CancellationToken cancellationToken);

    string PartPath(Guid uploadId, int partNumber);

    string QuarantinePath(Guid assetId);

    string BlobPath(string sha256);

    bool Exists(string path);

    Stream OpenRead(string path);

    /// <summary>Moves a cleared file into the content-addressed area. If that content is already there, the duplicate is wiped.</summary>
    Task PromoteAsync(string quarantinePath, string sha256, CancellationToken cancellationToken);

    /// <summary>
    /// Overwrites the file with zeros, flushes, and deletes it. The overwrite happens even if the file is open for reading. False
    /// if it could not be removed (some platforms refuse to delete an open file): the content is already zeros, and the sweeper removes it.
    /// </summary>
    Task<bool> WipeAsync(string path, CancellationToken cancellationToken);

    void DeleteUploadParts(Guid uploadId);

    IEnumerable<FileInfo> Files(AssetArea area);

    IEnumerable<Guid> UploadDirectories();
}

public sealed class FileAssetStore : IAssetStore
{
    private const int BufferSize = 128 * 1024;
    private const string TempMarker = ".tmp-";

    private readonly string _root;

    public FileAssetStore(IOptions<AssetOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.Root;
        _root = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured));
        foreach (var area in new[] { "parts", "quarantine", "blobs" })
        {
            Directory.CreateDirectory(Path.Combine(_root, area));
        }
    }

    public string PartPath(Guid uploadId, int partNumber) => Path.Combine(_root, "parts", uploadId.ToString("N"), partNumber.ToString());

    public string QuarantinePath(Guid assetId) => Path.Combine(_root, "quarantine", assetId.ToString("N"));

    // Only ever called with a hex digest we computed ourselves, but check anyway: a path built from text must not be steerable.
    public string BlobPath(string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Not a SHA-256 digest.", nameof(sha256));
        }

        var digest = sha256.ToLowerInvariant();
        return Path.Combine(_root, "blobs", digest[..2], digest[2..4], digest);
    }

    public bool Exists(string path) => File.Exists(path);

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public async Task<StoredFile> SaveAsync(Stream source, string destination, long maxBytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + TempMarker + Guid.NewGuid().ToString("N");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        throw new AssetTooLargeException(maxBytes);
                    }

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }

            File.Move(temp, destination, overwrite: true);
            return new StoredFile(total, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public async Task<StoredFile> AssembleAsync(IReadOnlyList<string> partPaths, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + TempMarker + Guid.NewGuid().ToString("N");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                var buffer = new byte[BufferSize];
                foreach (var part in partPaths)
                {
                    await using var input = OpenRead(part);
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        total += read;
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }

                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            File.Move(temp, destination, overwrite: true);
            return new StoredFile(total, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public async Task PromoteAsync(string quarantinePath, string sha256, CancellationToken cancellationToken)
    {
        var blob = BlobPath(sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        if (File.Exists(blob))
        {
            // Same content is already stored and cleared: keep one copy.
            await WipeAsync(quarantinePath, cancellationToken);
            return;
        }

        File.Move(quarantinePath, blob);
    }

    public async Task<bool> WipeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            // Shared, so the overwrite happens even while someone is reading: deleted content must not stay readable.
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous))
            {
                var zeros = new byte[BufferSize];
                for (long written = 0; written < file.Length; written += zeros.Length)
                {
                    await file.WriteAsync(zeros.AsMemory(0, (int)Math.Min(zeros.Length, file.Length - written)), cancellationToken);
                }

                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }

            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void DeleteUploadParts(Guid uploadId)
    {
        var directory = Path.GetDirectoryName(PartPath(uploadId, 1))!;
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Left for the sweeper.
        }
    }

    public IEnumerable<FileInfo> Files(AssetArea area)
    {
        var directory = Path.Combine(_root, area.ToString().ToLowerInvariant());
        return Directory.Exists(directory) ? new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories) : [];
    }

    public IEnumerable<Guid> UploadDirectories()
    {
        var directory = Path.Combine(_root, "parts");
        return Directory.Exists(directory)
            ? Directory.EnumerateDirectories(directory).Select(d => Guid.TryParseExact(Path.GetFileName(d), "N", out var id) ? id : Guid.Empty).Where(g => g != Guid.Empty).ToList()
            : [];
    }

    /// <summary>True for the half-written temp files that <see cref="SaveAsync"/> and <see cref="AssembleAsync"/> leave if the process dies.</summary>
    public static bool IsTemp(FileInfo file) => file.Name.Contains(TempMarker, StringComparison.Ordinal);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best effort; the sweeper removes stale temp files
        }
    }
}
