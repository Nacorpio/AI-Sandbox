using AISandbox.Domain.Authoring.QuestionSets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AISandbox.Infrastructure.Persistence.Configurations;

internal sealed class QuestionSetConfiguration : IEntityTypeConfiguration<QuestionSet>
{
    public void Configure(EntityTypeBuilder<QuestionSet> builder)
    {
        builder.ToTable("QuestionSets");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasConversion(id => id.Value, value => new QuestionSetId(value));
        builder.Property(s => s.Name).HasMaxLength(QuestionSet.MaxNameLength).IsRequired();
        builder.Property(s => s.Version);
        builder.Property(s => s.Questions).HasJsonConversion();
        builder.Property(s => s.CreatedAt).HasConversion<DateTimeOffsetToBinaryConverter>();
        builder.Property(s => s.UpdatedAt).HasConversion<DateTimeOffsetToBinaryConverter>();
        builder.Ignore(s => s.Ref);
        builder.Ignore(s => s.DomainEvents);
    }
}
