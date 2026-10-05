using AISandbox.Application.Abstractions;
using AISandbox.Application.Features.Models;
using AISandbox.Application.Features.Providers;
using AISandbox.Application.Features.Runs;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;
using AISandbox.Domain.Experimentation;
using Microsoft.EntityFrameworkCore;

namespace AISandbox.Infrastructure.Persistence;

internal sealed class ProviderRepository(AppDbContext db) : IProviderRepository
{
    public void Add(Provider provider) => db.Providers.Add(provider);

    public Task<Provider?> GetAsync(ProviderId id, CancellationToken cancellationToken) =>
        db.Providers.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
}

internal sealed class ProviderQueries(AppDbContext db) : IProviderQueries
{
    public async Task<IReadOnlyList<ProviderRow>> ListAsync(CancellationToken cancellationToken)
    {
        var providers = await db.Providers.AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);

        return providers
            .Select(p => new ProviderRow(p.Id, p.Name, p.Kind, p.BaseUrl.ToString(), p.Auth.Kind, p.Secret))
            .ToList();
    }
}

internal sealed class ModelDefinitionRepository(AppDbContext db) : IModelDefinitionRepository
{
    public void Add(ModelDefinition model) => db.ModelDefinitions.Add(model);

    public async Task<IReadOnlyList<ModelDefinition>> GetManyAsync(
        IReadOnlyCollection<ModelDefinitionId> ids,
        CancellationToken cancellationToken) =>
        await db.ModelDefinitions.Where(m => ids.Contains(m.Id)).ToListAsync(cancellationToken);
}

internal sealed class ModelQueries(AppDbContext db) : IModelQueries
{
    public async Task<IReadOnlyList<ModelSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await db.ModelDefinitions.AsNoTracking()
            .Join(db.Providers.AsNoTracking(), m => m.ProviderId, p => p.Id, (m, p) => new { Model = m, ProviderName = p.Name })
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(r => r.Model.Kind)
            .ThenBy(r => r.Model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(r => new ModelSummary(
                r.Model.Id,
                r.Model.DisplayName,
                r.Model.Kind,
                r.Model.Protocol.Value,
                r.Model.RemoteId.Value,
                r.ProviderName,
                r.Model.Pricing,
                r.Model.Capabilities,
                r.Model.Origin))
            .ToList();
    }
}

internal sealed class RunRepository(AppDbContext db) : IRunRepository
{
    public void Add(Run run) => db.Runs.Add(run);

    public Task<Run?> GetAsync(RunId id, CancellationToken cancellationToken) =>
        db.Runs.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
}

internal sealed class RunQueries(AppDbContext db) : IRunQueries
{
    private const int PreviewLength = 120;

    public async Task<IReadOnlyList<RunSummary>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        var runs = await db.Runs.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return runs.Select(r =>
        {
            var state = r.Input.State.Display;
            var costs = r.Executions.Where(e => e.Cost is not null).Select(e => e.Cost!).ToList();
            Money? total = costs.Count == 0 || costs.Select(c => c.Currency).Distinct().Count() > 1
                ? null
                : costs.Aggregate((a, b) => a + b);
            return new RunSummary(
                r.Id,
                r.CreatedAt,
                r.Status,
                state.Length > PreviewLength ? state[..PreviewLength] + "…" : state,
                r.Executions.Select(e => e.Model.DisplayName).ToList(),
                total);
        }).ToList();
    }
}
