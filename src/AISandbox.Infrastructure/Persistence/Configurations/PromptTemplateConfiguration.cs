using AISandbox.Domain.Authoring.Prompts;
using AISandbox.Domain.Catalog.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AISandbox.Infrastructure.Persistence.Configurations;

internal sealed class PromptTemplateConfiguration : IEntityTypeConfiguration<PromptTemplate>
{
    public void Configure(EntityTypeBuilder<PromptTemplate> builder)
    {
        builder.ToTable("PromptTemplates");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasConversion(id => id.Value, value => new PromptTemplateId(value));
        builder.Property(t => t.Name).HasMaxLength(PromptTemplate.MaxNameLength).IsRequired();
        builder.Property(t => t.SystemPrompt);
        builder.Property(t => t.UserPrompt).IsRequired();
        builder.Property(t => t.OutputSchema).HasConversion(
            s => s == null ? null : s.Json,
            json => json == null ? null : new JsonSchemaDocument(json));
        builder.Property(t => t.CreatedAt).HasConversion<DateTimeOffsetToBinaryConverter>();
        builder.Property(t => t.UpdatedAt).HasConversion<DateTimeOffsetToBinaryConverter>();
        builder.Ignore(t => t.Placeholders);
        builder.Ignore(t => t.DomainEvents);
    }
}
