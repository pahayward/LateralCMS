namespace Lateral.Cms.Persistence;

/// <summary>Where an uploaded asset is in its life. Only <see cref="Clean"/> assets are ever served.</summary>
public enum AssetState
{
    /// <summary>Stored, not yet cleared by the scanner (also while it is retried after a scanner error). Never served.</summary>
    Quarantined,

    /// <summary>The scanner cleared it. The only state that can be downloaded.</summary>
    Clean,

    /// <summary>The scanner flagged it. The bytes are wiped; only the verdict remains.</summary>
    Rejected,

    /// <summary>The scanner kept failing and the job was set aside. Still never served; an admin can requeue it.</summary>
    Failed,
}

/// <summary>One stored asset and, until it is cleared, its scan job (attempts and back-off live on the row).</summary>
public sealed class AssetRow
{
    public Guid Id { get; set; }

    /// <summary>The name the CMS gave it. Used for the download file name only, never as a path.</summary>
    public string Key { get; set; } = "";

    /// <summary>What the sender claimed. Never used to serve the asset.</summary>
    public string ContentType { get; set; } = "";

    /// <summary>What the scanner found by looking at the bytes. This is what a download is served as.</summary>
    public string? DetectedType { get; set; }

    public long Size { get; set; }

    /// <summary>Lower-case hex SHA-256 of the bytes. Content address, ETag and integrity check in one.</summary>
    public string Sha256 { get; set; } = "";

    public AssetState State { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ScannedAt { get; set; }

    public string? ScannerName { get; set; }

    public string? ScannerVersion { get; set; }

    /// <summary>The scanner's verdict word (<c>clean</c>, <c>infected</c>, <c>suspicious</c>), kept after the bytes are gone.</summary>
    public string? Verdict { get; set; }

    /// <summary>The signature or rule that matched. Never any content.</summary>
    public string? Signature { get; set; }

    public int Attempts { get; set; }

    public DateTime NextAttemptAt { get; set; }

    /// <summary>Kind of the last scan failure (an exception type), never a message.</summary>
    public string? LastError { get; set; }
}

/// <summary>
/// Which records name which files: one row per (file id, record id) for every file-id-shaped text in the record's current payload.
/// Written in the same transaction that applies the record, so it always matches. It is what lets a user download exactly the files
/// of records they can see, and what makes deleting a record delete its files. Holds ids only, never content.
/// </summary>
public sealed class AssetLinkRow
{
    /// <summary>A GUID found in the payload. There is no foreign key: the file may arrive after the record that names it.</summary>
    public Guid AssetId { get; set; }

    public string EntityId { get; set; } = "";
}

/// <summary>A multipart upload in progress.</summary>
public sealed class UploadRow
{
    public Guid UploadId { get; set; }

    public string Key { get; set; } = "";

    public string ContentType { get; set; } = "";

    public long DeclaredSize { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }
}

/// <summary>One uploaded part. Re-uploading a part number replaces it (last write wins, as in S3).</summary>
public sealed class UploadPartRow
{
    public Guid UploadId { get; set; }

    public int PartNumber { get; set; }

    public long Size { get; set; }

    public string Sha256 { get; set; } = "";
}

/// <summary>Append-only record of who did what. Holds no content. The schema refuses updates and deletes.</summary>
public sealed class AuditRow
{
    public long Id { get; set; }

    public DateTime At { get; set; }

    public string Actor { get; set; } = "";

    public string Action { get; set; } = "";

    public string Target { get; set; } = "";

    public string? Detail { get; set; }
}
