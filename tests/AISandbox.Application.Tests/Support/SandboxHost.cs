using AISandbox.Application;
using AISandbox.Application.Abstractions;
using AISandbox.Application.Features.Runs;
using AISandbox.Domain.Experimentation;
using AISandbox.Domain.Abstractions;
using AISandbox.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AISandbox.Application.Tests.Support;

/// <summary>
/// The primary test seam: real Application and Infrastructure wiring over an in-memory SQLite
/// database. Tests talk to it only through use cases and the database file it would write.
/// </summary>
public sealed class SandboxHost : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly ServiceProvider _services;

    private readonly IReadOnlyList<IHostedService> _workers;

    private SandboxHost(SqliteConnection keepAlive, ServiceProvider services, IReadOnlyList<IHostedService> workers)
    {
        _keepAlive = keepAlive;
        _services = services;
        _workers = workers;
    }

    public SqliteConnection Database => _keepAlive;

    public IServiceProvider Services => _services;

    public static async Task<SandboxHost> StartAsync(IDictionary<string, string?>? settings = null)
    {
        var connectionString = $"Data Source=sandbox-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ProviderResilience:BaseDelay"] = "00:00:00.010" })
            .AddInMemoryCollection(new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>())
            {
                [$"ConnectionStrings:{Infrastructure.DependencyInjection.ConnectionStringName}"] = connectionString,
            })
            .Build();

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddApplication()
            .AddInfrastructure(configuration)
            .AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        await services.MigrateDatabaseAsync();
        var workers = services.GetServices<IHostedService>().ToList();
        foreach (var worker in workers)
        {
            await worker.StartAsync(CancellationToken.None);
        }

        return new SandboxHost(keepAlive, services, workers);
    }

    public async Task<Result<TResult>> SendAsync<TCommand, TResult>(TCommand command)
    {
        await using var scope = _services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResult>>();
        return await handler.HandleAsync(command, CancellationToken.None);
    }

    /// <summary>
    /// Starts a run and waits until every execution has finished, the way a caller that does not
    /// watch live progress would. Failed commands return straight away.
    /// </summary>
    public async Task<Result<RunId>> RunToCompletionAsync(StartRun command)
    {
        var result = await SendAsync<StartRun, RunId>(command);
        if (result.IsSuccess)
        {
            await WaitForCompletionAsync(result.Value);
        }

        return result;
    }

    public Task WaitForCompletionAsync(RunId runId) =>
        _services.GetRequiredService<IRunScheduler>().WaitForCompletionAsync(runId).WaitAsync(TimeSpan.FromSeconds(30));

    public IRunNotifier Notifier => _services.GetRequiredService<IRunNotifier>();

    public async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
    {
        await using var scope = _services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>();
        return await handler.HandleAsync(query, CancellationToken.None);
    }

    public async Task<IReadOnlyList<string>> ReadColumnAsync(string sql)
    {
        await using var command = _keepAlive.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in _workers)
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await _services.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }
}
