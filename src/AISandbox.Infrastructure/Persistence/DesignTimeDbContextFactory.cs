using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AISandbox.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> create migrations without starting the web host.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(DependencyInjection.DefaultConnectionString)
            .Options);
}
