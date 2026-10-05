using System.Text.Json;
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

public sealed class JevRunTests : IDisposable
{
    private const string ApiKey = "test-typesafe-key-000000000000000";
    private readonly WireMockServer _typeSafe = WireMockServer.Start();

    public void Dispose() => _typeSafe.Stop();

    private static readonly IReadOnlyList<QuestionInput> QuickStartQuestions =
    [
        new(QuestionType.Choice, "department", "Which team should handle this",
            Options: [new("billing", "Payment or subscription issues"), new("technical", "Bugs or integration problems"), new("sales", "Pricing or account questions")]),
        new(QuestionType.Score, "frustration", "How frustrated the customer appears",
            Levels: ["Calm, just stating facts", "Frustrated but civil", "Very angry, strong language"]),
        new(QuestionType.Noul, "is_urgent", "The message conveys urgency or time-sensitivity"),
    ];

    private void StubSystemOne(int status, string body) =>
        _typeSafe
            .Given(Request.Create().WithPath("/v1/systemone").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    private async Task<ModelDefinitionId> CreateJevAsync(SandboxHost host, string? apiKey = ApiKey)
    {
        var provider = await host.SendAsync<RegisterProvider, ProviderId>(
            new RegisterProvider("TypeSafe", ProviderKind.TypeSafe, $"{_typeSafe.Url}/v1", AuthSchemeKind.Bearer, null, apiKey));
        var model = await host.SendAsync<CreateModelFromTemplate, ModelDefinitionId>(
            new CreateModelFromTemplate("typesafe.jev", provider.Value, null));
        Assert.True(model.IsSuccess, model.Error?.Message);
        return model.Value;
    }

    private static async Task<RunView> RunAsync(SandboxHost host, ModelDefinitionId model, IReadOnlyList<QuestionInput>? questions = null)
    {
        var run = await host.SendAsync<StartRun, RunId>(new StartRun(SystemOnePayloads.State, questions ?? QuickStartQuestions, [model]));
        Assert.True(run.IsSuccess, run.Error?.Message);
        return (await host.QueryAsync<GetRun, RunView?>(new GetRun(run.Value)))!;
    }

    [Fact]
    public async Task Jev_template_creates_a_system_one_decision_model()
    {
        await using var host = await SandboxHost.StartAsync();

        await CreateJevAsync(host);

        var model = Assert.Single(await host.QueryAsync<ListModels, IReadOnlyList<ModelSummary>>(new ListModels()));
        Assert.Equal("Jev (TypeSafe)", model.DisplayName);
        Assert.Equal(ModelKind.Decision, model.Kind);
        Assert.Equal("systemone", model.Protocol);
        Assert.Equal("jev-latest", model.RemoteId);
        Assert.Equal(0.042m, model.Pricing.InputPerMillion);
        Assert.Equal(new TemplateOrigin("typesafe.jev", 2), model.Origin);
    }

    [Fact]
    public async Task Template_is_rejected_for_a_provider_it_does_not_support()
    {
        await using var host = await SandboxHost.StartAsync();
        var provider = await host.SendAsync<RegisterProvider, ProviderId>(
            new RegisterProvider("Mine", ProviderKind.Custom, "https://example.com", AuthSchemeKind.None, null, null));

        var model = await host.SendAsync<CreateModelFromTemplate, ModelDefinitionId>(new CreateModelFromTemplate("typesafe.jev", provider.Value, null));

        Assert.Equal("validation.provider", model.Error!.Code);
    }

    [Fact]
    public async Task Running_jev_records_answers_usage_cost_and_resolved_version()
    {
        StubSystemOne(200, SystemOnePayloads.QuickStartResponse);
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateJevAsync(host);

        var run = await RunAsync(host, model);

        Assert.Equal(RunStatus.Completed, run.Status);
        var execution = Assert.Single(run.Executions);
        Assert.Equal(ExecutionStatus.Succeeded, execution.Status);
        Assert.Equal("jev-1.13.0", execution.ResolvedModel);
        Assert.Equal(new TokenUsage(392, 65), execution.Usage);
        Assert.Equal(392 * 0.042m / 1_000_000m, execution.Cost!.Amount);
        Assert.Equal(CostSource.Calculated, execution.CostSource);
        Assert.NotNull(execution.Latency);

        var answers = Assert.IsType<DecisionOutput>(execution.Output).Answers;
        var department = Assert.IsType<ChoiceAnswer>(answers["department"]);
        Assert.Equal("technical", department.Choice);
        Assert.Equal(0.78, department.Confidence);
        Assert.Equal(0.85, department.Probabilities["technical"]);
        var frustration = Assert.IsType<ScoreAnswer>(answers["frustration"]);
        Assert.Equal(1.0, frustration.Score);
        Assert.Equal("Frustrated but civil", frustration.Legend["1"]);
        Assert.Equal(1.0, Assert.IsType<NoulAnswer>(answers["is_urgent"]).Value);
    }

    [Fact]
    public async Task Request_follows_the_system_one_contract_and_carries_the_key()
    {
        StubSystemOne(200, SystemOnePayloads.QuickStartResponse);
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateJevAsync(host);

        await RunAsync(host, model);

        var request = Assert.Single(_typeSafe.LogEntries).RequestMessage!;
        Assert.Equal($"Bearer {ApiKey}", request.Headers!["Authorization"].Single());
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("jev-latest", root.GetProperty("model").GetString());
        Assert.Equal(SystemOnePayloads.State, root.GetProperty("state").GetString());
        var questions = root.GetProperty("questions");
        Assert.Equal("choice", questions.GetProperty("department").GetProperty("type").GetString());
        Assert.Equal("Bugs or integration problems",
            questions.GetProperty("department").GetProperty("criteria").GetProperty("technical").GetString());
        Assert.Equal(3, questions.GetProperty("frustration").GetProperty("criteria").GetArrayLength());
        Assert.False(questions.GetProperty("is_urgent").TryGetProperty("criteria", out _));
    }

    [Fact]
    public async Task Structured_state_and_instructions_are_sent_as_json()
    {
        StubSystemOne(200, SystemOnePayloads.QuickStartResponse);
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateJevAsync(host);

        await host.SendAsync<StartRun, RunId>(new StartRun(
            """{ "message": "Reply with your password" }""",
            [new(QuestionType.Noul, "credentials", """{ "question": "Does the message ask for a credential?", "inspect": "message" }""",
                WhenTrue: "Asks for a password", WhenFalse: "No credential requested")],
            [model]));

        using var body = JsonDocument.Parse(Assert.Single(_typeSafe.LogEntries).RequestMessage!.Body!);
        var root = body.RootElement;
        Assert.Equal("Reply with your password", root.GetProperty("state").GetProperty("message").GetString());
        var question = root.GetProperty("questions").GetProperty("credentials");
        Assert.Equal("message", question.GetProperty("instructions").GetProperty("inspect").GetString());
        Assert.Equal("Asks for a password", question.GetProperty("criteria").GetProperty("true").GetString());
    }

    [Fact]
    public async Task Provider_error_is_recorded_as_a_failed_execution()
    {
        StubSystemOne(401, """{ "error": { "message": "Invalid API key" } }""");
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateJevAsync(host);

        var run = await RunAsync(host, model);

        Assert.Equal(RunStatus.Completed, run.Status);
        var execution = Assert.Single(run.Executions);
        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.Equal("http.401", execution.Error!.Code);
        Assert.Equal(401, execution.Error.HttpStatus);
        Assert.Contains("Invalid API key", execution.Error.Message);
        Assert.Null(execution.Cost);
    }

    [Fact]
    public async Task Malformed_response_is_recorded_as_invalid()
    {
        StubSystemOne(200, """{ "unexpected": true }""");
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateJevAsync(host);

        var execution = Assert.Single((await RunAsync(host, model)).Executions);

        Assert.Equal("response.invalid", execution.Error!.Code);
        Assert.Equal("""{ "unexpected": true }""", execution.RawResponse);
    }

    [Fact]
    public async Task Missing_key_fails_without_calling_the_provider()
    {
        StubSystemOne(200, SystemOnePayloads.QuickStartResponse);
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateJevAsync(host, apiKey: null);

        var execution = Assert.Single((await RunAsync(host, model)).Executions);

        Assert.Equal("credentials.missing", execution.Error!.Code);
        Assert.Empty(_typeSafe.LogEntries);
    }

    [Theory]
    [InlineData("bad key!", "validation.key")]
    [InlineData("", "validation.key")]
    public async Task Invalid_question_key_is_rejected_before_any_call(string key, string code)
    {
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateJevAsync(host);

        var run = await host.SendAsync<StartRun, RunId>(new StartRun("state", [new(QuestionType.Noul, key, "Is it?")], [model]));

        Assert.Equal(code, run.Error!.Code);
        Assert.Empty(_typeSafe.LogEntries);
    }

    [Fact]
    public async Task Choice_question_needs_two_options()
    {
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateJevAsync(host);

        var run = await host.SendAsync<StartRun, RunId>(new StartRun(
            "state", [new(QuestionType.Choice, "team", "Which team?", Options: [new("billing", null)])], [model]));

        Assert.Equal("validation.options", run.Error!.Code);
    }

    [Fact]
    public async Task Duplicate_question_keys_are_rejected()
    {
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateJevAsync(host);

        var run = await host.SendAsync<StartRun, RunId>(new StartRun(
            "state", [new(QuestionType.Noul, "a", "One?"), new(QuestionType.Noul, "a", "Two?")], [model]));

        Assert.Equal("validation.questions", run.Error!.Code);
    }

    [Fact]
    public async Task Runs_are_listed_newest_first_with_total_cost()
    {
        StubSystemOne(200, SystemOnePayloads.QuickStartResponse);
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateJevAsync(host);
        await RunAsync(host, model);
        var second = await RunAsync(host, model);

        var runs = await host.QueryAsync<ListRuns, IReadOnlyList<RunSummary>>(new ListRuns());

        Assert.Equal(2, runs.Count);
        Assert.Equal(second.Id, runs[0].Id);
        Assert.Equal(["Jev (TypeSafe)"], runs[0].Models);
        Assert.Equal(392 * 0.042m / 1_000_000m, runs[0].TotalCost!.Amount);
    }
}
