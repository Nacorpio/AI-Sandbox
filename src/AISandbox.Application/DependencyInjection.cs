using AISandbox.Application.Abstractions;
using AISandbox.Application.Features.Models;
using AISandbox.Application.Features.Prompts;
using AISandbox.Application.Features.Providers;
using AISandbox.Application.Features.QuestionSets;
using AISandbox.Domain.Authoring.Prompts;
using AISandbox.Domain.Authoring.QuestionSets;
using AISandbox.Application.Features.Runs;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;
using AISandbox.Domain.Experimentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AISandbox.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<ICommandHandler<RegisterProvider, ProviderId>, RegisterProviderHandler>();
        services.AddScoped<ICommandHandler<UpdateProvider, ProviderId>, UpdateProviderHandler>();
        services.AddScoped<ICommandHandler<DeleteProvider, ProviderId>, DeleteProviderHandler>();
        services.AddScoped<ICommandHandler<TestProviderConnection, ConnectionTestResult>, TestProviderConnectionHandler>();
        services.AddScoped<IQueryHandler<ListProviders, IReadOnlyList<ProviderSummary>>, ListProvidersHandler>();

        services.AddScoped<IQueryHandler<ListTemplates, IReadOnlyList<TemplateSummary>>, ListTemplatesHandler>();
        services.AddScoped<ICommandHandler<CreateModelFromTemplate, ModelDefinitionId>, CreateModelFromTemplateHandler>();
        services.AddScoped<IQueryHandler<ListModels, IReadOnlyList<ModelSummary>>, ListModelsHandler>();
        services.AddScoped<IQueryHandler<GetModelDefinition, ModelDetails?>, GetModelDefinitionHandler>();
        services.AddScoped<ICommandHandler<UpdateModelDefinition, ModelDefinitionId>, UpdateModelDefinitionHandler>();

        services.AddScoped<ICommandHandler<CreateQuestionSet, QuestionSetId>, CreateQuestionSetHandler>();
        services.AddScoped<ICommandHandler<UpdateQuestionSet, QuestionSetRef>, UpdateQuestionSetHandler>();
        services.AddScoped<IQueryHandler<ListQuestionSets, IReadOnlyList<QuestionSetSummary>>, ListQuestionSetsHandler>();
        services.AddScoped<IQueryHandler<GetQuestionSet, QuestionSetView?>, GetQuestionSetHandler>();

        services.AddScoped<ICommandHandler<CreatePromptTemplate, PromptTemplateId>, CreatePromptTemplateHandler>();
        services.AddScoped<ICommandHandler<UpdatePromptTemplate, PromptTemplateId>, UpdatePromptTemplateHandler>();
        services.AddScoped<IQueryHandler<ListPromptTemplates, IReadOnlyList<PromptTemplateView>>, ListPromptTemplatesHandler>();
        services.AddScoped<IQueryHandler<GetPromptTemplate, PromptTemplateView?>, GetPromptTemplateHandler>();

        services.AddScoped<ICommandHandler<StartRun, RunId>, StartRunHandler>();
        services.AddScoped<ICommandHandler<CancelRun, RunId>, CancelRunHandler>();
        services.AddScoped<IRunExecutor, RunExecutor>();
        services.AddScoped<IQueryHandler<GetRun, RunView?>, GetRunHandler>();
        services.AddScoped<IQueryHandler<ListRuns, IReadOnlyList<RunSummary>>, ListRunsHandler>();

        return services;
    }
}
