using AISandbox.Application.Abstractions;
using AISandbox.Domain.Catalog.Providers;
using AISandbox.Infrastructure.Secrets;
using Microsoft.EntityFrameworkCore;

namespace AISandbox.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IUnitOfWork
{
    public DbSet<Provider> Providers => Set<Provider>();

    internal DbSet<StoredSecret> Secrets => Set<StoredSecret>();

    async Task IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken) =>
        await SaveChangesAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
