using AISandbox.Application.Abstractions;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Catalog.Providers;

namespace AISandbox.Application.Features.Providers;

/// <summary>
/// Edits a provider. An empty <paramref name="ApiKey"/> keeps the stored key; a value replaces it.
/// The kind cannot change.
/// </summary>
public sealed record UpdateProvider(
    ProviderId Id,
    string? Name,
    string? BaseUrl,
    AuthSchemeKind AuthKind,
    string? AuthHeaderName,
    string? ApiKey,
    IReadOnlyDictionary<string, string>? PathVariables = null,
    RateLimitSettings? RateLimit = null);

public sealed class UpdateProviderHandler(
    IProviderRepository providers,
    ISecretStore secrets,
    IProviderLimiter limiter,
    IUnitOfWork unitOfWork)
    : ICommandHandler<UpdateProvider, ProviderId>
{
    public async Task<Result<ProviderId>> HandleAsync(UpdateProvider command, CancellationToken cancellationToken)
    {
        var provider = await providers.GetAsync(command.Id, cancellationToken);
        if (provider is null)
        {
            return Error.NotFound("Provider");
        }

        var auth = AuthScheme.Create(command.AuthKind, command.AuthHeaderName);
        if (auth.IsFailure)
        {
            return auth.Error!;
        }

        var rateLimit = RateLimitSettings.ToPolicy(command.RateLimit);
        if (rateLimit.IsFailure)
        {
            return rateLimit.Error!;
        }

        var updated = provider.Update(
            command.Name,
            command.BaseUrl,
            auth.Value,
            ProviderDefaults.EnvironmentVariableFor(provider.Kind),
            command.PathVariables,
            rateLimit.Value);
        if (updated.IsFailure)
        {
            return updated.Error!;
        }

        if (!string.IsNullOrWhiteSpace(command.ApiKey))
        {
            await secrets.StoreAsync(provider.Secret, command.ApiKey.Trim(), cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        limiter.Invalidate(provider.Id);
        return provider.Id;
    }
}

public sealed record DeleteProvider(ProviderId Id);

public sealed class DeleteProviderHandler(
    IProviderRepository providers,
    IProviderQueries queries,
    ISecretStore secrets,
    IProviderLimiter limiter,
    IUnitOfWork unitOfWork)
    : ICommandHandler<DeleteProvider, ProviderId>
{
    public const string InUseCode = "provider.in_use";

    public async Task<Result<ProviderId>> HandleAsync(DeleteProvider command, CancellationToken cancellationToken)
    {
        var provider = await providers.GetAsync(command.Id, cancellationToken);
        if (provider is null)
        {
            return Error.NotFound("Provider");
        }

        var models = await queries.CountModelsAsync(provider.Id, cancellationToken);
        if (models > 0)
        {
            var noun = models == 1 ? "model definition uses" : "model definitions use";
            return new Error(
                InUseCode,
                $"{provider.Name} cannot be deleted: {models} {noun} it. Delete {(models == 1 ? "that model" : "those models")} first.");
        }

        providers.Remove(provider);
        await secrets.DeleteAsync(provider.Secret, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        limiter.Invalidate(provider.Id);
        return provider.Id;
    }
}

/// <summary>What a connection test found. Failing to connect is a result, not an error.</summary>
public sealed record ConnectionTestResult(bool Succeeded, string Message, int? StatusCode, TimeSpan Latency);

public sealed record ConnectionTestRequest(
    ProviderKind Kind,
    EndpointUri BaseUrl,
    AuthScheme Auth,
    IReadOnlyDictionary<string, string> PathVariables,
    string? ApiKey);

/// <summary>
/// Makes one cheap, read-only call to a provider to check its address and credentials.
/// Never throws for HTTP or network problems and never reveals the key.
/// </summary>
public interface IProviderConnectionTester
{
    Task<ConnectionTestResult> TestAsync(ConnectionTestRequest request, CancellationToken cancellationToken);
}

public sealed record TestProviderConnection(ProviderId Id);

public sealed class TestProviderConnectionHandler(
    IProviderRepository providers,
    ISecretStore secrets,
    IProviderConnectionTester tester)
    : ICommandHandler<TestProviderConnection, ConnectionTestResult>
{
    public async Task<Result<ConnectionTestResult>> HandleAsync(TestProviderConnection command, CancellationToken cancellationToken)
    {
        var provider = await providers.GetAsync(command.Id, cancellationToken);
        if (provider is null)
        {
            return Error.NotFound("Provider");
        }

        var key = await secrets.GetAsync(provider.Secret, cancellationToken);
        return await tester.TestAsync(
            new ConnectionTestRequest(provider.Kind, provider.BaseUrl, provider.Auth, provider.PathVariables, key),
            cancellationToken);
    }
}
