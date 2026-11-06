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

app.UseSecurityHeaders();
app.UseLocalRequestGuard();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapConsoleEndpoints();

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
