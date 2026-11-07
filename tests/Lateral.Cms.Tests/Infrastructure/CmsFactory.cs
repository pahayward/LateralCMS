using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lateral.Cms.Assets;
using Lateral.Cms.Ingestion;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lateral.Cms.Tests.Infrastructure;

/// <summary>
/// Hosts the real application in memory against a throwaway SQLite file, with known test credentials and a clock that
/// only moves when the test moves it. The background worker is off unless a test asks for it, so a test decides exactly
/// when queued batches are applied (<see cref="StepAsync"/>, <see cref="DrainAsync"/>) and never has to sleep.
/// Environment "Testing" means the Development environment's generated credentials are not used; the accounts below are.
/// </summary>
public class CmsFactory : WebApplicationFactory<Program>
{
    public const string CmsUser = "cms-ingest-svc";
    public const string UserName = "user@test.example";
    public const string AdminName = "admin@test.example";

    public static readonly string CmsPassword = Guid.NewGuid().ToString();
    public static readonly string CmsRotatedPassword = Guid.NewGuid().ToString();
    public static readonly string UserPassword = "user-" + Guid.NewGuid();
    public static readonly string AdminPassword = "admin-" + Guid.NewGuid();

    private readonly string _databasePath;
    private readonly bool _ownsDatabase;

    public CmsFactory()
        : this(Path.Combine(Path.GetTempPath(), $"lateral-test-{Guid.NewGuid():N}.db"), ownsDatabase: true)
    {
    }

    /// <summary>
    /// Hosts the app on an existing database file, as a restart would. Whoever created the file deletes it; this
    /// instance leaves it behind for the next one.
    /// </summary>
    protected CmsFactory(string databasePath, bool ownsDatabase)
    {
        _databasePath = databasePath;
        _ownsDatabase = ownsDatabase;
    }

    public CapturingLoggerProvider Logs { get; } = new();

    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero));

    public string DatabasePath => _databasePath;

    /// <summary>Where this factory's asset bytes live (its own folder, removed on dispose).</summary>
    public string AssetsPath => _databasePath + "-assets";

    /// <summary>Whether the real asset scan worker and sweeper run. Off by default; tests drive the processors themselves.</summary>
    protected virtual bool AssetWorkerEnabled => false;

    /// <summary>Whether the real background worker runs. Off by default; tests drive the processor themselves.</summary>
    protected virtual bool WorkerEnabled => false;

    /// <summary>Extra or overriding configuration for a specific test class.</summary>
    protected virtual IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        var settings = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = $"Data Source={_databasePath}",
            ["Auth:Cms:Username"] = CmsUser,
            ["Auth:Cms:Passwords:0"] = CmsPassword,
            ["Auth:Cms:Passwords:1"] = CmsRotatedPassword,
            ["Auth:Users:0:Username"] = UserName,
            ["Auth:Users:0:Password"] = UserPassword,
            ["Auth:Users:0:Role"] = "User",
            ["Auth:Users:1:Username"] = AdminName,
            ["Auth:Users:1:Password"] = AdminPassword,
            ["Auth:Users:1:Role"] = "Admin",
            // Many tests deliberately fail to sign in; the throttle has its own dedicated tests.
            ["Auth:MaxFailuresPerMinute"] = "0",
            ["Assets:Root"] = AssetsPath,
            ["Assets:WorkerEnabled"] = AssetWorkerEnabled ? "true" : "false",
            ["Inbox:WorkerEnabled"] = WorkerEnabled ? "true" : "false",
            ["Inbox:PollInterval"] = "00:00:00.050",
            ["Inbox:FaultDelay"] = "00:00:00.050",
            ["Inbox:RetryBaseDelay"] = "00:00:10",
            ["Inbox:RetryMaxDelay"] = "00:01:00",
            ["Inbox:MaxAttempts"] = "3",
        };
        foreach (var (key, value) in ExtraSettings)
        {
            settings[key] = value;
        }

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }

    public HttpClient ClientFor(string username, string password)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = Basic(username, password);
        return client;
    }

    public HttpClient Cms() => ClientFor(CmsUser, CmsPassword);

    public HttpClient User() => ClientFor(UserName, UserPassword);

    public HttpClient Admin() => ClientFor(AdminName, AdminPassword);

    public static AuthenticationHeaderValue Basic(string username, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

    // ---- Driving the pipeline ----

    /// <summary>Applies the next due batch, as the worker would.</summary>
    public async Task<ProcessStep> StepAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BatchProcessor>().ProcessNextAsync(CancellationToken.None);
    }

    /// <summary>Refreshes the backlog figures the queue gauges report, as the worker does after each turn.</summary>
    public async Task RefreshBacklogAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BatchProcessor>().RefreshBacklogAsync(CancellationToken.None);
    }

    /// <summary>Scans the next quarantined asset that is due, as the scan worker would.</summary>
    public async Task<ScanStep> ScanStepAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ScanProcessor>().ProcessNextAsync(CancellationToken.None);
    }

    /// <summary>Scans every asset that is due. Returns the steps taken, in order.</summary>
    public async Task<List<ScanStep>> ScanAllAsync()
    {
        var steps = new List<ScanStep>();
        for (var i = 0; i < 1_000; i++)
        {
            var step = await ScanStepAsync();
            if (step == ScanStep.Idle)
            {
                return steps;
            }

            steps.Add(step);
        }

        throw new InvalidOperationException("The scan queue never went idle.");
    }

    /// <summary>Runs one pass of the asset sweeper.</summary>
    public async Task<SweepReport> SweepAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AssetSweepService>().SweepOnceAsync(CancellationToken.None);
    }

    /// <summary>Applies every batch that is due. Returns how many it applied.</summary>
    public async Task<int> DrainAsync()
    {
        var applied = 0;
        for (var i = 0; i < 10_000; i++)
        {
            var step = await StepAsync();
            if (step == ProcessStep.Idle)
            {
                return applied;
            }

            if (step == ProcessStep.Applied)
            {
                applied++;
            }
        }

        throw new InvalidOperationException("The queue never went idle.");
    }

    /// <summary>Sends a batch that must be queued (202) and returns the acknowledgement.</summary>
    public async Task<JsonDocument> AcceptAsync(params object[] events)
    {
        var response = await Cms().PostAsync(events);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"Expected 202, got {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// Posts a batch, applies everything that is due, and returns the batch's final status: the helper rule-level tests
    /// use. The body has the same shape as the acknowledgement, with each event's final outcome.
    /// </summary>
    public async Task<JsonDocument> SendAsync(params object[] events)
    {
        var response = await Cms().PostAsync(events);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected 2xx, got {(int)response.StatusCode}: {body}{Errors()}");
        using var acknowledgement = JsonDocument.Parse(body);
        var batchId = BatchId(acknowledgement);
        await DrainAsync();
        return await StatusAsync(batchId);
    }

    /// <summary>Error-level log lines so far, appended to failure messages so a 500 explains itself.</summary>
    public string Errors() =>
        string.Concat(Logs.Entries.Where(e => e.Level >= LogLevel.Error).TakeLast(8).Select(e => $"{Environment.NewLine}  [log] {e.Message} {e.Exception}"));

    public static string BatchId(JsonDocument acknowledgement) => acknowledgement.RootElement.GetProperty("batchId").GetString()!;

    /// <summary>What the CMS sees when it asks after a batch.</summary>
    public async Task<JsonDocument> StatusAsync(string batchId)
    {
        var response = await Cms().GetAsync($"/cms/batches/{batchId}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected 200, got {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    public async Task<string> StatusNameAsync(string batchId)
    {
        using var status = await StatusAsync(batchId);
        return status.RootElement.GetProperty("status").GetString()!;
    }

    // ---- Looking at the database directly ----

    /// <summary>Runs SQL straight against the test database, bypassing the application.</summary>
    public List<object?[]> Sql(string commandText)
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        using var reader = command.ExecuteReader();

        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public long Count(string sql) => Convert.ToInt64(Sql(sql)[0][0]);

    /// <summary>Waits (without a fixed sleep) until the condition holds, for tests with the live worker.</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }

    private static void ClearPool(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        // Close only this database's pooled connections. ClearAllPools() is process-wide, so one test class finishing
        // would pull connections out from under the others still running (an ObjectDisposedException, as a 500).
        foreach (var mode in new[] { SqliteOpenMode.ReadWriteCreate, SqliteOpenMode.ReadOnly })
        {
            ClearPool(new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = mode }.ToString());
        }

        ClearPool($"Data Source={_databasePath}");
        if (!_ownsDatabase)
        {
            return;
        }

        try
        {
            if (Directory.Exists(AssetsPath))
            {
                Directory.Delete(AssetsPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: it is a temp folder.
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_databasePath + suffix);
            }
            catch (IOException)
            {
                // Best effort: it is a temp file.
            }
        }
    }
}

/// <summary>Collects everything the application logs so tests can assert on it.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public sealed record Entry(string Category, LogLevel Level, string Message, string? Exception);

    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyCollection<Entry> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<Entry> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue(new Entry(
                category,
                logLevel,
                formatter(state, exception),
                (exception ?? (state as Microsoft.EntityFrameworkCore.Diagnostics.IErrorEventData)?.Exception)?.ToString()
                    ?? (logLevel >= LogLevel.Error ? "[state " + state?.GetType().FullName + " event " + eventId + "] " + string.Join("; ", (state as System.Collections.IEnumerable)?.Cast<object>().Select(o => o.ToString()) ?? []) : null)));
    }
}
