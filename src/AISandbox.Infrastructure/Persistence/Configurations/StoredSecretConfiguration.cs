using AISandbox.Infrastructure.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AISandbox.Infrastructure.Persistence.Configurations;

internal sealed class StoredSecretConfiguration : IEntityTypeConfiguration<StoredSecret>
{
    public void Configure(EntityTypeBuilder<StoredSecret> builder)
    {
        builder.ToTable("Secrets");
        builder.HasKey(s => s.Name);
        builder.Property(s => s.Name).HasMaxLength(200);
        builder.Property(s => s.Ciphertext).IsRequired();
    }
}
