using System.Reflection;
using System.Text.Json;
using AISandbox.Application.Abstractions;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;

namespace AISandbox.Infrastructure.Templates;

/// <summary>
/// Loads the versioned template documents embedded in this assembly once, at first use.
/// A malformed template is a build defect, so loading throws instead of skipping it.
/// </summary>
internal sealed class EmbeddedTemplateCatalog : ITemplateCatalog
{
    private const string Prefix = "Templates/";
    private const string SchemaPrefix = "Templates/schemas/";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly Lazy<IReadOnlyList<ModelTemplate>> _templates = new(Load);

    public IReadOnlyList<ModelTemplate> List() => _templates.Value;

    public ModelTemplate? Find(string templateId) =>
        _templates.Value.FirstOrDefault(t => t.TemplateId == templateId);

    private static IReadOnlyList<ModelTemplate> Load()
    {
        var assembly = typeof(EmbeddedTemplateCatalog).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .ToDictionary(name => name.Replace('\\', '/'), name => name);

        return resources.Keys
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal) && !name.StartsWith(SchemaPrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name => ToTemplate(Read<TemplateDocument>(assembly, resources[name]), name, assembly, resources))
            .OrderBy(t => t.Kind)
            .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ModelTemplate ToTemplate(
        TemplateDocument document,
        string resourceName,
        Assembly assembly,
        IReadOnlyDictionary<string, string> resources)
    {
        string Schema(string path) => resources.TryGetValue(Prefix + path, out var name)
            ? ReadText(assembly, name)
            : throw new InvalidOperationException($"Template {resourceName} references missing schema '{path}'.");

        var routes = document.Routes.Select(route => new TemplateRoute(
                Enum.Parse<ProviderKind>(route.ProviderKind),
                ProtocolId.Parse(route.Protocol).Value,
                RemoteModelId.Create(route.RemoteId).Value,
                new ModelCapabilities(
                    route.Capabilities.AcceptsImages,
                    route.Capabilities.MaxQuestions,
                    route.Capabilities.ContextTokens,
                    route.Capabilities.QuestionTypes ?? []),
                PricingSchedule.Create(
                    route.Pricing.Currency,
                    route.Pricing.InputPerMillion,
                    route.Pricing.OutputPerMillion,
                    route.Pricing.CacheReadPerMillion).Value))
            .ToList();

        return new ModelTemplate(
            document.TemplateId,
            document.Version,
            document.DisplayName,
            document.Description,
            Enum.Parse<ModelKind>(document.Kind),
            document.DocsUrl,
            new JsonSchemaDocument(Schema(document.InputSchema)),
            new JsonSchemaDocument(Schema(document.OutputSchema)),
            routes);
    }

    private static T Read<T>(Assembly assembly, string name) =>
        JsonSerializer.Deserialize<T>(ReadText(assembly, name), Options)
        ?? throw new InvalidOperationException($"Template resource {name} is empty.");

    private static string ReadText(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record TemplateDocument(
        string TemplateId,
        int Version,
        string DisplayName,
        string Description,
        string Kind,
        string DocsUrl,
        string InputSchema,
        string OutputSchema,
        IReadOnlyList<RouteDocument> Routes);

    private sealed record RouteDocument(
        string ProviderKind,
        string Protocol,
        string RemoteId,
        CapabilitiesDocument Capabilities,
        PricingDocument Pricing);

    private sealed record CapabilitiesDocument(bool AcceptsImages, int MaxQuestions, int? ContextTokens, IReadOnlyList<string>? QuestionTypes);

    private sealed record PricingDocument(string Currency, decimal InputPerMillion, decimal OutputPerMillion, decimal? CacheReadPerMillion);
}
