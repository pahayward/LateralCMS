// Load smoke. Starts the BUILT service on a loopback port with generated credentials and a temporary database, drives it
// with concurrent senders, and prints throughput and latency. It measures; it does not assert. Numbers from one machine and
// one run are an order of magnitude, not a guarantee, and belong in docs/EVIDENCE.md with the machine and date.
//
//   dotnet build -c Release
//   dotnet run -c Release --project tools/Lateral.Cms.LoadSmoke -- --senders 8 --batches 25 --events 100 --payload 1024 --log Warning
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var options = Options.Parse(args);
var repoRoot = FindRepoRoot();
var servicePath = options.Service ?? Path.Combine(repoRoot, "src", "Lateral.Cms", "bin", "Release", "net9.0", "Lateral.Cms.dll");
if (!File.Exists(servicePath))
{
    Console.Error.WriteLine($"The built service was not found at {servicePath}. Run 'dotnet build -c Release' first, or pass --service.");
    return 2;
}

var cmsUser = "load-" + Guid.NewGuid().ToString("N")[..12];
var cmsPassword = Guid.NewGuid().ToString();
var userName = "reader@load.test";
var userPassword = "reader-" + Guid.NewGuid();
var adminName = "admin@load.test";
var adminPassword = "admin-" + Guid.NewGuid();
var databasePath = Path.Combine(Path.GetTempPath(), $"lateral-load-{Guid.NewGuid():N}.db");
var baseUrl = $"http://127.0.0.1:{options.Port}";

var startInfo = new ProcessStartInfo("dotnet", $"\"{servicePath}\"")
{
    WorkingDirectory = Path.GetDirectoryName(servicePath)!,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};
var environment = startInfo.Environment;
environment["DOTNET_ROLL_FORWARD"] = "Major";
environment["ASPNETCORE_URLS"] = baseUrl;
// Not Development (that would read the development credentials) and not Production (that would redirect to https).
environment["ASPNETCORE_ENVIRONMENT"] = "Staging";
environment["Database__ConnectionString"] = $"Data Source={databasePath}";
environment["Auth__Cms__Username"] = cmsUser;
environment["Auth__Cms__Passwords__0"] = cmsPassword;
environment["Auth__Users__0__Username"] = userName;
environment["Auth__Users__0__Password"] = userPassword;
environment["Auth__Users__0__Role"] = "User";
environment["Auth__Users__1__Username"] = adminName;
environment["Auth__Users__1__Password"] = adminPassword;
environment["Auth__Users__1__Role"] = "Admin";
environment["Auth__MaxFailuresPerMinute"] = "0";
environment["Logging__LogLevel__Default"] = options.LogLevel;
environment["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning";

using var service = Process.Start(startInfo)!;
var logLines = 0L;
service.OutputDataReceived += (_, e) => { if (e.Data is not null) Interlocked.Increment(ref logLines); };
service.ErrorDataReceived += (_, e) => { if (e.Data is not null) Interlocked.Increment(ref logLines); };
service.BeginOutputReadLine();
service.BeginErrorReadLine();

try
{
    using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 256 }) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(2) };

    if (!await WaitForHealthAsync(http, service))
    {
        Console.Error.WriteLine("The service did not become healthy.");
        return 3;
    }

    var cms = Basic(cmsUser, cmsPassword);
    var user = Basic(userName, userPassword);
    var admin = Basic(adminName, adminPassword);
    var payload = new string('x', Math.Max(0, options.PayloadBytes - 24));
    var latencies = new List<double>(options.Senders * options.Batches);
    var latencyLock = new object();
    long accepted = 0, refused = 0;
    var runId = Guid.NewGuid().ToString("N")[..8];

    Console.WriteLine($"Service {baseUrl} (log level {options.LogLevel}). {options.Senders} senders x {options.Batches} batches x {options.Events} events, ~{options.PayloadBytes} B payloads.");
    var started = Stopwatch.GetTimestamp();

    await Task.WhenAll(Enumerable.Range(0, options.Senders).Select(sender => Task.Run(async () =>
    {
        for (var b = 0; b < options.Batches; b++)
        {
            var events = Enumerable.Range(0, options.Events).Select(e => new
            {
                type = "publish",
                id = $"load-{runId}-{sender}-{b}-{e}",
                version = 1,
                payload = new { body = payload },
                timestamp = "2024-01-01T00:00:00Z",
            });
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/cms/events")
                {
                    Content = new StringContent(JsonSerializer.Serialize(events), Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = cms;
                var requestStarted = Stopwatch.GetTimestamp();
                using var response = await http.SendAsync(request);
                var elapsed = Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds;
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    Interlocked.Increment(ref refused);
                    await Task.Delay(500);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                lock (latencyLock) latencies.Add(elapsed);
                Interlocked.Add(ref accepted, options.Events);
                break;
            }
        }
    })));

    var acceptedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;

    // Wait for the queue to drain, as the admin report sees it.
    var drainDeadline = DateTime.UtcNow.AddMinutes(10);
    while (true)
    {
        using var report = await GetJsonAsync(http, "/api/admin/ingestion", admin);
        if (report.RootElement.GetProperty("pendingBatches").GetInt32() == 0) break;
        if (DateTime.UtcNow > drainDeadline) { Console.Error.WriteLine("The queue did not drain within 10 minutes."); return 4; }
        await Task.Delay(100);
    }

    var appliedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;

    // Read everything back as a user, page by page.
    var readStarted = Stopwatch.GetTimestamp();
    var pageTimes = new List<double>();
    var entities = 0;
    string? cursor = null;
    do
    {
        var pageStarted = Stopwatch.GetTimestamp();
        using var page = await GetJsonAsync(http, "/api/entities?limit=100" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"), user);
        pageTimes.Add(Stopwatch.GetElapsedTime(pageStarted).TotalMilliseconds);
        entities += page.RootElement.GetProperty("items").GetArrayLength();
        cursor = page.RootElement.GetProperty("nextCursor").GetString();
    }
    while (cursor is not null);
    var readSeconds = Stopwatch.GetElapsedTime(readStarted).TotalSeconds;

    latencies.Sort();
    pageTimes.Sort();
    Console.WriteLine();
    Console.WriteLine($"Accepted        {accepted:N0} events in {acceptedSeconds:F1} s = {accepted / acceptedSeconds:F0} events/s ({refused} refusals with 429)");
    Console.WriteLine($"Request latency p50 {Percentile(latencies, .50):F0} ms, p95 {Percentile(latencies, .95):F0} ms, p99 {Percentile(latencies, .99):F0} ms");
    Console.WriteLine($"Applied         queue empty {appliedSeconds:F1} s after the first request = {accepted / appliedSeconds:F0} events/s end to end");
    Console.WriteLine($"Read back       {entities:N0} entities in {pageTimes.Count} pages, {readSeconds:F2} s; page p50 {Percentile(pageTimes, .50):F1} ms, p95 {Percentile(pageTimes, .95):F1} ms");
    Console.WriteLine($"Log lines       {Interlocked.Read(ref logLines):N0} written by the service");
    Console.WriteLine($"Machine         {Environment.MachineName}, {Environment.ProcessorCount} logical cores, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, {DateTime.UtcNow:yyyy-MM-dd}");
    return entities == accepted ? 0 : 5;
}
finally
{
    try
    {
        if (!service.HasExited)
        {
            service.Kill(entireProcessTree: true);
            service.WaitForExit(5000);
        }
    }
    catch (InvalidOperationException)
    {
        // already gone
    }

    foreach (var suffix in new[] { "", "-wal", "-shm" })
    {
        try { File.Delete(databasePath + suffix); } catch (IOException) { }
    }
}

static AuthenticationHeaderValue Basic(string user, string password) =>
    new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

static async Task<JsonDocument> GetJsonAsync(HttpClient http, string path, AuthenticationHeaderValue auth)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, path);
    request.Headers.Authorization = auth;
    using var response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode();
    return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}

static async Task<bool> WaitForHealthAsync(HttpClient http, Process service)
{
    for (var i = 0; i < 200 && !service.HasExited; i++)
    {
        try
        {
            if ((await http.GetAsync("/health")).IsSuccessStatusCode) return true;
        }
        catch (HttpRequestException)
        {
            // not listening yet
        }

        await Task.Delay(100);
    }

    return false;
}

static double Percentile(List<double> sorted, double p) =>
    sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lateral.sln")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? Directory.GetCurrentDirectory();
}

internal sealed record Options(int Senders, int Batches, int Events, int PayloadBytes, string LogLevel, int Port, string? Service)
{
    public static Options Parse(string[] args)
    {
        string Value(string name, string fallback) =>
            Array.IndexOf(args, "--" + name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;

        return new Options(
            int.Parse(Value("senders", "8")), int.Parse(Value("batches", "25")), int.Parse(Value("events", "100")),
            int.Parse(Value("payload", "1024")), Value("log", "Warning"), int.Parse(Value("port", "5290")),
            Value("service", "") is { Length: > 0 } service ? service : null);
    }
}
