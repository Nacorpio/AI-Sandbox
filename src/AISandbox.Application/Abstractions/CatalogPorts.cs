using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;

namespace AISandbox.Application.Abstractions;

/// <summary>
/// A versioned, read-only starting point for a model definition.
/// </summary>
public sealed record ModelTemplate(
    string TemplateId,
    int Version,
    string DisplayName,
    string Description,
    ModelKind Kind,
    string DocsUrl,
    JsonSchemaDocument InputSchema,
    JsonSchemaDocument OutputSchema,
    IReadOnlyList<TemplateRoute> Routes)
{
    public TemplateRoute? RouteFor(ProviderKind kind) => Routes.FirstOrDefault(r => r.ProviderKind == kind);
}

/// <summary>
/// How a template is reached through one kind of provider. The same model can speak a different
/// protocol, have a different id, different limits and a different price on each route.
/// </summary>
public sealed record TemplateRoute(
    ProviderKind ProviderKind,
    ProtocolId Protocol,
    RemoteModelId RemoteId,
    ModelCapabilities Capabilities,
    PricingSchedule Pricing);

public interface ITemplateCatalog
{
    IReadOnlyList<ModelTemplate> List();

    ModelTemplate? Find(string templateId);
}

public interface IModelDefinitionRepository
{
    void Add(ModelDefinition model);

    Task<IReadOnlyList<ModelDefinition>> GetManyAsync(IReadOnlyCollection<ModelDefinitionId> ids, CancellationToken cancellationToken);
}
