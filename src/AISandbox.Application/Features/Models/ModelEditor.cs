using AISandbox.Application.Abstractions;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Catalog.Models;

namespace AISandbox.Application.Features.Models;

public sealed record GetModelDefinition(ModelDefinitionId Id);

public sealed record ModelDetails(
    ModelDefinitionId Id,
    string DisplayName,
    ModelKind Kind,
    string Protocol,
    string RemoteId,
    string ProviderName,
    string InputSchema,
    string OutputSchema,
    string UiHints,
    TemplateOrigin? Origin);

public interface IModelDetailsQueries
{
    Task<ModelDetails?> GetAsync(ModelDefinitionId id, CancellationToken cancellationToken);
}

public sealed class GetModelDefinitionHandler(IModelDetailsQueries queries) : IQueryHandler<GetModelDefinition, ModelDetails?>
{
    public Task<ModelDetails?> HandleAsync(GetModelDefinition query, CancellationToken cancellationToken) =>
        queries.GetAsync(query.Id, cancellationToken);
}

public sealed record UpdateModelDefinition(
    ModelDefinitionId Id,
    string? DisplayName,
    string? RemoteId,
    string InputSchema,
    string OutputSchema,
    string? UiHints);

/// <summary>
/// Saves edits to a model. Both schemas must be valid JSON Schema and the hints must use only the
/// widgets the form renderer knows; otherwise nothing is saved.
/// </summary>
public sealed class UpdateModelDefinitionHandler(
    IModelDefinitionRepository models,
    ISchemaValidator schemas,
    IUnitOfWork unitOfWork)
    : ICommandHandler<UpdateModelDefinition, ModelDefinitionId>
{
    public async Task<Result<ModelDefinitionId>> HandleAsync(UpdateModelDefinition command, CancellationToken cancellationToken)
    {
        var model = await models.GetAsync(command.Id, cancellationToken);
        if (model is null)
        {
            return Error.NotFound("Model");
        }

        var remoteId = RemoteModelId.Create(command.RemoteId);
        if (remoteId.IsFailure)
        {
            return remoteId.Error!;
        }

        var input = CheckSchema(command.InputSchema, "inputSchema", "Input schema");
        if (input is not null)
        {
            return input;
        }

        var output = CheckSchema(command.OutputSchema, "outputSchema", "Output schema");
        if (output is not null)
        {
            return output;
        }

        var hints = UiHints.Parse(command.UiHints);
        if (hints.IsFailure)
        {
            return hints.Error!;
        }

        var updated = model.Update(
            command.DisplayName,
            remoteId.Value,
            new JsonSchemaDocument(command.InputSchema),
            new JsonSchemaDocument(command.OutputSchema),
            hints.Value);
        if (updated.IsFailure)
        {
            return updated.Error!;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return model.Id;
    }

    private Error? CheckSchema(string schema, string field, string name)
    {
        var outcome = schemas.CheckSchema(schema);
        return outcome.IsValid
            ? null
            : Error.Validation(field, $"{name}: {outcome.Issues[0].Message}");
    }
}
