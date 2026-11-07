using Lateral.Cms.TestConsole;

var builder = WebApplication.CreateBuilder(args);

// Loopback only, whatever ASPNETCORE_URLS says: the console holds CMS credentials and acts with them.
builder.WebHost.ConfigureKestrel((context, kestrel) =>
    kestrel.ListenLocalhost(context.Configuration.GetValue($"{ConsoleOptions.SectionName}:Port", 5100)));

builder.Services.AddOptions<ConsoleOptions>()
    .BindConfiguration(ConsoleOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<AccountBook>();
builder.Services.AddHttpClient("cms")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    })
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMinutes(2));

var app = builder.Build();

// A tool that holds credentials and can erase data does not run in a deployed environment by accident.
if (!app.Environment.IsDevelopment() && !app.Configuration.GetValue<bool>($"{ConsoleOptions.SectionName}:AllowNonDevelopment"))
{
    throw new InvalidOperationException(
        $"The test console only runs in the Development environment (this is '{app.Environment.EnvironmentName}'). " +
        $"Set {ConsoleOptions.SectionName}:AllowNonDevelopment=true to run it anyway.");
}

app.UseSecurityHeaders();
app.UseLocalRequestGuard();
app.UseDefaultFiles();
// A development tool: always ask whether a script or style has changed rather than trusting a cached copy.
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache" });

// "/" is the photo library (the sample application); the engineering console that came first lives at /engineering.
app.MapGet("/engineering", (IWebHostEnvironment environment) =>
    environment.WebRootFileProvider.GetFileInfo("engineering.html") is { Exists: true } page
        ? Results.Stream(page.CreateReadStream(), "text/html; charset=utf-8")
        : Results.NotFound());
app.MapConsoleEndpoints();
app.MapMediaEndpoints();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ConsoleOptions>>().Value;
    var accounts = app.Services.GetRequiredService<AccountBook>();
    app.Logger.LogInformation(
        "CMS test console at http://localhost:{Port}, testing the CMS at {CmsBaseUrl}. Accounts available: {Accounts}",
        options.Port, options.CmsBaseUrl, string.Join(", ", accounts.Configured.Order()));
});

app.Run();

public partial class Program;
