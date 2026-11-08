using Lateral.Cms.TestConsole;

var builder = WebApplication.CreateBuilder(args);

// Loopback only, whatever ASPNETCORE_URLS says: the console holds CMS credentials and acts with them.
builder.WebHost.ConfigureKestrel((context, kestrel) =>
    kestrel.ListenLocalhost(context.Configuration.GetValue($"{ConsoleOptions.SectionName}:Port", 5100)));

builder.Services.AddOptions<ConsoleOptions>()
    .BindConfiguration(ConsoleOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// No people configured: make (or read back) a random GUID password each for admin and user. Never a password written in the repository.
builder.Services.PostConfigure<ConsoleOptions>(options =>
{
    if (options.SignIn.Enabled && options.SignIn.People.Count == 0)
    {
        var path = options.SignIn.CredentialsPath is { Length: > 0 } custom
            ? Path.GetFullPath(custom, builder.Environment.ContentRootPath)
            : SignInCredentials.PathIn(builder.Environment.ContentRootPath);
        options.SignIn.People = SignInCredentials.LoadOrCreate(path);
        options.SignIn.CredentialsPath = path;
    }
});

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

if (app.Configuration.GetValue<string>($"{ConsoleOptions.SectionName}:PathBase") is { Length: > 0 } pathBase)
{
    app.UsePathBase(pathBase.TrimEnd('/'));
}

// A tool that holds credentials and can erase data does not run in a deployed environment by accident.
if (!app.Environment.IsDevelopment() && !app.Configuration.GetValue<bool>($"{ConsoleOptions.SectionName}:AllowNonDevelopment"))
{
    throw new InvalidOperationException(
        $"The test console only runs in the Development environment (this is '{app.Environment.EnvironmentName}'). " +
        $"Set {ConsoleOptions.SectionName}:AllowNonDevelopment=true to run it anyway.");
}

// Behind a reverse proxy or a tunnel (nginx, ngrok) the page is https while this hop is http: believe the proxy about the scheme so the
// session cookie gets its Secure flag. Only a proxy on this machine is believed (the default).
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto });
app.UseSecurityHeaders();
app.UseLocalRequestGuard();
app.UseSignIn();
app.UseDefaultFiles();
// A development tool: always ask whether a script or style has changed rather than trusting a cached copy.
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache" });

// "/" is the photo library (the sample application); the engineering console that came first lives at /engineering.
app.MapGet("/engineering", (IWebHostEnvironment environment) =>
    environment.WebRootFileProvider.GetFileInfo("engineering.html") is { Exists: true } page
        ? Results.Stream(page.CreateReadStream(), "text/html; charset=utf-8")
        : Results.NotFound());
app.MapSignIn();
app.MapConsoleEndpoints();
app.MapMediaEndpoints();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ConsoleOptions>>().Value;
    var accounts = app.Services.GetRequiredService<AccountBook>();
    if (options.SignIn.Enabled)
    {
        app.Logger.LogInformation("Website sign-in is on. Names: {Names}. Passwords: {Where}", string.Join(", ", options.SignIn.People.Select(p => p.Name)),
            options.SignIn.CredentialsPath is { } where ? $"in {where}" : "as configured");
    }
    else
    {
        app.Logger.LogWarning("Website sign-in is OFF (Console:SignIn:Enabled=false). Do not expose this to anyone you do not trust.");
    }

    app.Logger.LogInformation(
        "CMS test console at http://localhost:{Port}, testing the CMS at {CmsBaseUrl}. Accounts available: {Accounts}",
        options.Port, options.CmsBaseUrl, string.Join(", ", accounts.Configured.Order()));
});

app.Run();

public partial class Program;
