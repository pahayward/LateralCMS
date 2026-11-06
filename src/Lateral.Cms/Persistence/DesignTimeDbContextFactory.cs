using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lateral.Cms.Persistence;

/// <summary>
/// Lets `dotnet ef` build the context without starting the web host (which would run startup code,
/// including migrations, against the real database).
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CmsWriteDbContext>
{
    public CmsWriteDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CmsWriteDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
