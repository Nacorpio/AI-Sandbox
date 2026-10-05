using AISandbox.Domain.Catalog.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AISandbox.Infrastructure.Persistence.Configurations;

internal sealed class ProviderConfiguration : IEntityTypeConfiguration<Provider>
{
    public void Configure(EntityTypeBuilder<Provider> builder)
    {
        builder.ToTable("Providers");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasConversion(id => id.Value, value => new ProviderId(value));
        builder.Property(p => p.Name).HasMaxLength(Provider.MaxNameLength).IsRequired();
        builder.Property(p => p.Kind).HasConversion<string>().HasMaxLength(40);
        builder.Property(p => p.BaseUrl)
            .HasConversion(uri => uri.ToString(), raw => EndpointUri.Create(raw).Value)
            .HasMaxLength(2048);
        builder.Property(p => p.Auth)
            .HasConversion(auth => AuthSchemeConversion.ToColumn(auth), raw => AuthSchemeConversion.FromColumn(raw))
            .HasMaxLength(200);
        builder.ComplexProperty(p => p.Secret, secret =>
        {
            secret.Property(s => s.Name).HasColumnName("SecretName").HasMaxLength(200);
            secret.Property(s => s.EnvironmentVariable).HasColumnName("SecretEnvironmentVariable").HasMaxLength(200);
        });
        builder.Property(p => p.PathVariables).HasJsonConversion().HasMaxLength(2000).HasDefaultValueSql("'{}'");
        builder.Property(p => p.CreatedAt);
        builder.Ignore(p => p.DomainEvents);
    }
}

internal static class AuthSchemeConversion
{
    public static string ToColumn(AuthScheme auth) => auth.Kind switch
    {
        AuthSchemeKind.Header => $"Header:{auth.HeaderName}",
        _ => auth.Kind.ToString(),
    };

    public static AuthScheme FromColumn(string raw)
    {
        if (raw.StartsWith("Header:", StringComparison.Ordinal))
        {
            return AuthScheme.Header(raw["Header:".Length..]).Value;
        }

        return Enum.Parse<AuthSchemeKind>(raw) == AuthSchemeKind.None ? AuthScheme.None : AuthScheme.Bearer;
    }
}
