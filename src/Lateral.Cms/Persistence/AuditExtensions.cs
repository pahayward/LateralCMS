namespace Lateral.Cms.Persistence;

public static class AuditExtensions
{
    /// <summary>
    /// Adds an audit record to the context, to be saved in the same transaction as the change it describes (so a change
    /// and its record exist together or not at all). Never put content or secrets in <paramref name="detail"/>.
    /// </summary>
    public static void Audit(this CmsDbContext db, DateTime at, string actor, string action, string target, string? detail = null) =>
        db.AuditLog.Add(new AuditRow
        {
            At = at,
            Actor = Bound(actor, 320),
            Action = Bound(action, 64),
            Target = Bound(target, 200),
            Detail = detail is null ? null : Bound(detail, 500),
        });

    private static string Bound(string text, int max) => text.Length <= max ? text : text[..max];
}
