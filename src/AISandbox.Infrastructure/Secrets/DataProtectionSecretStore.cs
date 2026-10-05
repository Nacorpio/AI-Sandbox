using AISandbox.Application.Abstractions;
using AISandbox.Domain.Catalog.Providers;
using AISandbox.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AISandbox.Infrastructure.Secrets;

/// <summary>
/// An encrypted credential row. Lives in the same database as the aggregates but is only
/// reachable through <see cref="ISecretStore"/>.
/// </summary>
internal sealed class StoredSecret
{
    public required string Name { get; init; }

    public required string Ciphertext { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Encrypts keys with ASP.NET Core Data Protection. A non-empty configuration value named by
/// <see cref="SecretReference.EnvironmentVariable"/> (environment variables and user secrets both
/// feed configuration) wins over the stored value.
/// </summary>
internal sealed class DataProtectionSecretStore(
    AppDbContext db,
    IDataProtectionProvider protection,
    IConfiguration configuration,
    TimeProvider time) : ISecretStore
{
    private const string Purpose = "AISandbox.Secrets.v1";
    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    public async Task StoreAsync(SecretReference reference, string secret, CancellationToken cancellationToken)
    {
        var ciphertext = _protector.Protect(secret);
        var row = await db.Secrets.FindAsync([reference.Name], cancellationToken);
        if (row is null)
        {
            db.Secrets.Add(new StoredSecret { Name = reference.Name, Ciphertext = ciphertext, UpdatedAt = time.GetUtcNow() });
        }
        else
        {
            row.Ciphertext = ciphertext;
            row.UpdatedAt = time.GetUtcNow();
        }
    }

    public async Task<string?> GetAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        var fromEnvironment = ReadOverride(reference);
        if (fromEnvironment is not null)
        {
            return fromEnvironment;
        }

        var row = await db.Secrets.AsNoTracking().FirstOrDefaultAsync(s => s.Name == reference.Name, cancellationToken);
        return row is null ? null : _protector.Unprotect(row.Ciphertext);
    }

    public async Task<SecretStatus> DescribeAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        var fromEnvironment = ReadOverride(reference);
        if (fromEnvironment is not null)
        {
            return new SecretStatus(SecretSource.Environment, Mask(fromEnvironment));
        }

        var stored = await GetAsync(reference, cancellationToken);
        return stored is null
            ? new SecretStatus(SecretSource.Missing, null)
            : new SecretStatus(SecretSource.Stored, Mask(stored));
    }

    private string? ReadOverride(SecretReference reference) =>
        reference.EnvironmentVariable is { } name && configuration[name] is { Length: > 0 } value ? value : null;

    /// <summary>
    /// Shows the last four characters only for keys long enough that four characters reveal
    /// almost nothing.
    /// </summary>
    internal static string Mask(string secret) =>
        secret.Length >= 16 ? $"••••{secret[^4..]}" : "••••";
}
