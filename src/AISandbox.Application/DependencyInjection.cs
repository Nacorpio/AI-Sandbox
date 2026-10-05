using AISandbox.Application.Abstractions;
using AISandbox.Application.Features.Providers;
using AISandbox.Domain.Catalog.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AISandbox.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<ICommandHandler<RegisterProvider, ProviderId>, RegisterProviderHandler>();
        services.AddScoped<IQueryHandler<ListProviders, IReadOnlyList<ProviderSummary>>, ListProvidersHandler>();

        return services;
    }
}
