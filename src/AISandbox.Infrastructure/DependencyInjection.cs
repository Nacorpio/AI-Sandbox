using AISandbox.Application.Abstractions;
using AISandbox.Application.Features.Models;
using AISandbox.Application.Features.Providers;
using AISandbox.Application.Features.QuestionSets;
using AISandbox.Application.Features.Runs;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Infrastructure.Persistence;
using AISandbox.Infrastructure.Protocols;
using AISandbox.Infrastructure.Protocols.SystemOne;
using AISandbox.Infrastructure.Runs;
using AISandbox.Infrastructure.Secrets;
using AISandbox.Infrastructure.Templates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AISandbox.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "AISandbox";
    public const string DefaultConnectionString = "Data Source=aisandbox.db";
    private static readonly TimeSpan ConnectionTestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(60);

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName) ?? DefaultConnectionString;
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AppDbContext>());

        services.AddDataProtection().SetApplicationName("AISandbox");
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IProviderRepository, ProviderRepository>();
        services.AddScoped<IProviderQueries, ProviderQueries>();
        services.AddScoped<ISecretStore, DataProtectionSecretStore>();

        services.AddSingleton<ITemplateCatalog, EmbeddedTemplateCatalog>();
        services.AddScoped<IModelDefinitionRepository, ModelDefinitionRepository>();
        services.AddScoped<IModelQueries, ModelQueries>();
        services.AddScoped<IQuestionSetRepository, QuestionSetRepository>();
        services.AddScoped<IQuestionSetQueries, QuestionSetQueries>();
        services.AddScoped<IRunRepository, RunRepository>();
        services.AddScoped<IRunQueries, RunQueries>();

        services.AddOptions<ProviderLimitsOptions>()
            .Bind(configuration.GetSection(ProviderLimitsOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ProviderLimitsOptions>, ProviderLimitsOptionsValidator>();
        services.AddSingleton<IProviderLimiter, ProviderLimiter>();
        services.AddSingleton<IRunNotifier, RunNotifier>();
        services.AddSingleton<RunScheduler>();
        services.AddSingleton<IRunScheduler>(sp => sp.GetRequiredService<RunScheduler>());
        services.AddHostedService<RunWorker>();

        services.AddHttpClient(ProviderHttp.ClientName, client => client.Timeout = ProviderTimeout);
        services.AddSingleton<ProviderHttp>();
        services.AddHttpClient(ProviderConnectionTester.ClientName, client => client.Timeout = ConnectionTestTimeout);
        services.AddSingleton<IProviderConnectionTester, ProviderConnectionTester>();
        services.AddSingleton<IModelInvokerResolver, ModelInvokerResolver>();
        services.AddKeyedSingleton<IModelInvoker, SystemOneInvoker>(ProtocolId.SystemOne.Value);
        services.AddKeyedSingleton<IModelInvoker, OpenRouterDecisionsInvoker>(ProtocolId.OpenRouterDecisions.Value);
        services.AddKeyedSingleton<IModelInvoker, WorkersAiInvoker>(ProtocolId.WorkersAi.Value);

        return services;
    }

    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}
