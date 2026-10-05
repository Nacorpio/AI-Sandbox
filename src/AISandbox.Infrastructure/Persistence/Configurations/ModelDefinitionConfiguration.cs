using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AISandbox.Infrastructure.Persistence.Configurations;

internal sealed class ModelDefinitionConfiguration : IEntityTypeConfiguration<ModelDefinition>
{
    public void Configure(EntityTypeBuilder<ModelDefinition> builder)
    {
        builder.ToTable("ModelDefinitions");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasConversion(id => id.Value, value => new ModelDefinitionId(value));
        builder.Property(m => m.ProviderId).HasConversion(id => id.Value, value => new ProviderId(value));
        builder.HasOne<Provider>().WithMany().HasForeignKey(m => m.ProviderId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(m => m.DisplayName).HasMaxLength(ModelDefinition.MaxDisplayNameLength).IsRequired();
        builder.Property(m => m.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.Protocol)
            .HasConversion(p => p.Value, value => ProtocolId.Parse(value).Value)
            .HasMaxLength(40);
        builder.Property(m => m.RemoteId)
            .HasConversion(r => r.Value, value => RemoteModelId.Create(value).Value)
            .HasMaxLength(200);
        builder.Property(m => m.InputSchema).HasConversion(s => s.Json, json => new JsonSchemaDocument(json));
        builder.Property(m => m.OutputSchema).HasConversion(s => s.Json, json => new JsonSchemaDocument(json));
        builder.Property(m => m.UiHints)
            .HasConversion(h => h.ToJson(), json => UiHints.Parse(json).Value)
            .HasMaxLength(20000)
            .HasDefaultValue(UiHints.Empty)
            .IsRequired();
        builder.Property(m => m.Pricing).HasJsonConversion();
        builder.Property(m => m.Capabilities).HasJsonConversion();
        builder.Property(m => m.Origin).HasJsonConversion();
        builder.Ignore(m => m.DomainEvents);
    }
}
