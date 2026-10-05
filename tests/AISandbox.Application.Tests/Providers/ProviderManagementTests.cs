using System.Diagnostics;
using AISandbox.Application.Abstractions;
using AISandbox.Domain.Abstractions;
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

namespace AISandbox.Application.Tests.Providers;

public sealed class ProviderManagementTests : IDisposable
{
    private const string Key = "sk-test-0123456789abcdef0123456789abcdef";
    private readonly List<WireMockServer> _servers = [];

    public void Dispose()
    {
        foreach (var server in _servers)
        {
            server.Stop();
        }
    }

    private WireMockServer Server() => Track(WireMockServer.Start());

    private WireMockServer Track(WireMockServer server)
    {
        _servers.Add(server);
        return server;
    }

    private static Task<Result<ConnectionTestResult>> TestAsync(SandboxHost host, ProviderId id) =>
        host.SendAsync<TestProviderConnection, ConnectionTestResult>(new TestProviderConnection(id));

    private static async Task<ProviderId> RegisterAsync(
        SandboxHost host,
        string baseUrl,
        ProviderKind kind = ProviderKind.TypeSafe,
        string? key = Key,
        IReadOnlyDictionary<string, string>? pathVariables = null,
        RateLimitSettings? rateLimit = null,
        AuthSchemeKind auth = AuthSchemeKind.Bearer)
    {
        var result = await host.SendAsync<RegisterProvider, ProviderId>(
            new RegisterProvider("Provider", kind, baseUrl, auth, null, key, pathVariables, rateLimit));
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    private static async Task<ProviderSummary> GetAsync(SandboxHost host, ProviderId id) =>
        (await host.QueryAsync<ListProviders, IReadOnlyList<ProviderSummary>>(new ListProviders())).Single(p => p.Id == id);

    private static UpdateProvider Update(ProviderSummary p, string? apiKey = null, string? name = null, RateLimitSettings? rateLimit = null) =>
        new(p.Id, name ?? p.Name, p.BaseUrl, p.Auth, p.AuthHeaderName, apiKey, p.PathVariables, rateLimit ?? p.RateLimit);

    // ---- Test connection ----

    [Fact]
    public async Task Typesafe_test_calls_the_model_list_with_the_key_and_reports_success_and_latency()
    {
        var server = Server();
        server.Given(Request.Create().WithPath("/v1/models").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{ "data": [] }""").WithDelay(TimeSpan.FromMilliseconds(250)));
        await using var host = await SandboxHost.StartAsync();
        var id = await RegisterAsync(host, $"{server.Url}/v1");

        var result = await TestAsync(host, id);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Succeeded);
        Assert.Equal(200, result.Value.StatusCode);
        Assert.StartsWith("OK, 200, ", result.Value.Message);
        Assert.EndsWith(" ms", result.Value.Message);
        Assert.True(result.Value.Latency >= TimeSpan.FromMilliseconds(200), $"Latency was {result.Value.Latency}.");
        var request = Assert.Single(server.LogEntries).RequestMessage!;
        Assert.Equal($"Bearer {Key}", request.Headers!["Authorization"].Single());
    }

    [Fact]
    public async Task Openrouter_test_works_without_a_key()
    {
        var server = Server();
        server.Given(Request.Create().WithPath("/api/v1/models").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        await using var host = await SandboxHost.StartAsync();
        var id = await RegisterAsync(host, $"{server.Url}/api/v1", ProviderKind.OpenRouter, key: null);

        var result = await TestAsync(host, id);

        Assert.True(result.Value.Succeeded);
        Assert.False(server.LogEntries.Single().RequestMessage!.Headers!.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Rejected_key_is_reported_as_a_failed_result_without_echoing_the_key()
    {
        var server = Server();
        server.Given(Request.Create().WithPath("/v1/models").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401).WithBody($$"""{ "error": "bad key {{Key}}" }"""));
        await using var host = await SandboxHost.StartAsync();
        var id = await RegisterAsync(host, $"{server.Url}/v1");

        var result = await TestAsync(host, id);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Succeeded);
        Assert.Equal(401, result.Value.StatusCode);
        Assert.Equal("401 Unauthorized", result.Value.Message);
        Assert.DoesNotContain(Key, result.Value.Message);
    }

    [Fact]
    public async Task Unreachable_provider_is_reported_without_throwing()
    {
        var server = Server();
        var url = server.Url!;
        server.Stop();
        await using var host = await SandboxHost.StartAsync();
        var id = await RegisterAsync(host, $"{url}/v1");

        var result = await TestAsync(host, id);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Succeeded);
        Assert.Null(result.Value.StatusCode);
        Assert.StartsWith("Could not connect", result.Value.Message);
        Assert.DoesNotContain(Key, result.Value.Message);
    }

    [Fact]
    public async Task Cloudflare_test_calls_the_account_model_search_with_the_token()
    {
        var server = Server();
        server.Given(Request.Create().WithPath("/client/v4/accounts/acc123/ai/models/search").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        await using var host = await SandboxHost.StartAsync();
        var id = await RegisterAsync(host, $"{server.Url}/client/v4", ProviderKind.CloudflareWorkersAi,
            pathVariables: new Dictionary<string, string> { ["account_id"] = "acc123" });

        var result = await TestAsync(host, id);

        Assert.True(result.Value.Succeeded, result.Value.Message);
        var request = Assert.Single(server.LogEntries).RequestMessage!;
        Assert.Contains("per_page=1", request.Url);
        Assert.Equal($"Bearer {Key}", request.Headers!["Authorization"].Single());
    }

    [Fact]
    public async Task Custom_test_calls_the_base_url_and_reports_the_status()
    {
        var server = Server();
        server.Given(Request.Create().WithPath("/health").UsingGet()).RespondWith(Response.Create().WithStatusCode(503));
        await using var host = await SandboxHost.StartAsync();
        var id = await RegisterAsync(host, $"{server.Url}/health", ProviderKind.Custom, key: null, auth: AuthSchemeKind.None);

        var result = await TestAsync(host, id);

        Assert.False(result.Value.Succeeded);
        Assert.Equal(503, result.Value.StatusCode);
        Assert.StartsWith("503", result.Value.Message);
    }

    [Fact]
    public async Task Testing_an_unknown_provider_is_not_found()
    {
        await using var host = await SandboxHost.StartAsync();

        var result = await TestAsync(host, ProviderId.New());

        Assert.Equal("not_found", result.Error!.Code);
    }

    // ---- Edit ----

    [Fact]
    public async Task Update_changes_fields_and_keeps_the_key_when_none_is_given()
    {
        await using var host = await SandboxHost.StartAsync();
        var id = await RegisterAsync(host, "https://api.typesafe.ai/v1");
        var before = await GetAsync(host, id);

        var result = await host.SendAsync<UpdateProvider, ProviderId>(
            Update(before, name: "Renamed", rateLimit: new RateLimitSettings(2, 1.5, 100)) with { BaseUrl = "https://example.com/v2" });

        Assert.True(result.IsSuccess, result.Error?.Message);
        var after = await GetAsync(host, id);
        Assert.Equal("Renamed", after.Name);
        Assert.Equal("https://example.com/v2", after.BaseUrl);
        Assert.Equal(ProviderKind.TypeSafe, after.Kind);
        Assert.Equal(new RateLimitSettings(2, 1.5, 100), after.RateLimit);
        Assert.Equal(SecretSource.Stored, after.Key.Source);
        Assert.Equal(before.Key.MaskedHint, after.Key.MaskedHint);
    }

    [Fact]
    public async Task Update_replaces_the_key_when_one_is_given_and_never_lists_it()
    {
        await using var host = await SandboxHost.StartAsync();
        var id = await RegisterAsync(host, "https://api.typesafe.ai/v1");
        var before = await GetAsync(host, id);

        await host.SendAsync<UpdateProvider, ProviderId>(Update(before, apiKey: "brand-new-key-ending-in-wxyz"));

        var after = await GetAsync(host, id);
        Assert.Equal("••••wxyz", after.Key.MaskedHint);
        Assert.NotEqual(before.Key.MaskedHint, after.Key.MaskedHint);
        Assert.DoesNotContain("brand-new-key", after.ToString());
        Assert.Single(await host.ReadColumnAsync("SELECT Ciphertext FROM Secrets"));
    }

    [Fact]
    public async Task Update_changes_path_variables()
    {
        await using var host = await SandboxHost.StartAsync();
        var id = await RegisterAsync(host, "https://api.cloudflare.com/client/v4", ProviderKind.CloudflareWorkersAi,
            pathVariables: new Dictionary<string, string> { ["account_id"] = "old" });
        var before = await GetAsync(host, id);

        var result = await host.SendAsync<UpdateProvider, ProviderId>(
            Update(before) with { PathVariables = new Dictionary<string, string> { ["account_id"] = "new" } });

        Assert.True(result.IsSuccess);
        Assert.Equal("new", (await GetAsync(host, id)).PathVariables["account_id"]);
    }

    [Fact]
    public async Task Update_applies_the_same_validation_as_register_and_saves_nothing()
    {
        await using var host = await SandboxHost.StartAsync();
        var id = await RegisterAsync(host, "https://api.typesafe.ai/v1");
        var before = await GetAsync(host, id);

        var badUrl = await host.SendAsync<UpdateProvider, ProviderId>(Update(before, name: "X") with { BaseUrl = "ftp://nope" });
        var noName = await host.SendAsync<UpdateProvider, ProviderId>(Update(before, name: " "));

        Assert.Equal("validation.baseUrl", badUrl.Error!.Code);
        Assert.Equal("validation.name", noName.Error!.Code);
        Assert.Equal(before.Name, (await GetAsync(host, id)).Name);
    }

    [Fact]
    public async Task Updating_an_unknown_provider_is_not_found()
    {
        await using var host = await SandboxHost.StartAsync();

        var result = await host.SendAsync<UpdateProvider, ProviderId>(
            new UpdateProvider(ProviderId.New(), "X", "https://example.com", AuthSchemeKind.None, null, null));

        Assert.Equal("not_found", result.Error!.Code);
    }

    // ---- Rate-limit validation ----

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(-1, null, null)]
    [InlineData(1, 0.0, null)]
    [InlineData(1, -2.0, null)]
    [InlineData(1, null, 0.0)]
    [InlineData(null, 5.0, null)]
    public async Task Invalid_rate_limits_are_rejected(int? concurrency, double? rps, double? tps)
    {
        await using var host = await SandboxHost.StartAsync();
        var id = await RegisterAsync(host, "https://api.typesafe.ai/v1");
        var before = await GetAsync(host, id);

        var result = await host.SendAsync<UpdateProvider, ProviderId>(Update(before, rateLimit: new RateLimitSettings(concurrency, rps, tps)));

        Assert.Equal("validation.rateLimit", result.Error!.Code);
        Assert.Null((await GetAsync(host, id)).RateLimit);
    }

    [Fact]
    public async Task Empty_rate_limit_clears_the_policy()
    {
        await using var host = await SandboxHost.StartAsync();
        var id = await RegisterAsync(host, "https://api.typesafe.ai/v1", rateLimit: new RateLimitSettings(3, null, null));
        Assert.Equal(3, (await GetAsync(host, id)).RateLimit!.MaxConcurrency);

        var cleared = await host.SendAsync<UpdateProvider, ProviderId>(
            Update(await GetAsync(host, id)) with { RateLimit = new RateLimitSettings(null, null, null) });

        Assert.True(cleared.IsSuccess);
        Assert.Null((await GetAsync(host, id)).RateLimit);
    }

    // ---- Delete ----

    [Fact]
    public async Task Delete_is_blocked_while_a_model_uses_the_provider()
    {
        await using var host = await SandboxHost.StartAsync();
        var id = await RegisterAsync(host, "https://api.typesafe.ai/v1");
        await host.SendAsync<CreateModelFromTemplate, ModelDefinitionId>(new CreateModelFromTemplate("typesafe.jev", id, "Jev"));

        var result = await host.SendAsync<DeleteProvider, ProviderId>(new DeleteProvider(id));

        Assert.Equal("provider.in_use", result.Error!.Code);
        Assert.Contains("1 model definition uses it", result.Error.Message);
        Assert.Equal(id, (await GetAsync(host, id)).Id);
        Assert.Single(await host.ReadColumnAsync("SELECT Ciphertext FROM Secrets"));
    }

    [Fact]
    public async Task Delete_removes_an_unused_provider_and_its_key()
    {
        await using var host = await SandboxHost.StartAsync();
        var id = await RegisterAsync(host, "https://api.typesafe.ai/v1");

        var result = await host.SendAsync<DeleteProvider, ProviderId>(new DeleteProvider(id));

        Assert.True(result.IsSuccess);
        Assert.Empty(await host.QueryAsync<ListProviders, IReadOnlyList<ProviderSummary>>(new ListProviders()));
        Assert.Empty(await host.ReadColumnAsync("SELECT Ciphertext FROM Secrets"));
    }

    [Fact]
    public async Task Deleting_an_unknown_provider_is_not_found()
    {
        await using var host = await SandboxHost.StartAsync();

        var result = await host.SendAsync<DeleteProvider, ProviderId>(new DeleteProvider(ProviderId.New()));

        Assert.Equal("not_found", result.Error!.Code);
    }

    // ---- Limiter ----

    private static readonly IReadOnlyList<QuestionInput> Questions =
        [new(QuestionType.Noul, "is_urgent", "The message conveys urgency")];

    private WireMockServer SystemOneServer(TimeSpan delay)
    {
        var server = Server();
        server.Given(Request.Create().WithPath("/v1/systemone").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody(SystemOnePayloads.QuickStartResponse).WithDelay(delay));
        return server;
    }

    private static async Task<ModelDefinitionId[]> ModelsAsync(SandboxHost host, ProviderId provider, int count)
    {
        var models = new List<ModelDefinitionId>();
        for (var i = 0; i < count; i++)
        {
            models.Add((await host.SendAsync<CreateModelFromTemplate, ModelDefinitionId>(
                new CreateModelFromTemplate("typesafe.jev", provider, $"M{i}"))).Value);
        }

        return [.. models];
    }

    private static async Task<TimeSpan> TimedRunAsync(SandboxHost host, ModelDefinitionId[] models)
    {
        var stopwatch = Stopwatch.StartNew();
        var run = await host.RunToCompletionAsync(new StartRun(SystemOnePayloads.State, Questions, models));
        stopwatch.Stop();
        var view = (await host.QueryAsync<GetRun, RunView?>(new GetRun(run.Value)))!;
        Assert.All(view.Executions, e => Assert.Equal(ExecutionStatus.Succeeded, e.Status));
        return stopwatch.Elapsed;
    }

    [Fact]
    public async Task A_policy_with_concurrency_one_serialises_models_despite_a_higher_global_default()
    {
        await using var host = await SandboxHost.StartAsync(new Dictionary<string, string?> { ["ProviderLimits:DefaultConcurrency"] = "4" });
        var server = SystemOneServer(TimeSpan.FromMilliseconds(500));
        var id = await RegisterAsync(host, $"{server.Url}/v1", rateLimit: new RateLimitSettings(1, null, null));

        var elapsed = await TimedRunAsync(host, await ModelsAsync(host, id, 2));

        Assert.True(elapsed >= TimeSpan.FromMilliseconds(900), $"Took {elapsed}; a policy of 1 must serialise two 500 ms calls.");
    }

    [Fact]
    public async Task Without_a_policy_the_global_default_applies()
    {
        await using var host = await SandboxHost.StartAsync(new Dictionary<string, string?> { ["ProviderLimits:DefaultConcurrency"] = "4" });
        var server = SystemOneServer(TimeSpan.FromMilliseconds(700));
        var id = await RegisterAsync(host, $"{server.Url}/v1");

        var elapsed = await TimedRunAsync(host, await ModelsAsync(host, id, 2));

        Assert.True(elapsed < TimeSpan.FromMilliseconds(1300), $"Took {elapsed}; two calls should overlap.");
    }

    [Fact]
    public async Task Updating_the_policy_takes_effect_on_the_next_run()
    {
        await using var host = await SandboxHost.StartAsync(new Dictionary<string, string?> { ["ProviderLimits:DefaultConcurrency"] = "4" });
        var server = SystemOneServer(TimeSpan.FromMilliseconds(500));
        var id = await RegisterAsync(host, $"{server.Url}/v1");
        var models = await ModelsAsync(host, id, 2);
        var parallel = await TimedRunAsync(host, models);
        Assert.True(parallel < TimeSpan.FromMilliseconds(900), $"Took {parallel}.");

        await host.SendAsync<UpdateProvider, ProviderId>(Update(await GetAsync(host, id), rateLimit: new RateLimitSettings(1, null, null)));
        var serial = await TimedRunAsync(host, models);

        Assert.True(serial >= TimeSpan.FromMilliseconds(900), $"Took {serial}; the new policy must apply without a restart.");
    }

    [Fact]
    public async Task Requests_per_second_spaces_the_calls()
    {
        await using var host = await SandboxHost.StartAsync(new Dictionary<string, string?> { ["ProviderLimits:DefaultConcurrency"] = "4" });
        var server = SystemOneServer(TimeSpan.FromMilliseconds(5));
        var id = await RegisterAsync(host, $"{server.Url}/v1", rateLimit: new RateLimitSettings(4, 2, null));

        var elapsed = await TimedRunAsync(host, await ModelsAsync(host, id, 3));

        // A bucket of one token refilled every 500 ms: calls start at about 0, 500 and 1000 ms.
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(800), $"Took {elapsed}; three calls at 2/s cannot finish sooner.");
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"Took {elapsed}.");
    }
}
