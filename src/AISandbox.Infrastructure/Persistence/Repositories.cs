using AISandbox.Application.Abstractions;
using AISandbox.Application.Features.Providers;
using AISandbox.Domain.Catalog.Providers;
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
