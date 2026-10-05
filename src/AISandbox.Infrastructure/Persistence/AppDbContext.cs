using AISandbox.Application.Abstractions;
using AISandbox.Domain.Authoring.Prompts;
using AISandbox.Domain.Authoring.QuestionSets;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;
using AISandbox.Domain.Experimentation;
using AISandbox.Infrastructure.Secrets;
using Microsoft.EntityFrameworkCore;

namespace AISandbox.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IUnitOfWork
{
    public DbSet<Provider> Providers => Set<Provider>();

    public DbSet<ModelDefinition> ModelDefinitions => Set<ModelDefinition>();

    public DbSet<QuestionSet> QuestionSets => Set<QuestionSet>();

    public DbSet<PromptTemplate> PromptTemplates => Set<PromptTemplate>();

    public DbSet<Run> Runs => Set<Run>();

    internal DbSet<StoredSecret> Secrets => Set<StoredSecret>();

    async Task IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken) =>
        await SaveChangesAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
