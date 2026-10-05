using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AISandbox.Infrastructure.Persistence.Configurations;

internal static class JsonConversion
{
    /// <summary>
    /// Stores an immutable domain value as a JSON text column. Values are replaced, never mutated,
    /// so comparing serialized forms is a correct change check.
    /// </summary>
    public static PropertyBuilder<T> HasJsonConversion<T>(this PropertyBuilder<T> property)
    {
        property.HasConversion(
            value => DomainJson.Serialize(value),
            json => DomainJson.Deserialize<T>(json),
            new ValueComparer<T>(
                (left, right) => DomainJson.Serialize(left) == DomainJson.Serialize(right),
                value => DomainJson.Serialize(value).GetHashCode(),
                value => value));
        return property;
    }
}
