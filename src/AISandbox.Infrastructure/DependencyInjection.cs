using AISandbox.Application.Abstractions;
using AISandbox.Application.Features.Providers;
using AISandbox.Infrastructure.Persistence;
using AISandbox.Infrastructure.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AISandbox.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "AISandbox";
    public const string DefaultConnectionString = "Data Source=aisandbox.db";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName) ?? DefaultConnectionString;
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AppDbContext>());

        services.AddDataProtection().SetApplicationName("AISandbox");

        services.AddScoped<IProviderRepository, ProviderRepository>();
        services.AddScoped<IProviderQueries, ProviderQueries>();
        services.AddScoped<ISecretStore, DataProtectionSecretStore>();

        return services;
    }

    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}
