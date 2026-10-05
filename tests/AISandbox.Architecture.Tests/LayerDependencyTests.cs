using System.Reflection;

namespace AISandbox.Architecture.Tests;

/// <summary>
/// Enforces the dependency rule: references point inward only. The compiler drops references an
/// assembly never uses, so these checks reflect real dependencies, not just project files.
/// </summary>
public sealed class LayerDependencyTests
{
    private static readonly Assembly Domain = typeof(Domain.Abstractions.AggregateRoot<>).Assembly;
    private static readonly Assembly Application = typeof(Application.DependencyInjection).Assembly;
    private static readonly Assembly Infrastructure = typeof(Infrastructure.DependencyInjection).Assembly;
    private static readonly Assembly Web = typeof(Web.Components.App).Assembly;

    private static IReadOnlyList<string> ReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

    private static bool IsBaseClassLibrary(string name) =>
        name is "netstandard" or "mscorlib" || name.StartsWith("System", StringComparison.Ordinal);

    [Fact]
    public void Domain_depends_on_the_base_class_library_only()
    {
        var offenders = ReferencesOf(Domain).Where(name => !IsBaseClassLibrary(name)).ToList();

        Assert.True(offenders.Count == 0, $"Domain references: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Application_depends_only_on_domain_and_abstractions()
    {
        var offenders = ReferencesOf(Application)
            .Where(name => !IsBaseClassLibrary(name)
                && name != Domain.GetName().Name
                && !(name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) && name.EndsWith(".Abstractions", StringComparison.Ordinal)))
            .ToList();

        Assert.True(offenders.Count == 0, $"Application references: {string.Join(", ", offenders)}");
    }

    [Theory]
    [InlineData("AISandbox.Infrastructure")]
    [InlineData("AISandbox.Web")]
    public void Inner_layers_never_reference_outer_layers(string outer)
    {
        Assert.DoesNotContain(outer, ReferencesOf(Domain));
        Assert.DoesNotContain(outer, ReferencesOf(Application));
    }

    [Fact]
    public void Infrastructure_never_references_web()
    {
        Assert.DoesNotContain(Web.GetName().Name, ReferencesOf(Infrastructure));
    }

    [Fact]
    public void Domain_types_do_not_expose_persistence_or_serialization_attributes()
    {
        var offenders = Domain.GetTypes()
            .SelectMany(t => t.GetCustomAttributesData().Select(a => (Type: t, Attribute: a.AttributeType.Namespace ?? string.Empty)))
            .Where(x => x.Attribute.StartsWith("System.Text.Json", StringComparison.Ordinal)
                || x.Attribute.StartsWith("System.ComponentModel.DataAnnotations", StringComparison.Ordinal))
            .Select(x => x.Type.FullName)
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0, $"Domain types with infrastructure attributes: {string.Join(", ", offenders)}");
    }
}
