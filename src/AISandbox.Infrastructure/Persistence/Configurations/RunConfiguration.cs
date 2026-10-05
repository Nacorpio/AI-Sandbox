using AISandbox.Domain.Experimentation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AISandbox.Infrastructure.Persistence.Configurations;

internal sealed class RunConfiguration : IEntityTypeConfiguration<Run>
{
    public void Configure(EntityTypeBuilder<Run> builder)
    {
        builder.ToTable("Runs");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasConversion(id => id.Value, value => new RunId(value));
        builder.Property(r => r.Input).HasJsonConversion();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        // SQLite cannot order DateTimeOffset text; store a sortable integer so history can be ordered.
        builder.Property(r => r.CreatedAt).HasConversion<DateTimeOffsetToBinaryConverter>();
        builder.HasIndex(r => r.CreatedAt);
        builder.Ignore(r => r.DomainEvents);

        builder.OwnsMany(r => r.Executions, execution =>
        {
            execution.ToTable("Executions");
            execution.WithOwner().HasForeignKey("RunId");
            execution.HasKey(e => e.Id);
            execution.Property(e => e.Id).HasConversion(id => id.Value, value => new ExecutionId(value)).ValueGeneratedNever();
            execution.Property(e => e.Model).HasJsonConversion();
            execution.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            execution.Property(e => e.Output).HasJsonConversion();
            execution.Property(e => e.Usage).HasJsonConversion();
            execution.Property(e => e.Cost).HasJsonConversion();
            execution.Property(e => e.CostSource).HasConversion<string>().HasMaxLength(20);
            execution.Property(e => e.Latency).HasJsonConversion();
            execution.Property(e => e.Error).HasJsonConversion();
            execution.Property(e => e.ResolvedModel).HasMaxLength(200);
        });
        builder.Navigation(r => r.Executions).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
