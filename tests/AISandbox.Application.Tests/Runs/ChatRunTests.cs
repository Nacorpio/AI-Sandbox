using System.Text.Json;
using AISandbox.Application.Features.Models;
using AISandbox.Application.Features.Prompts;
using AISandbox.Application.Features.Providers;
using AISandbox.Application.Features.Runs;
using AISandbox.Application.Tests.Support;
using AISandbox.Domain.Authoring.Prompts;
using AISandbox.Domain.Authoring.Questions;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;
using AISandbox.Domain.Experimentation;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace AISandbox.Application.Tests.Runs;

public sealed class ChatRunTests : IDisposable
{
    private const string ApiKey = "sk-or-test-key-000000000000000000";
    private readonly WireMockServer _openRouter = WireMockServer.Start();

    public void Dispose() => _openRouter.Stop();

    private const string Completion = """
        {
          "id": "gen-1",
          "model": "deepseek/deepseek-v4.1-flash-20261001",
          "choices": [ { "message": {
            "role": "assistant",
            "content": "# Hello\n\nThis is **markdown**.",
            "reasoning": "The user greeted me, so I greet back."
          } } ],
          "usage": {
            "prompt_tokens": 21, "completion_tokens": 40, "cost": 0.0000411,
            "completion_tokens_details": { "reasoning_tokens": 12 }
          }
        }
        """;

    private void StubChat(string body, int status = 200) =>
        _openRouter
            .Given(Request.Create().WithPath("/api/v1/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    private async Task<ProviderId> ProviderAsync(SandboxHost host) =>
        (await host.SendAsync<RegisterProvider, ProviderId>(
            new RegisterProvider("OpenRouter", ProviderKind.OpenRouter, $"{_openRouter.Url}/api/v1", AuthSchemeKind.Bearer, null, ApiKey))).Value;

    private static async Task<ModelDefinitionId> ModelAsync(SandboxHost host, ProviderId provider, string template) =>
        (await host.SendAsync<CreateModelFromTemplate, ModelDefinitionId>(new CreateModelFromTemplate(template, provider, null))).Value;

    private static async Task<RunView> GetAsync(SandboxHost host, RunId id) =>
        (await host.QueryAsync<GetRun, RunView?>(new GetRun(id)))!;

    private static StartRun ChatRun(ModelDefinitionId model, ChatPromptInput? prompt = null, ChatOptions? options = null) =>
        new(null, [], [model], Prompt: prompt ?? new ChatPromptInput(User: "Say hello", System: "Be brief"),
            ChatOptions: options is null ? null : new Dictionary<ModelDefinitionId, ChatOptions> { [model] = options });

    private JsonElement SentBody() => JsonDocument.Parse(Assert.Single(_openRouter.LogEntries).RequestMessage!.Body!).RootElement;

    [Fact]
    public async Task Chat_run_records_markdown_reasoning_usage_and_provider_cost()
    {
        StubChat(Completion);
        await using var host = await SandboxHost.StartAsync();
        var model = await ModelAsync(host, await ProviderAsync(host), "deepseek.v4-1-flash");

        var result = await host.RunToCompletionAsync(ChatRun(model));

        Assert.True(result.IsSuccess, result.Error?.Message);
        var execution = Assert.Single((await GetAsync(host, result.Value)).Executions);
        Assert.Equal(ExecutionStatus.Succeeded, execution.Status);
        var output = Assert.IsType<ChatOutput>(execution.Output);
        Assert.Equal("# Hello\n\nThis is **markdown**.", output.Text);
        Assert.Equal("The user greeted me, so I greet back.", output.Reasoning);
        Assert.Equal(12, output.ReasoningTokens);
        Assert.Null(output.StructuredJson);
        Assert.Equal(new TokenUsage(21, 40), execution.Usage);
        Assert.Equal(0.0000411m, execution.Cost!.Amount);
        Assert.Equal(CostSource.ProviderReported, execution.CostSource);
        Assert.Equal("deepseek/deepseek-v4.1-flash-20261001", execution.ResolvedModel);
    }

    [Fact]
    public async Task Request_follows_the_chat_completions_shape()
    {
        StubChat(Completion);
        await using var host = await SandboxHost.StartAsync();
        var model = await ModelAsync(host, await ProviderAsync(host), "deepseek.v4-1-flash");

        await host.RunToCompletionAsync(ChatRun(model, options: new ChatOptions(Temperature: 0.2, MaxTokens: 300, ReasoningEffort: "high")));

        var request = Assert.Single(_openRouter.LogEntries).RequestMessage!;
        Assert.Equal($"Bearer {ApiKey}", request.Headers!["Authorization"].Single());
        var body = SentBody();
        Assert.Equal("deepseek/deepseek-v4.1-flash", body.GetProperty("model").GetString());
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["system", "user"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("Be brief", messages[0].GetProperty("content").GetString());
        Assert.Equal("Say hello", messages[1].GetProperty("content").GetString());
        Assert.Equal(0.2, body.GetProperty("temperature").GetDouble());
        Assert.Equal(300, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal("high", body.GetProperty("reasoning_effort").GetString());
        Assert.False(body.TryGetProperty("response_format", out _));
        Assert.False(body.TryGetProperty("provider", out _));
    }

    [Fact]
    public async Task Reasoning_content_field_is_read_too()
    {
        StubChat("""{ "model": "m", "choices": [ { "message": { "content": "ok", "reasoning_content": "thinking" } } ], "usage": { "prompt_tokens": 1, "completion_tokens": 2 } }""");
        await using var host = await SandboxHost.StartAsync();
        var model = await ModelAsync(host, await ProviderAsync(host), "deepseek.v4-1-flash");

        var result = await host.RunToCompletionAsync(ChatRun(model));

        var execution = Assert.Single((await GetAsync(host, result.Value)).Executions);
        Assert.Equal("thinking", ((ChatOutput)execution.Output!).Reasoning);
        Assert.Equal(CostSource.Calculated, execution.CostSource);
    }

    [Fact]
    public async Task Output_schema_is_sent_as_strict_json_schema_and_the_reply_is_parsed()
    {
        const string schema = """{ "type": "object", "properties": { "city": { "type": "string" } }, "required": ["city"] }""";
        StubChat("""{ "model": "m", "choices": [ { "message": { "content": "{\"city\":\"Oslo\"}" } } ], "usage": { "prompt_tokens": 1, "completion_tokens": 2 } }""");
        await using var host = await SandboxHost.StartAsync();
        var model = await ModelAsync(host, await ProviderAsync(host), "deepseek.v4-1-flash");

        var result = await host.RunToCompletionAsync(ChatRun(model, new ChatPromptInput(User: "Capital of Norway?", OutputSchema: schema)));

        var format = SentBody().GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("object", format.GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString());
        var output = (ChatOutput)Assert.Single((await GetAsync(host, result.Value)).Executions).Output!;
        Assert.Equal("""{"city":"Oslo"}""", output.StructuredJson);
    }

    [Fact]
    public async Task Non_json_reply_keeps_the_text_without_structured_output()
    {
        StubChat("""{ "model": "m", "choices": [ { "message": { "content": "Sorry, no." } } ], "usage": { "prompt_tokens": 1, "completion_tokens": 2 } }""");
        await using var host = await SandboxHost.StartAsync();
        var model = await ModelAsync(host, await ProviderAsync(host), "deepseek.v4-1-flash");

        var result = await host.RunToCompletionAsync(ChatRun(model, new ChatPromptInput(User: "x", OutputSchema: """{ "type": "object" }""")));

        var output = (ChatOutput)Assert.Single((await GetAsync(host, result.Value)).Executions).Output!;
        Assert.Equal("Sorry, no.", output.Text);
        Assert.Null(output.StructuredJson);
    }

    [Theory]
    [InlineData(UpstreamPreferenceKind.Cheapest, null, """{"sort":"price"}""")]
    [InlineData(UpstreamPreferenceKind.Fastest, null, """{"sort":"throughput"}""")]
    [InlineData(UpstreamPreferenceKind.Pinned, "DeepSeek", """{"order":["DeepSeek"],"allow_fallbacks":false}""")]
    public async Task Upstream_preference_maps_to_openrouter_provider_routing(UpstreamPreferenceKind kind, string? name, string expected)
    {
        StubChat(Completion);
        await using var host = await SandboxHost.StartAsync();
        var model = await ModelAsync(host, await ProviderAsync(host), "deepseek.v4-1-flash");

        await host.RunToCompletionAsync(ChatRun(model, options: new ChatOptions(Upstream: new UpstreamPreference(kind, name))));

        Assert.Equal(expected, SentBody().GetProperty("provider").GetRawText());
    }

    [Fact]
    public async Task Json_object_response_format_is_sent_when_requested()
    {
        StubChat(Completion);
        await using var host = await SandboxHost.StartAsync();
        var model = await ModelAsync(host, await ProviderAsync(host), "deepseek.v4-1-flash");

        await host.RunToCompletionAsync(ChatRun(model, options: new ChatOptions(ResponseFormat: ResponseFormat.JsonObject)));

        Assert.Equal("json_object", SentBody().GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Prompt_template_is_rendered_with_values_and_pinned_on_the_run()
    {
        StubChat(Completion);
        await using var host = await SandboxHost.StartAsync();
        var model = await ModelAsync(host, await ProviderAsync(host), "deepseek.v4-1-flash");
        var template = await host.SendAsync<CreatePromptTemplate, PromptTemplateId>(
            new CreatePromptTemplate("Translator", "Translate into {{language}}.", "Text: {{text}}"));

        var result = await host.RunToCompletionAsync(ChatRun(model, new ChatPromptInput(
            TemplateId: template.Value,
            Values: new Dictionary<string, string> { ["language"] = "French", ["text"] = "Good morning" })));

        Assert.True(result.IsSuccess, result.Error?.Message);
        var messages = SentBody().GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()).ToList();
        Assert.Equal(["Translate into French.", "Text: Good morning"], messages);
        var run = await GetAsync(host, result.Value);
        Assert.Equal("Translator", run.Prompt!.TemplateName);
        Assert.Equal("Text: Good morning", run.Prompt.User);
    }

    [Fact]
    public async Task Missing_placeholder_value_fails_the_start_and_names_the_placeholder()
    {
        await using var host = await SandboxHost.StartAsync();
        var model = await ModelAsync(host, await ProviderAsync(host), "deepseek.v4-1-flash");
        var template = await host.SendAsync<CreatePromptTemplate, PromptTemplateId>(
            new CreatePromptTemplate("Translator", null, "Translate {{text}} into {{language}}"));

        var result = await host.RunToCompletionAsync(ChatRun(model, new ChatPromptInput(
            TemplateId: template.Value, Values: new Dictionary<string, string> { ["text"] = "hi" })));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.placeholders", result.Error!.Code);
        Assert.Contains("{{language}}", result.Error.Message);
        Assert.Empty(_openRouter.LogEntries);
    }

    [Fact]
    public async Task Chat_model_without_a_prompt_is_rejected()
    {
        await using var host = await SandboxHost.StartAsync();
        var model = await ModelAsync(host, await ProviderAsync(host), "deepseek.v4-1-flash");

        var result = await host.RunToCompletionAsync(new StartRun(null, [], [model]));

        Assert.Equal("validation.prompt", result.Error!.Code);
    }

    [Fact]
    public async Task Decision_model_without_questions_is_rejected_even_when_a_prompt_is_given()
    {
        await using var host = await SandboxHost.StartAsync();
        var jev = await host.SendAsync<CreateModelFromTemplate, ModelDefinitionId>(new CreateModelFromTemplate(
            "perplexity.decider-v1-27b", await ProviderAsync(host), null));

        var result = await host.RunToCompletionAsync(new StartRun(SystemOnePayloads.State, [], [jev.Value], Prompt: new ChatPromptInput(User: "hi")));

        Assert.Equal("validation.questions", result.Error!.Code);
    }

    [Fact]
    public async Task Provider_error_is_recorded_on_the_chat_execution()
    {
        StubChat("""{ "error": { "message": "Insufficient credits" } }""", 402);
        await using var host = await SandboxHost.StartAsync();
        var model = await ModelAsync(host, await ProviderAsync(host), "deepseek.v4-1-flash");

        var result = await host.RunToCompletionAsync(ChatRun(model));

        var execution = Assert.Single((await GetAsync(host, result.Value)).Executions);
        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.Equal("http.402", execution.Error!.Code);
    }

    [Fact]
    public async Task Run_with_a_decision_and_a_chat_model_answers_both()
    {
        StubChat(Completion);
        _openRouter
            .Given(Request.Create().WithPath("/api/v1/systemone").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(
                """{ "model": "d", "answers": { "is_urgent": { "type": "noul", "noul": 0.9 } }, "usage": { "input_tokens": 5, "output_tokens": 1 } }"""));
        await using var host = await SandboxHost.StartAsync();
        var provider = await ProviderAsync(host);
        var chat = await ModelAsync(host, provider, "deepseek.v4-1-flash");
        var decision = await ModelAsync(host, provider, "perplexity.decider-v1-27b");

        var result = await host.RunToCompletionAsync(new StartRun(
            SystemOnePayloads.State,
            [new QuestionInput(QuestionType.Noul, "is_urgent", "Is it urgent?")],
            [decision, chat],
            Prompt: new ChatPromptInput(User: "Summarise the message")));

        Assert.True(result.IsSuccess, result.Error?.Message);
        var run = await GetAsync(host, result.Value);
        Assert.All(run.Executions, e => Assert.Equal(ExecutionStatus.Succeeded, e.Status));
        Assert.Single(run.Executions, e => e.Output is DecisionOutput);
        Assert.Single(run.Executions, e => e.Output is ChatOutput);
        Assert.Null(AnswerMatrix.Of(run));
    }

    [Fact]
    public async Task Runs_stored_before_chat_support_still_load()
    {
        _openRouter
            .Given(Request.Create().WithPath("/api/v1/systemone").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(
                """{ "model": "d", "answers": { "is_urgent": { "type": "noul", "noul": 0.9 } }, "usage": { "input_tokens": 5, "output_tokens": 1 } }"""));
        await using var host = await SandboxHost.StartAsync();
        var decision = await ModelAsync(host, await ProviderAsync(host), "perplexity.decider-v1-27b");
        var result = await host.RunToCompletionAsync(new StartRun(
            SystemOnePayloads.State, [new QuestionInput(QuestionType.Noul, "is_urgent", "Is it urgent?")], [decision]));

        // Rewrite the stored input into the shape written before chat support existed.
        await using (var command = host.Database.CreateCommand())
        {
            command.CommandText = "UPDATE Runs SET Input = json_remove(Input, '$.chat', '$.chatOptions')";
            await command.ExecuteNonQueryAsync();
        }

        var legacy = (await host.ReadColumnAsync("SELECT Input FROM Runs")).Single();
        Assert.DoesNotContain("chat", legacy);
        var run = await GetAsync(host, result.Value);
        Assert.Equal(SystemOnePayloads.State, run.State);
        Assert.Single(run.Questions);
        Assert.Null(run.Prompt);
        Assert.IsType<DecisionOutput>(Assert.Single(run.Executions).Output);
    }
}
