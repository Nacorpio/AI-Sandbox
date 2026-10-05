using AISandbox.Application.Abstractions;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Catalog.Providers;

namespace AISandbox.Application.Features.Providers;

public sealed record RegisterProvider(
    string? Name,
    ProviderKind Kind,
    string? BaseUrl,
    AuthSchemeKind AuthKind,
    string? AuthHeaderName,
    string? ApiKey,
    IReadOnlyDictionary<string, string>? PathVariables = null,
    RateLimitSettings? RateLimit = null);

public sealed class RegisterProviderHandler(
    IProviderRepository providers,
    ISecretStore secrets,
    IUnitOfWork unitOfWork,
    TimeProvider time)
    : ICommandHandler<RegisterProvider, ProviderId>
{
    public async Task<Result<ProviderId>> HandleAsync(RegisterProvider command, CancellationToken cancellationToken)
    {
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

        var provider = Provider.Register(
            command.Name,
            command.Kind,
            command.BaseUrl,
            auth.Value,
            ProviderDefaults.EnvironmentVariableFor(command.Kind),
            command.PathVariables,
            time.GetUtcNow(),
            rateLimit.Value);
        if (provider.IsFailure)
        {
            return provider.Error!;
        }

        providers.Add(provider.Value);
        if (!string.IsNullOrWhiteSpace(command.ApiKey))
        {
            await secrets.StoreAsync(provider.Value.Secret, command.ApiKey.Trim(), cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return provider.Value.Id;
    }
}
