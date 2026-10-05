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

public sealed class WorkersAiRunTests : IDisposable
{
    private const string ApiKey = "cf-test-token-0000000000000000000";
    private const string AccountId = "acc123";
    private readonly WireMockServer _cloudflare = WireMockServer.Start();

    public void Dispose() => _cloudflare.Stop();

    private const string Result = """
        {
          "model": "clef-flash",
          "answers": { "is_urgent": { "type": "noul", "noul": 1.0 } },
          "usage": { "input_tokens": 120, "output_tokens": 10 }
        }
        """;

    private static readonly IReadOnlyList<QuestionInput> Questions =
        [new(QuestionType.Noul, "is_urgent", "The message conveys urgency")];

    private void Stub(int status, string body) =>
        _cloudflare
            .Given(Request.Create().WithPath("/client/v4/accounts/*/ai/run/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    private async Task<ProviderId> RegisterAsync(SandboxHost host, string accountId = AccountId)
    {
        var provider = await host.SendAsync<RegisterProvider, ProviderId>(new RegisterProvider(
            "Cloudflare", ProviderKind.CloudflareWorkersAi, $"{_cloudflare.Url}/client/v4", AuthSchemeKind.Bearer, null, ApiKey,
            new Dictionary<string, string> { ["account_id"] = accountId }));
        Assert.True(provider.IsSuccess, provider.Error?.Message);
        return provider.Value;
    }

    private static async Task<ModelDefinitionId> CreateModelAsync(SandboxHost host, ProviderId provider, string templateId)
    {
        var model = await host.SendAsync<CreateModelFromTemplate, ModelDefinitionId>(new CreateModelFromTemplate(templateId, provider, null));
        Assert.True(model.IsSuccess, model.Error?.Message);
        return model.Value;
    }

    private static async Task<RunView> RunAsync(SandboxHost host, ModelDefinitionId model)
    {
        var run = await host.RunToCompletionAsync(new StartRun(SystemOnePayloads.State, Questions, [model]));
        Assert.True(run.IsSuccess, run.Error?.Message);
        return (await host.QueryAsync<GetRun, RunView?>(new GetRun(run.Value)))!;
    }

    private static string Envelope(string result) => "{ \"result\": " + result + ", \"success\": true, \"errors\": [], \"messages\": [] }";

    [Fact]
    public async Task Envelope_is_unwrapped_and_request_targets_the_account_and_model()
    {
        Stub(200, Envelope(Result));
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateModelAsync(host, await RegisterAsync(host), "cloudflare.clef-flash");

        var execution = Assert.Single((await RunAsync(host, model)).Executions);

        Assert.Equal(ExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(new TokenUsage(120, 10), execution.Usage);
        var request = Assert.Single(_cloudflare.LogEntries).RequestMessage!;
        Assert.Contains($"/accounts/{AccountId}/ai/run/@cf/cloudflare/clef-flash", request.Path);
        Assert.Equal($"Bearer {ApiKey}", request.Headers!["Authorization"].Single());
        Assert.Contains("\"model\":\"clef-flash\"", request.Body!.Replace(" ", ""));
    }

    [Fact]
    public async Task Clef_27b_uses_the_clef_path()
    {
        Stub(200, Envelope(Result));
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateModelAsync(host, await RegisterAsync(host), "cloudflare.clef");

        await RunAsync(host, model);

        var request = Assert.Single(_cloudflare.LogEntries).RequestMessage!;
        Assert.EndsWith("/ai/run/@cf/cloudflare/clef", request.Path);
        Assert.Contains("\"model\":\"clef\"", request.Body!.Replace(" ", ""));
    }

    [Fact]
    public async Task Envelope_error_is_surfaced_as_a_failed_execution()
    {
        Stub(200, """{ "result": null, "success": false, "errors": [ { "code": 7003, "message": "No route for that URI" } ], "messages": [] }""");
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateModelAsync(host, await RegisterAsync(host), "cloudflare.clef-flash");

        var execution = Assert.Single((await RunAsync(host, model)).Executions);

        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.Equal("cloudflare.7003", execution.Error!.Code);
        Assert.Equal("No route for that URI", execution.Error.Message);
    }

    [Fact]
    public async Task Http_error_with_an_envelope_surfaces_the_envelope_message()
    {
        Stub(403, """{ "result": null, "success": false, "errors": [ { "code": 10000, "message": "Authentication error" } ], "messages": [] }""");
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateModelAsync(host, await RegisterAsync(host), "cloudflare.clef-flash");

        var execution = Assert.Single((await RunAsync(host, model)).Executions);

        Assert.Equal("cloudflare.10000", execution.Error!.Code);
        Assert.Equal(403, execution.Error.HttpStatus);
    }

    [Fact]
    public async Task Cloudflare_provider_without_account_id_cannot_be_registered()
    {
        await using var host = await SandboxHost.StartAsync();

        var result = await host.SendAsync<RegisterProvider, ProviderId>(new RegisterProvider(
            "Cloudflare", ProviderKind.CloudflareWorkersAi, "https://api.cloudflare.com/client/v4", AuthSchemeKind.Bearer, null, ApiKey));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.pathVariables", result.Error!.Code);
    }

    [Fact]
    public async Task Invalid_path_variable_name_is_rejected()
    {
        await using var host = await SandboxHost.StartAsync();

        var result = await host.SendAsync<RegisterProvider, ProviderId>(new RegisterProvider(
            "Custom", ProviderKind.Custom, "https://example.com", AuthSchemeKind.None, null, null,
            new Dictionary<string, string> { ["bad-name"] = "x" }));

        Assert.Equal("validation.pathVariables", result.Error!.Code);
    }

    [Fact]
    public async Task Missing_account_id_at_run_time_fails_with_configuration_error()
    {
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateModelAsync(host, await RegisterAsync(host), "cloudflare.clef-flash");
        await using (var command = host.Database.CreateCommand())
        {
            command.CommandText = "UPDATE Providers SET PathVariables = '{}'";
            await command.ExecuteNonQueryAsync();
        }

        var execution = Assert.Single((await RunAsync(host, model)).Executions);

        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.Equal("configuration", execution.Error!.Code);
        Assert.Contains("account id", execution.Error.Message);
        Assert.Empty(_cloudflare.LogEntries);
    }

    [Theory]
    [InlineData("cloudflare.clef-flash", "Clef Flash", "clef-flash", 0.09)]
    [InlineData("cloudflare.clef", "Clef 27B", "clef", 0.24)]
    public async Task Template_creates_for_the_cloudflare_provider(string templateId, string name, string remoteId, double input)
    {
        await using var host = await SandboxHost.StartAsync();
        await CreateModelAsync(host, await RegisterAsync(host), templateId);

        var model = Assert.Single(await host.QueryAsync<ListModels, IReadOnlyList<ModelSummary>>(new ListModels()));
        Assert.StartsWith(name, model.DisplayName);
        Assert.Equal("workers-ai", model.Protocol);
        Assert.Equal(remoteId, model.RemoteId);
        Assert.Equal((decimal)input, model.Pricing.InputPerMillion);
        Assert.Equal(0m, model.Pricing.OutputPerMillion);
    }

    [Theory]
    [InlineData("cloudflare.clef-flash", "cloudflare/clef-flash", 0.09)]
    [InlineData("cloudflare.clef", "cloudflare/clef", 0.24)]
    public async Task Template_creates_for_the_open_router_provider(string templateId, string remoteId, double input)
    {
        await using var host = await SandboxHost.StartAsync();
        var provider = await host.SendAsync<RegisterProvider, ProviderId>(
            new RegisterProvider("OpenRouter", ProviderKind.OpenRouter, "https://openrouter.ai/api/v1", AuthSchemeKind.Bearer, null, ApiKey));
        await CreateModelAsync(host, provider.Value, templateId);

        var model = Assert.Single(await host.QueryAsync<ListModels, IReadOnlyList<ModelSummary>>(new ListModels()));
        Assert.Equal("openrouter-decisions", model.Protocol);
        Assert.Equal(remoteId, model.RemoteId);
        Assert.Equal((decimal)input, model.Pricing.InputPerMillion);
    }

    [Fact]
    public async Task Path_variables_round_trip_through_persistence()
    {
        await using var host = await SandboxHost.StartAsync();
        await RegisterAsync(host, " acc123 ");

        var provider = Assert.Single(await host.QueryAsync<ListProviders, IReadOnlyList<ProviderSummary>>(new ListProviders()));
        Assert.Equal("acc123", Assert.Single(provider.PathVariables, v => v.Key == "account_id").Value);
    }
}
