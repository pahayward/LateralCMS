using System.Text;
using Lateral.Cms.Domain;
using Lateral.Cms.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Entities;

/// <summary>Read side. Uses the read-only, non-tracking context and projects straight to what it serves.</summary>
public sealed class EntityQueries(CmsReadDbContext db, IOptions<ApiOptions> options)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private sealed record Row(
        string Id, long Version, string PayloadChecksum, DateTime LastEventTimestamp, string Payload,
        bool IsPublished, bool IsDisabledByAdmin, string? DisabledBy, DateTime? DisabledAt);

    public async Task<EntityPage> ListAsync(bool isAdmin, string? afterId, int? requestedLimit, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var limit = Math.Clamp(requestedLimit ?? settings.DefaultPageSize, 1, settings.MaxPageSize);

        var query = Visible(isAdmin);
        if (afterId is not null)
        {
            query = query.Where(e => string.Compare(e.Id, afterId) > 0);
        }

        // Keyset pagination: cost does not grow with page depth, and the page is stable under inserts.
        // One extra row tells us whether there is a next page without a COUNT.
        var rows = await query
            .OrderBy(e => e.Id)
            .Take(limit + 1)
            .Select(ProjectRow)
            .ToListAsync(cancellationToken);

        var items = rows.Take(limit).Select(row => ToDto(row, isAdmin)).ToList();
        var next = rows.Count > limit ? EncodeCursor(items[^1].Id) : null;
        return new EntityPage(items, next);
    }

    public async Task<EntityDto?> GetAsync(bool isAdmin, string id, CancellationToken cancellationToken)
    {
        var row = await Visible(isAdmin)
            .Where(e => e.Id == id)
            .Select(ProjectRow)
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToDto(row, isAdmin);
    }

    /// <summary>Decodes a cursor produced by this class. Null means "from the start".</summary>
    public static bool TryDecodeCursor(string? cursor, out string? afterId)
    {
        afterId = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        try
        {
            afterId = StrictUtf8.GetString(WebEncoders.Base64UrlDecode(cursor));
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The visibility rule. Consumers see what the CMS has published and no admin has disabled; admins see
    /// everything. Decided here, from the caller's role, never from a request parameter.
    /// </summary>
    private IQueryable<EntityRow> Visible(bool isAdmin) =>
        isAdmin ? db.Entities : db.Entities.Where(e => e.IsPublished && !e.IsDisabledByAdmin);

    private static readonly System.Linq.Expressions.Expression<Func<EntityRow, Row>> ProjectRow = e =>
        new Row(e.Id, e.Version, e.PayloadChecksum, e.LastEventTimestamp, e.Payload, e.IsPublished, e.IsDisabledByAdmin, e.DisabledBy, e.DisabledAt);

    private static EntityDto ToDto(Row row, bool isAdmin) =>
        new(
            row.Id,
            row.Version,
            row.PayloadChecksum,
            row.LastEventTimestamp,
            new RawJson(row.Payload),
            isAdmin ? new EntityStatusDto(row.IsPublished, row.IsDisabledByAdmin, row.DisabledBy, row.DisabledAt) : null);

    private static string EncodeCursor(string id) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(id));
}
