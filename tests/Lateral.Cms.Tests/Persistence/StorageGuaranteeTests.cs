using System.Text;
using Lateral.Cms.Persistence;
using Lateral.Cms.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lateral.Cms.Tests.Persistence;

/// <summary>Claims made about the storage layer: the schema, WAL, constraints, UTC, and what a "hard delete" leaves in the file.</summary>
public class StorageGuaranteeTests(CmsFactory factory) : IClassFixture<CmsFactory>
{
    private IServiceScope Scope()
    {
        _ = factory.Services; // make sure the app has started and the database exists
        return factory.Services.CreateScope();
    }

    // ---- ST-01: one migration builds the final schema ----------------------------------------------------

    [Fact]
    [Trait("req", "ST-01")]
    public void The_migrations_build_the_final_schema_from_nothing()
    {
        _ = factory.Services;

        var migrations = factory.Sql("SELECT MigrationId FROM __EFMigrationsHistory").Select(r => (string)r[0]!).ToList();
        var tables = factory.Sql("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE '\\_\\_%' ESCAPE '\\' AND name NOT LIKE 'sqlite_%' ORDER BY name")
            .Select(r => (string)r[0]!);
        var indexes = factory.Sql("SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'IX\\_%' ESCAPE '\\' ORDER BY name")
            .Select(r => (string)r[0]!);

        // Two migrations: the core schema, then assets and the audit log. Each is applied from nothing, in order.
        Assert.Equal(2, migrations.Count);
        Assert.EndsWith("_Initial", migrations[0]);
        Assert.EndsWith("_Assets", migrations[1]);
        Assert.Equal(["Assets", "AuditLog", "Batches", "Entities", "Events", "Tombstones", "UploadParts", "Uploads"], tables);
        Assert.Contains("IX_Batches_Pick", indexes);
        Assert.Contains("IX_Entities_Visible", indexes);
        Assert.Contains("IX_Events_BatchId_Sequence", indexes);
        Assert.Contains("IX_Events_EntityId", indexes);
    }

    // ---- ST-03: WAL, FULL synchronous, one writer at a time ----------------------------------------------

    [Fact]
    [Trait("req", "ST-03")]
    public async Task The_database_is_in_wal_mode_and_keeps_synchronous_full_so_an_acknowledgement_is_durable()
    {
        using var scope = Scope();
        var write = scope.ServiceProvider.GetRequiredService<CmsWriteDbContext>();

        var mode = (await write.Database.SqlQueryRaw<string>("SELECT journal_mode AS Value FROM pragma_journal_mode").ToListAsync()).Single();
        var synchronous = (await write.Database.SqlQueryRaw<int>("SELECT synchronous AS Value FROM pragma_synchronous").ToListAsync()).Single();

        Assert.Equal("wal", mode);
        Assert.Equal(2, synchronous); // 2 = FULL
    }

    // ---- ST-05: the schema enforces what the code assumes ------------------------------------------------

    private static string NewBatch(CmsFactory f)
    {
        var id = Guid.NewGuid().ToString().ToUpperInvariant();
        f.Sql($"INSERT INTO Batches (BatchId, ReceivedAt, EventCount, PayloadBytes, HasTakedown, Status, Attempts, NextAttemptAt) " +
              $"VALUES ('{id}', '2024-01-01 00:00:00', 1, 1, 0, 'queued', 0, '2024-01-01 00:00:00')");
        return id;
    }

    private static string InsertEvent(string batch, int sequence, string outcome, string? payload) =>
        $"INSERT INTO Events (BatchId, Sequence, RawType, Payload, Outcome, ReceivedAt) VALUES ('{batch}', {sequence}, 'publish', " +
        $"{(payload is null ? "NULL" : $"'{payload}'")}, '{outcome}', '2024-01-01 00:00:00')";

    [Fact]
    [Trait("req", "ST-05")]
    [Trait("req", "AS-09")]
    public void A_decided_event_cannot_hold_a_payload_the_schema_refuses_it()
    {
        _ = factory.Services;
        var batch = NewBatch(factory);

        factory.Sql(InsertEvent(batch, 0, "queued", "{\"a\":1}")); // fine: still waiting
        factory.Sql(InsertEvent(batch, 1, "applied", null)); // fine: decided, no payload

        foreach (var outcome in new[] { "applied", "ignored_stale", "ignored_duplicate", "ignored_unsupported_type", "rejected" })
        {
            var ex = Assert.Throws<SqliteException>(() => factory.Sql(InsertEvent(batch, 10, outcome, "{\"a\":1}")));
            Assert.Contains("CHECK", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Updating a queued row into a decided one while it still holds its payload is refused too.
        var update = Assert.Throws<SqliteException>(() =>
            factory.Sql($"UPDATE Events SET Outcome = 'applied' WHERE BatchId = '{batch}' AND Sequence = 0"));
        Assert.Contains("CHECK", update.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("req", "ST-05")]
    public void Event_positions_and_batch_ids_are_unique()
    {
        _ = factory.Services;
        var batch = NewBatch(factory);
        factory.Sql(InsertEvent(batch, 0, "queued", "{}"));

        var duplicateEvent = Assert.Throws<SqliteException>(() => factory.Sql(InsertEvent(batch, 0, "queued", "{}")));
        var duplicateBatch = Assert.Throws<SqliteException>(() => factory.Sql(
            $"INSERT INTO Batches (BatchId, ReceivedAt, EventCount, PayloadBytes, HasTakedown, Status, Attempts, NextAttemptAt) " +
            $"VALUES ('{batch}', '2024-01-01 00:00:00', 1, 1, 0, 'queued', 0, '2024-01-01 00:00:00')"));

        Assert.Contains("UNIQUE", duplicateEvent.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNIQUE", duplicateBatch.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- ST-04: UTC in, UTC out --------------------------------------------------------------------------

    [Fact]
    [Trait("req", "ST-04")]
    public async Task Every_timestamp_comes_back_as_utc()
    {
        var id = Evt.Id();
        await factory.SendAsync(Evt.Publish(id, 1, 1), Evt.Delete(id + "-x", 2));

        using var scope = Scope();
        var read = scope.ServiceProvider.GetRequiredService<CmsReadDbContext>();
        var entity = await read.Entities.SingleAsync(e => e.Id == id);
        var tombstone = await read.Tombstones.SingleAsync(t => t.Id == id + "-x");
        var ledger = await read.Events.Where(e => e.EntityId == id).SingleAsync();
        var batch = await read.Batches.OrderByDescending(b => b.Id).FirstAsync();

        foreach (var value in new[]
                 {
                     entity.LastEventTimestamp, entity.CreatedAt, entity.UpdatedAt, tombstone.DeletedAt, tombstone.RecordedAt,
                     ledger.ReceivedAt, ledger.ProcessedAt!.Value, ledger.Timestamp!.Value, batch.ReceivedAt, batch.CompletedAt!.Value,
                 })
        {
            Assert.Equal(DateTimeKind.Utc, value.Kind);
        }
    }

    // ---- ST-06: a hard delete is physically true ---------------------------------------------------------

    private static bool FileContains(string path, string marker)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) >= 0;
    }

    private static bool AnywhereInDatabase(string databasePath, string marker) =>
        FileContains(databasePath, marker) || FileContains(databasePath + "-wal", marker);

    private static void Checkpoint(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(0, reader.GetInt32(0)); // not busy: the whole WAL was folded in and truncated
    }

    [Fact]
    [Trait("req", "ST-06")]
    public void Without_secure_delete_sqlite_leaves_a_deleted_value_in_the_file_which_is_why_it_is_switched_on()
    {
        // A control for the test below: the default really does leave bytes behind, so that test is able to fail.
        Assert.True(MarkerSurvivesDelete(secureDelete: false), "expected the default to leave the deleted value in the file");
        Assert.False(MarkerSurvivesDelete(secureDelete: true), "secure_delete should have wiped the deleted value");
    }

    /// <summary>Inserts a row among neighbours, deletes it, folds the WAL into the file, and looks for the row's text.</summary>
    private static bool MarkerSurvivesDelete(bool secureDelete)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lateral-control-{Guid.NewGuid():N}.db");
        var marker = "control-marker-" + Guid.NewGuid();
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                // Neighbouring rows keep the page alive, so the deleted row's bytes are left in its free space.
                command.CommandText =
                    $"PRAGMA journal_mode=WAL; PRAGMA secure_delete={(secureDelete ? "ON" : "OFF")}; " +
                    "CREATE TABLE t(v TEXT); " +
                    "INSERT INTO t VALUES ('filler-before-1'), ('filler-before-2'), ('filler-before-3'); " +
                    $"INSERT INTO t VALUES ('{marker}'); " +
                    "INSERT INTO t VALUES ('filler-after-1'), ('filler-after-2'), ('filler-after-3'); " +
                    $"DELETE FROM t WHERE v = '{marker}';";
                command.ExecuteNonQuery();
            }

            Checkpoint(path);
            return AnywhereInDatabase(path, marker);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(path + suffix);
                }
                catch (IOException)
                {
                    // best effort
                }
            }
        }
    }

    [Fact]
    [Trait("req", "ST-06")]
    public async Task Secure_delete_is_on_for_the_connections_of_both_contexts()
    {
        using var scope = Scope();

        foreach (var db in new CmsDbContext[] { scope.ServiceProvider.GetRequiredService<CmsWriteDbContext>(), scope.ServiceProvider.GetRequiredService<CmsReadDbContext>() })
        {
            var value = (await db.Database.SqlQueryRaw<int>("SELECT secure_delete AS Value FROM pragma_secure_delete").ToListAsync()).Single();
            Assert.Equal(1, value);
        }
    }

    [Fact]
    [Trait("req", "ST-06")]
    [Trait("req", "EV-06")]
    public async Task After_a_delete_and_a_checkpoint_the_deleted_entitys_content_is_in_neither_the_file_nor_the_wal()
    {
        var id = Evt.Id();
        var marker = "wipe-marker-" + Guid.NewGuid().ToString("N");

        // Queued: the content is in the Events row, and so in the file.
        await factory.AcceptAsync(Evt.Publish(id, 1, 1, new { secret = marker }));
        Checkpoint(factory.DatabasePath);
        Assert.True(AnywhereInDatabase(factory.DatabasePath, marker), "the queued payload should be on disk");

        // Applied: the Events payload is nulled but the Entities row now holds it.
        await factory.DrainAsync();
        Checkpoint(factory.DatabasePath);
        Assert.True(AnywhereInDatabase(factory.DatabasePath, marker), "the entity row should hold the payload");
        Assert.Equal(0, factory.Count($"SELECT COUNT(*) FROM Events WHERE Payload LIKE '%{marker}%'"));

        // Replaced by a later version: the old text is overwritten, not left behind in a free page.
        var replacedMarker = "replaced-marker-" + Guid.NewGuid().ToString("N");
        await factory.SendAsync(Evt.Publish(id, 2, 2, new { secret = replacedMarker }));
        Checkpoint(factory.DatabasePath);
        Assert.False(AnywhereInDatabase(factory.DatabasePath, marker), "the superseded payload survived an update");
        Assert.True(AnywhereInDatabase(factory.DatabasePath, replacedMarker));

        // Deleted: nothing remains.
        await factory.SendAsync(Evt.Delete(id, 3));
        Checkpoint(factory.DatabasePath);
        Assert.False(AnywhereInDatabase(factory.DatabasePath, replacedMarker), "the deleted entity's payload survived in the file");
        Assert.False(AnywhereInDatabase(factory.DatabasePath, marker));
    }
}
