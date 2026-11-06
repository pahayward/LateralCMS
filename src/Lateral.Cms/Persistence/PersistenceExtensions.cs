using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>SQLite connection string for the writer. A relative path is resolved against the content root.</summary>
    [Required]
    public string ConnectionString { get; set; } = "";

    /// <summary>Optional separate connection string for the reader (e.g. a replica). Defaults to the writer's file, read-only.</summary>
    public string? ReadConnectionString { get; set; }

    public bool ApplyMigrationsOnStartup { get; set; } = true;
}

/// <summary>The resolved writer and reader connection strings.</summary>
public sealed class DatabaseConnections
{
    public DatabaseConnections(IOptions<DatabaseOptions> options, IHostEnvironment environment)
    {
        var settings = options.Value;

        var write = Resolve(settings.ConnectionString, environment.ContentRootPath);
        write.Mode = SqliteOpenMode.ReadWriteCreate;
        DataSource = write.DataSource;
        Write = write.ToString();

        var read = string.IsNullOrWhiteSpace(settings.ReadConnectionString)
            ? new SqliteConnectionStringBuilder(Write)
            : Resolve(settings.ReadConnectionString, environment.ContentRootPath);
        read.Mode = SqliteOpenMode.ReadOnly;
        Read = read.ToString();
    }

    public string Write { get; }

    public string Read { get; }

    /// <summary>Full path of the writer's database file.</summary>
    public string DataSource { get; }

    private static SqliteConnectionStringBuilder Resolve(string connectionString, string contentRoot)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (!Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.GetFullPath(Path.Combine(contentRoot, builder.DataSource));
        }

        return builder;
    }
}

/// <summary>
/// Turns on <c>secure_delete</c> for every connection that is opened. Without it SQLite leaves the bytes of a
/// deleted or overwritten value in the file, so a "hard delete" would be logical only. The setting belongs to the
/// connection, not the file, hence an interceptor rather than a one-off statement.
/// </summary>
public sealed class SecureDeleteInterceptor : DbConnectionInterceptor
{
    private const string Pragma = "PRAGMA secure_delete = ON;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragma;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragma;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public static class PersistenceExtensions
{
    public static IServiceCollection AddCmsPersistence(this IServiceCollection services)
    {
        services.AddOptions<DatabaseOptions>()
            .BindConfiguration(DatabaseOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<DatabaseConnections>();
        services.AddSingleton<WriteGate>();
        services.AddSingleton<SecureDeleteInterceptor>();

        services.AddDbContext<CmsWriteDbContext>((provider, options) =>
            options
                .UseSqlite(provider.GetRequiredService<DatabaseConnections>().Write)
                .AddInterceptors(provider.GetRequiredService<SecureDeleteInterceptor>()));

        services.AddDbContext<CmsReadDbContext>((provider, options) =>
            options
                .UseSqlite(provider.GetRequiredService<DatabaseConnections>().Read)
                .AddInterceptors(provider.GetRequiredService<SecureDeleteInterceptor>())
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

        return services;
    }

    /// <summary>
    /// Creates/migrates the database and switches it to WAL so readers never block the writer. Runs before the
    /// read-only connection is first opened, which needs the file to exist.
    /// </summary>
    public static async Task InitialiseDatabaseAsync(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        var connections = app.Services.GetRequiredService<DatabaseConnections>();

        Directory.CreateDirectory(Path.GetDirectoryName(connections.DataSource)!);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsWriteDbContext>();

        if (settings.ApplyMigrationsOnStartup)
        {
            await db.Database.MigrateAsync();
        }

        // Persistent property of the database file; harmless to repeat. synchronous is left at its default (FULL),
        // so an acknowledged batch is on disk.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }
}
