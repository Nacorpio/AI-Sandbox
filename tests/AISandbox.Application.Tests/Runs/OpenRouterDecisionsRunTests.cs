using AISandbox.Application.Features.Models;
using AISandbox.Application.Features.Providers;
using AISandbox.Application.Features.Runs;
using AISandbox.Application.Tests.Support;
using AISandbox.Domain.Authoring.Questions;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;
using AISandbox.Domain.Experimentation;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace AISandbox.Application.Tests.Runs;

public sealed class OpenRouterDecisionsRunTests : IDisposable
{
    private const string ApiKey = "sk-or-test-key-000000000000000000";
    private readonly WireMockServer _openRouter = WireMockServer.Start();

    public void Dispose() => _openRouter.Stop();

    private const string Payload = """
        {
          "id": "gen-123",
          "model": "typesafe/jev-1.13",
          "provider": "TypeSafe",
          "answers": { "is_urgent": { "type": "noul", "noul": 1.0 } },
          "usage": { "input_tokens": 392, "output_tokens": 65, "cost": 0.0000321 }
        }
        """;

    private static readonly IReadOnlyList<QuestionInput> Questions =
        [new(QuestionType.Noul, "is_urgent", "The message conveys urgency")];

    private void Stub(int status, string body) =>
        _openRouter
            .Given(Request.Create().WithPath("/api/v1/systemone").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    private async Task<ModelDefinitionId> CreateAsync(SandboxHost host, string templateId)
    {
        var provider = await host.SendAsync<RegisterProvider, ProviderId>(
            new RegisterProvider("OpenRouter", ProviderKind.OpenRouter, $"{_openRouter.Url}/api/v1", AuthSchemeKind.Bearer, null, ApiKey));
        var model = await host.SendAsync<CreateModelFromTemplate, ModelDefinitionId>(
            new CreateModelFromTemplate(templateId, provider.Value, null));
        Assert.True(model.IsSuccess, model.Error?.Message);
        return model.Value;
    }

    private static async Task<RunView> RunAsync(SandboxHost host, ModelDefinitionId model)
    {
        var run = await host.SendAsync<StartRun, RunId>(new StartRun(SystemOnePayloads.State, Questions, [model]));
        Assert.True(run.IsSuccess, run.Error?.Message);
        return (await host.QueryAsync<GetRun, RunView?>(new GetRun(run.Value)))!;
    }

    [Fact]
    public async Task Provider_reported_cost_is_the_execution_cost()
    {
        Stub(200, Payload);
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateAsync(host, "typesafe.jev");

        var execution = Assert.Single((await RunAsync(host, model)).Executions);

        Assert.Equal(ExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(0.0000321m, execution.Cost!.Amount);
        Assert.Equal("USD", execution.Cost.Currency);
        Assert.Equal(CostSource.ProviderReported, execution.CostSource);
        Assert.Equal(new TokenUsage(392, 65), execution.Usage);
        var request = Assert.Single(_openRouter.LogEntries).RequestMessage!;
        Assert.Equal($"Bearer {ApiKey}", request.Headers!["Authorization"].Single());
        Assert.Contains("typesafe/jev-1.13", request.Body!);
    }

    [Fact]
    public async Task Missing_usage_cost_falls_back_to_calculated()
    {
        Stub(200, Payload.Replace(", \"cost\": 0.0000321", ""));
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateAsync(host, "typesafe.jev");

        var execution = Assert.Single((await RunAsync(host, model)).Executions);

        Assert.Equal(CostSource.Calculated, execution.CostSource);
    }

    [Fact]
    public async Task Jev_via_open_router_uses_the_openrouter_route()
    {
        await using var host = await SandboxHost.StartAsync();
        await CreateAsync(host, "typesafe.jev");

        var model = Assert.Single(await host.QueryAsync<ListModels, IReadOnlyList<ModelSummary>>(new ListModels()));
        Assert.Equal("openrouter-decisions", model.Protocol);
        Assert.Equal("typesafe/jev-1.13", model.RemoteId);
        Assert.Equal(0.042m, model.Pricing.InputPerMillion);
        Assert.Equal(new TemplateOrigin("typesafe.jev", 2), model.Origin);
    }

    [Fact]
    public async Task Decider_template_creates_a_decision_model()
    {
        await using var host = await SandboxHost.StartAsync();
        await CreateAsync(host, "perplexity.decider-v1-27b");

        var model = Assert.Single(await host.QueryAsync<ListModels, IReadOnlyList<ModelSummary>>(new ListModels()));
        Assert.Equal("Decider V1 27B (OpenRouter)", model.DisplayName);
        Assert.Equal(ModelKind.Decision, model.Kind);
        Assert.Equal("openrouter-decisions", model.Protocol);
        Assert.Equal("perplexity/pplx-decider-v1-27b", model.RemoteId);
        Assert.Equal(0.04m, model.Pricing.InputPerMillion);
        Assert.Equal(0m, model.Pricing.OutputPerMillion);
    }

    [Fact]
    public async Task Client_error_is_recorded_as_failed_execution()
    {
        Stub(402, """{ "error": { "message": "Insufficient credits" } }""");
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateAsync(host, "perplexity.decider-v1-27b");

        var execution = Assert.Single((await RunAsync(host, model)).Executions);

        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.Equal("http.402", execution.Error!.Code);
        Assert.Null(execution.Cost);
    }
}
