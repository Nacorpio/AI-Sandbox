using AISandbox.Application.Abstractions;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;

namespace AISandbox.Application.Features.Models;

public sealed record ListTemplates;

public sealed record TemplateSummary(
    string TemplateId,
    int Version,
    string DisplayName,
    string Description,
    ModelKind Kind,
    string DocsUrl,
    IReadOnlyList<TemplateRoute> Routes);

public sealed class ListTemplatesHandler(ITemplateCatalog templates) : IQueryHandler<ListTemplates, IReadOnlyList<TemplateSummary>>
{
    public Task<IReadOnlyList<TemplateSummary>> HandleAsync(ListTemplates query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TemplateSummary>>(templates.List()
            .Select(t => new TemplateSummary(t.TemplateId, t.Version, t.DisplayName, t.Description, t.Kind, t.DocsUrl, t.Routes))
            .ToList());
}

public sealed record CreateModelFromTemplate(string TemplateId, ProviderId ProviderId, string? DisplayName);

public sealed class CreateModelFromTemplateHandler(
    ITemplateCatalog templates,
    IProviderRepository providers,
    IModelDefinitionRepository models,
    IUnitOfWork unitOfWork,
    TimeProvider time)
    : ICommandHandler<CreateModelFromTemplate, ModelDefinitionId>
{
    public async Task<Result<ModelDefinitionId>> HandleAsync(CreateModelFromTemplate command, CancellationToken cancellationToken)
    {
        var template = templates.Find(command.TemplateId);
        if (template is null)
        {
            return Error.NotFound($"Template '{command.TemplateId}'");
        }

        var provider = await providers.GetAsync(command.ProviderId, cancellationToken);
        if (provider is null)
        {
            return Error.NotFound("Provider");
        }

        var route = template.RouteFor(provider.Kind);
        if (route is null)
        {
            var supported = string.Join(", ", template.Routes.Select(r => r.ProviderKind));
            return Error.Validation("provider", $"{template.DisplayName} is not available through {provider.Kind}. Supported: {supported}.");
        }

        var model = ModelDefinition.Create(
            provider,
            string.IsNullOrWhiteSpace(command.DisplayName) ? $"{template.DisplayName} ({provider.Name})" : command.DisplayName,
            template.Kind,
            route.Protocol,
            route.RemoteId,
            template.InputSchema,
            template.OutputSchema,
            route.Pricing,
            route.Capabilities,
            new TemplateOrigin(template.TemplateId, template.Version),
            time.GetUtcNow());
        if (model.IsFailure)
        {
            return model.Error!;
        }

        models.Add(model.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return model.Value.Id;
    }
}

public sealed record ListModels;

public sealed record ModelSummary(
    ModelDefinitionId Id,
    string DisplayName,
    ModelKind Kind,
    string Protocol,
    string RemoteId,
    string ProviderName,
    PricingSchedule Pricing,
    ModelCapabilities Capabilities,
    TemplateOrigin? Origin);

public interface IModelQueries
{
    Task<IReadOnlyList<ModelSummary>> ListAsync(CancellationToken cancellationToken);
}

public sealed class ListModelsHandler(IModelQueries queries) : IQueryHandler<ListModels, IReadOnlyList<ModelSummary>>
{
    public Task<IReadOnlyList<ModelSummary>> HandleAsync(ListModels query, CancellationToken cancellationToken) =>
        queries.ListAsync(cancellationToken);
}
