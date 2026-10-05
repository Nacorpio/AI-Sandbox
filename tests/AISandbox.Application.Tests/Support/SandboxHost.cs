using AISandbox.Application;
using AISandbox.Application.Abstractions;
using AISandbox.Domain.Abstractions;
using AISandbox.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AISandbox.Application.Tests.Support;

/// <summary>
/// The primary test seam: real Application and Infrastructure wiring over an in-memory SQLite
/// database. Tests talk to it only through use cases and the database file it would write.
/// </summary>
public sealed class SandboxHost : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly ServiceProvider _services;

    private SandboxHost(SqliteConnection keepAlive, ServiceProvider services)
    {
        _keepAlive = keepAlive;
        _services = services;
    }

    public SqliteConnection Database => _keepAlive;

    public static async Task<SandboxHost> StartAsync(IDictionary<string, string?>? settings = null)
    {
        var connectionString = $"Data Source=sandbox-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var configuration = new ConfigurationBuilder()
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
        return new SandboxHost(keepAlive, services);
    }

    public async Task<Result<TResult>> SendAsync<TCommand, TResult>(TCommand command)
    {
        await using var scope = _services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResult>>();
        return await handler.HandleAsync(command, CancellationToken.None);
    }

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
        await _services.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }
}
