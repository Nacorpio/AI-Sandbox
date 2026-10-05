using AISandbox.Application.Abstractions;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Authoring.Prompts;

namespace AISandbox.Application.Features.Prompts;

public sealed record CreatePromptTemplate(string? Name, string? SystemPrompt, string? UserPrompt, string? OutputSchema = null);

/// <summary>
/// Replaces every field of a prompt template. Earlier runs keep the text they were sent.
/// </summary>
public sealed record UpdatePromptTemplate(PromptTemplateId Id, string? Name, string? SystemPrompt, string? UserPrompt, string? OutputSchema = null);

public sealed record ListPromptTemplates;

public sealed record GetPromptTemplate(PromptTemplateId Id);

public sealed record PromptTemplateView(
    PromptTemplateId Id,
    string Name,
    string? SystemPrompt,
    string UserPrompt,
    string? OutputSchema,
    IReadOnlyList<string> Placeholders,
    DateTimeOffset UpdatedAt);

public interface IPromptTemplateRepository
{
    void Add(PromptTemplate template);

    Task<PromptTemplate?> GetAsync(PromptTemplateId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<PromptTemplate>> ListAsync(CancellationToken cancellationToken);
}

internal static class PromptTemplateMapper
{
    public static PromptTemplateView ToView(PromptTemplate t) =>
        new(t.Id, t.Name, t.SystemPrompt, t.UserPrompt, t.OutputSchema?.Json, t.Placeholders, t.UpdatedAt);
}

public sealed class CreatePromptTemplateHandler(IPromptTemplateRepository templates, IUnitOfWork unitOfWork, TimeProvider time)
    : ICommandHandler<CreatePromptTemplate, PromptTemplateId>
{
    public async Task<Result<PromptTemplateId>> HandleAsync(CreatePromptTemplate command, CancellationToken cancellationToken)
    {
        var template = PromptTemplate.Create(command.Name, command.SystemPrompt, command.UserPrompt, command.OutputSchema, time.GetUtcNow());
        if (template.IsFailure)
        {
            return template.Error!;
        }

        templates.Add(template.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return template.Value.Id;
    }
}

public sealed class UpdatePromptTemplateHandler(IPromptTemplateRepository templates, IUnitOfWork unitOfWork, TimeProvider time)
    : ICommandHandler<UpdatePromptTemplate, PromptTemplateId>
{
    public async Task<Result<PromptTemplateId>> HandleAsync(UpdatePromptTemplate command, CancellationToken cancellationToken)
    {
        var template = await templates.GetAsync(command.Id, cancellationToken);
        if (template is null)
        {
            return Error.NotFound("Prompt template");
        }

        var updated = template.Update(command.Name, command.SystemPrompt, command.UserPrompt, command.OutputSchema, time.GetUtcNow());
        if (updated.IsFailure)
        {
            return updated.Error!;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return template.Id;
    }
}

public sealed class ListPromptTemplatesHandler(IPromptTemplateRepository templates)
    : IQueryHandler<ListPromptTemplates, IReadOnlyList<PromptTemplateView>>
{
    public async Task<IReadOnlyList<PromptTemplateView>> HandleAsync(ListPromptTemplates query, CancellationToken cancellationToken) =>
        (await templates.ListAsync(cancellationToken)).Select(PromptTemplateMapper.ToView).ToList();
}

public sealed class GetPromptTemplateHandler(IPromptTemplateRepository templates)
    : IQueryHandler<GetPromptTemplate, PromptTemplateView?>
{
    public async Task<PromptTemplateView?> HandleAsync(GetPromptTemplate query, CancellationToken cancellationToken)
    {
        var template = await templates.GetAsync(query.Id, cancellationToken);
        return template is null ? null : PromptTemplateMapper.ToView(template);
    }
}
