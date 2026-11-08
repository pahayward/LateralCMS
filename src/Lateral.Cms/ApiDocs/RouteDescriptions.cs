namespace Lateral.Cms.ApiDocs;

/// <summary>
/// What each route is for, in the words the API reference shows, keyed by the route's name (<c>WithName</c>). The reference reads
/// the routes from the running service, so it cannot list one that is gone; this table supplies the explanations, and a test fails
/// if a route has none, so the reference cannot fall behind the code.
/// </summary>
public static class RouteDescriptions
{
    public sealed record Description(string Summary, string Detail, params (int Status, string Meaning)[] Statuses);

    public static readonly IReadOnlyDictionary<string, Description> ByName = new Dictionary<string, Description>
    {
        // ---- the CMS pushes content ----
        ["IngestCmsEvents"] = new("Send a batch of CMS events",
            "The only way records get in. The body is a JSON array of events: publish, unPublish or delete, each with an id, a timestamp, "
            + "and (for publish and unPublish) a version and a payload object. Events may arrive late, twice or out of order; the end state is the same. "
            + "A 202 means the whole batch is stored durably, not that it has been applied: follow it with the batch status route using the X-Correlation-Id header.",
            (202, "Accepted: the batch is stored and will be applied. The X-Correlation-Id header is its batch id."),
            (400, "The body is not valid; nothing was stored. The problem body names what is wrong."),
            (413, "The body is larger than the limit (refused before it was read)."),
            (429, "Too many batches are being accepted at once, or the queue is full. Retry after the Retry-After seconds."),
            (503, "The batch could not be stored. Nothing was accepted; send it again.")),
        ["GetBatchStatus"] = new("Follow one batch",
            "Where a batch sent to POST /cms/events has got to: accepted, applying, completed, or failed and set aside.",
            (200, "The batch's state."), (404, "No such batch.")),

        // ---- reading records ----
        ["ListEntities"] = new("List records",
            "Records in id order, a page at a time. Pass the cursor from one page to get the next. Records the caller may not see are left out.",
            (200, "A page of records and, if there are more, the cursor for the next."), (400, "The cursor or limit is not valid.")),
        ["GetEntity"] = new("Read one record",
            "One record by id. A record that is hidden from the caller answers exactly like one that does not exist.",
            (200, "The record."), (404, "No such record, or not visible to this caller.")),
        ["DisableEntity"] = new("Hide a record from users",
            "An admin override: the record stays stored but users no longer see it. A later publish from the CMS does not undo it.",
            (200, "Done."), (404, "No such record.")),
        ["EnableEntity"] = new("Show a record to users again", "Reverses a disable.", (200, "Done."), (404, "No such record.")),

        // ---- files ----
        ["PutAsset"] = new("Upload a small file",
            "One request, up to the simple-upload limit. Send the bytes as the body with the file's Content-Type, and optionally their SHA-256 in X-Content-SHA256 to have them checked. "
            + "The file is held in quarantine and scanned; nothing can download it until the scanner has said it is clean.",
            (202, "Stored and queued for scanning. The body says where to follow it."),
            (400, "The key, type or checksum is not valid."), (413, "Larger than the simple-upload limit: use a multipart upload."),
            (429, "The asset store is full.")),
        ["InitiateUpload"] = new("Start a multipart upload",
            "For large files. Say the file's key, content type and size; the answer gives the upload id and the part size limits. Then put the parts (in parallel if you like) and complete the upload.",
            (201, "The upload is open."), (400, "The key, type or size is not valid."), (413, "Larger than the object limit.")),
        ["PutPart"] = new("Upload one part",
            "Send the part's bytes as the body, optionally with their SHA-256 in X-Content-SHA256. Sending the same part number again with the same bytes is harmless, so a failed part can simply be retried.",
            (200, "The part's receipt, including its checksum (also the ETag)."), (400, "The part is the wrong size or its checksum does not match."), (404, "No such open upload.")),
        ["CompleteUpload"] = new("Finish a multipart upload",
            "List the parts in order. The service assembles them, checks the size and SHA-256, and queues the result for scanning.",
            (202, "Assembled and queued for scanning."), (400, "A part is missing or does not match."), (404, "No such open upload.")),
        ["AbortUpload"] = new("Cancel a multipart upload", "Throws the uploaded parts away.", (204, "Cancelled."), (404, "No such open upload.")),
        ["GetAssetStatus"] = new("Follow one file",
            "A file's state (quarantined, clean or rejected), the scanner's verdict and its checksum. Never the bytes.",
            (200, "The file's state."), (404, "No such file.")),
        ["DownloadAsset"] = new("Download a file",
            "The bytes of a clean file. Supports Range requests (so a big file can be fetched as parallel ranges), If-Range and If-None-Match; the ETag is the SHA-256. "
            + "Anything not clean answers 404, the same as a file that does not exist. GET and HEAD.",
            (200, "The whole file."), (206, "The requested range."), (304, "Not modified."), (404, "No such file, or not clean."), (416, "The range cannot be satisfied.")),
        ["DeleteAsset"] = new("Delete a file for good", "Overwrites and removes the stored bytes. Cannot be undone.", (204, "Deleted."), (404, "No such file.")),
        ["ListAssets"] = new("List files with their state", "Every file, optionally only those in one state, newest first.", (200, "The files.")),
        ["RescanAsset"] = new("Scan a file again", "Queues one clean file for another scan, for example after the scanner's signatures changed.", (200, "Queued: the file is held back until the new scan is done."), (409, "Only a clean file can be scanned again, or there is no such file.")),
        ["RequeueAsset"] = new("Retry a file the scanner gave up on",
            "A file whose scan kept failing is set aside, still unserved. This puts it back in the queue.", (200, "Queued."), (409, "The file is not one whose scanning was set aside, or there is no such file.")),
        ["RescanOutdatedAssets"] = new("Scan again every file checked by an older scanner", "Queues every file whose recorded scanner version is not the current one.", (200, "Queued: the body gives the current scanner version and how many files were queued.")),

        // ---- watching the queue ----
        ["GetIngestionStatus"] = new("Show the batch queue", "How many batches are waiting, being applied, done or set aside, and the oldest wait.", (200, "The queue report.")),
        ["RequeueFailedBatch"] = new("Retry a failed batch", "Puts a batch that failed and was set aside back in the queue.", (200, "Queued."), (409, "The batch has not failed."), (404, "No such batch.")),

        // ---- signing in ----
        ["SignIn"] = new("Sign in",
            "Exchanges a username and password for a session cookie (HttpOnly, SameSite=Strict). Anyone may call it; repeated failures are throttled. "
            + "Requests made with the cookie that change something must also send an X-Requested-With header (any value), which a web page on another site cannot do.",
            (200, "Signed in; the cookie is set. mustChangePassword says whether a new password is required first."), (400, "No username or password was sent."),
            (401, "Wrong username or password."), (403, "The request came from another site."), (429, "Too many failed attempts; wait a moment.")),
        ["SignOut"] = new("Sign out", "Ends the session cookie.", (204, "Signed out.")),
        ["WhoAmI"] = new("Who am I", "The caller's username, role and how they signed in.", (200, "The caller.")),
        ["ChangeOwnPassword"] = new("Change my password", "Needs the current password. Ends the caller's other sessions.", (200, "Changed."), (400, "The new password is too short or too long, or the current one is wrong."), (409, "This account is set in configuration, so its password cannot be changed here.")),

        // ---- people ----
        ["ListUsers"] = new("List people", "Everyone who can sign in, with role, state and when they last did. Never password hashes.", (200, "The people.")),
        ["CreateUser"] = new("Add a person", "Username and role (User or Admin), and optionally a starting password of at least 12 characters; if none is sent one is made and returned once, as temporaryPassword.", (201, "Created."), (400, "Not valid."), (409, "That username is taken.")),
        ["UpdateUser"] = new("Change a person's role or disable them", "Cannot demote or disable the last admin or the caller's own account.", (200, "Changed."), (404, "No such person."), (409, "Refused: the last admin or yourself.")),
        ["ResetUserPassword"] = new("Reset a person's password", "Sets a new password (or makes one and returns it once as temporaryPassword) and ends all of that person's sessions.", (200, "Reset."), (400, "Not valid."), (404, "No such person.")),
        ["RevokeUserSessions"] = new("Sign a person out everywhere", "Ends every session of that person.", (200, "Done."), (404, "No such person.")),
        ["DeleteUser"] = new("Delete a person", "Cannot delete the last admin or the caller's own account.", (204, "Deleted."), (404, "No such person."), (409, "Refused: the last admin or yourself.")),

        // ---- running it ----
        ["ShowSettings"] = new("Show every setting", "Each setting the service reads, its value, default, where the value came from and what it is for. Secrets show only as set or not set.", (200, "The settings.")),
        ["ShowStorage"] = new("Show where data is kept", "The database, asset and key folders with sizes and free space, record counts and what to back up.", (200, "The storage report.")),
        ["ResetAllData"] = new("Erase all data (testing only)",
            "Exists only when Admin:AllowReset is on, which the service refuses in Production. The body must carry the confirmation phrase.", (200, "Erased."), (400, "Wrong confirmation phrase.")),

        // ---- looking after itself ----
        ["Health"] = new("Is it up", "Answers 200 when the database can be reached, 503 when not. No sign-in needed: this is what a reverse proxy or monitor should poll.", (200, "Up."), (503, "The database cannot be reached.")),
    };
}
