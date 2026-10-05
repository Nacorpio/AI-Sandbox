using System.Diagnostics;
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

public sealed class ParallelRunTests : IDisposable
{
    private readonly List<WireMockServer> _servers = [];

    public void Dispose()
    {
        foreach (var server in _servers)
        {
            server.Stop();
        }
    }

    private static readonly IReadOnlyList<QuestionInput> Questions =
    [
        new(QuestionType.Noul, "is_urgent", "The message conveys urgency"),
    ];

    private static Dictionary<string, string?> Limit(int concurrency) =>
        new() { ["ProviderLimits:DefaultConcurrency"] = concurrency.ToString() };

    private WireMockServer Server(int status = 200, TimeSpan? delay = null)
    {
        var server = WireMockServer.Start();
        _servers.Add(server);
        var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json")
            .WithBody(status == 200 ? SystemOnePayloads.QuickStartResponse : """{ "error": { "message": "boom" } }""");
        if (delay is { } d)
        {
            response = response.WithDelay(d);
        }

        server.Given(Request.Create().WithPath("/v1/systemone").UsingPost()).RespondWith(response);
        return server;
    }

    private static async Task<ProviderId> ProviderAsync(SandboxHost host, WireMockServer server, string name) =>
        (await host.SendAsync<RegisterProvider, ProviderId>(
            new RegisterProvider(name, ProviderKind.TypeSafe, $"{server.Url}/v1", AuthSchemeKind.Bearer, null, "test-typesafe-key-000000000000000"))).Value;

    private static async Task<ModelDefinitionId> ModelAsync(SandboxHost host, ProviderId provider, string name) =>
        (await host.SendAsync<CreateModelFromTemplate, ModelDefinitionId>(new CreateModelFromTemplate("typesafe.jev", provider, name))).Value;

    private static async Task<RunView> GetAsync(SandboxHost host, RunId id) =>
        (await host.QueryAsync<GetRun, RunView?>(new GetRun(id)))!;

    private static StartRun Start(params ModelDefinitionId[] models) => new(SystemOnePayloads.State, Questions, models);

    [Fact]
    public async Task Models_on_different_providers_run_in_parallel()
    {
        await using var host = await SandboxHost.StartAsync();
        var models = new List<ModelDefinitionId>();
        for (var i = 0; i < 3; i++)
        {
            var provider = await ProviderAsync(host, Server(delay: TimeSpan.FromMilliseconds(800)), $"P{i}");
            models.Add(await ModelAsync(host, provider, $"M{i}"));
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await host.RunToCompletionAsync(Start([.. models]));
        stopwatch.Stop();

        var run = await GetAsync(host, result.Value);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(3, run.Executions.Count);
        Assert.All(run.Executions, e => Assert.Equal(ExecutionStatus.Succeeded, e.Status));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(2000), $"Took {stopwatch.Elapsed}; sequential execution would need 2400 ms.");
    }

    [Fact]
    public async Task StartRun_returns_before_the_models_have_answered()
    {
        await using var host = await SandboxHost.StartAsync();
        var provider = await ProviderAsync(host, Server(delay: TimeSpan.FromMilliseconds(600)), "Slow");
        var model = await ModelAsync(host, provider, "M");

        var result = await host.SendAsync<StartRun, RunId>(Start(model));

        var started = await GetAsync(host, result.Value);
        Assert.Equal(RunStatus.Running, started.Status);
        Assert.All(started.Executions, e => Assert.NotEqual(ExecutionStatus.Succeeded, e.Status));
        await host.WaitForCompletionAsync(result.Value);
        Assert.Equal(RunStatus.Completed, (await GetAsync(host, result.Value)).Status);
    }

    [Fact]
    public async Task One_failing_model_does_not_fail_the_run()
    {
        await using var host = await SandboxHost.StartAsync();
        var good1 = await ModelAsync(host, await ProviderAsync(host, Server(), "Good1"), "Good1");
        var bad = await ModelAsync(host, await ProviderAsync(host, Server(status: 500), "Bad"), "Bad");
        var good2 = await ModelAsync(host, await ProviderAsync(host, Server(), "Good2"), "Good2");

        var result = await host.RunToCompletionAsync(Start(good1, bad, good2));

        Assert.True(result.IsSuccess);
        var run = await GetAsync(host, result.Value);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(2, run.Executions.Count(e => e.Status == ExecutionStatus.Succeeded));
        var failed = Assert.Single(run.Executions, e => e.Status == ExecutionStatus.Failed);
        Assert.Equal("Bad", failed.ModelName);
        Assert.Equal("http.500", failed.Error!.Code);
    }

    [Fact]
    public async Task Models_on_one_provider_share_its_concurrency_budget()
    {
        await using var host = await SandboxHost.StartAsync(Limit(1));
        var provider = await ProviderAsync(host, Server(delay: TimeSpan.FromMilliseconds(400)), "Shared");
        var models = new[] { await ModelAsync(host, provider, "A"), await ModelAsync(host, provider, "B"), await ModelAsync(host, provider, "C") };

        var stopwatch = Stopwatch.StartNew();
        var result = await host.RunToCompletionAsync(Start(models));
        stopwatch.Stop();

        Assert.All((await GetAsync(host, result.Value)).Executions, e => Assert.Equal(ExecutionStatus.Succeeded, e.Status));
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(1100), $"Took {stopwatch.Elapsed}; a limit of 1 must serialise three 400 ms calls.");
    }

    [Fact]
    public async Task A_higher_limit_lets_models_on_one_provider_overlap()
    {
        await using var host = await SandboxHost.StartAsync(Limit(3));
        var provider = await ProviderAsync(host, Server(delay: TimeSpan.FromMilliseconds(800)), "Shared");
        var models = new[] { await ModelAsync(host, provider, "A"), await ModelAsync(host, provider, "B"), await ModelAsync(host, provider, "C") };

        var stopwatch = Stopwatch.StartNew();
        await host.RunToCompletionAsync(Start(models));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(2000), $"Took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Queued_models_stay_pending_until_a_slot_is_free()
    {
        await using var host = await SandboxHost.StartAsync(Limit(1));
        var provider = await ProviderAsync(host, Server(delay: TimeSpan.FromMilliseconds(800)), "Shared");
        var models = new[] { await ModelAsync(host, provider, "A"), await ModelAsync(host, provider, "B") };

        var result = await host.SendAsync<StartRun, RunId>(Start(models));
        var run = await WaitUntilAsync(host, result.Value, r => r.Executions.Any(e => e.Status == ExecutionStatus.Running));

        Assert.Single(run.Executions, e => e.Status == ExecutionStatus.Running);
        Assert.Single(run.Executions, e => e.Status == ExecutionStatus.Pending);
        await host.WaitForCompletionAsync(result.Value);
    }

    [Fact]
    public async Task Cancelling_stops_in_flight_and_queued_executions()
    {
        await using var host = await SandboxHost.StartAsync(Limit(1));
        var provider = await ProviderAsync(host, Server(delay: TimeSpan.FromSeconds(20)), "Slow");
        var models = new[] { await ModelAsync(host, provider, "A"), await ModelAsync(host, provider, "B") };
        var result = await host.SendAsync<StartRun, RunId>(Start(models));
        await WaitUntilAsync(host, result.Value, r => r.Executions.Any(e => e.Status == ExecutionStatus.Running));

        var stopwatch = Stopwatch.StartNew();
        var cancel = await host.SendAsync<CancelRun, RunId>(new CancelRun(result.Value));
        await host.WaitForCompletionAsync(result.Value);

        Assert.True(cancel.IsSuccess);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Took {stopwatch.Elapsed}.");
        var run = await GetAsync(host, result.Value);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.All(run.Executions, e =>
        {
            Assert.Equal(ExecutionStatus.Failed, e.Status);
            Assert.Equal("cancelled", e.Error!.Code);
        });
    }

    [Fact]
    public async Task Cancelling_keeps_results_that_already_arrived()
    {
        await using var host = await SandboxHost.StartAsync();
        var fast = await ModelAsync(host, await ProviderAsync(host, Server(), "Fast"), "Fast");
        var slow = await ModelAsync(host, await ProviderAsync(host, Server(delay: TimeSpan.FromSeconds(20)), "Slow"), "Slow");
        var result = await host.SendAsync<StartRun, RunId>(Start(fast, slow));
        await WaitUntilAsync(host, result.Value, r => r.Executions.Any(e => e.Status == ExecutionStatus.Succeeded));

        await host.SendAsync<CancelRun, RunId>(new CancelRun(result.Value));
        await host.WaitForCompletionAsync(result.Value);

        var run = await GetAsync(host, result.Value);
        Assert.Equal(ExecutionStatus.Succeeded, run.Executions.Single(e => e.ModelName == "Fast").Status);
        Assert.Equal("cancelled", run.Executions.Single(e => e.ModelName == "Slow").Error!.Code);
    }

    [Fact]
    public async Task Cancelling_a_finished_run_changes_nothing()
    {
        await using var host = await SandboxHost.StartAsync();
        var model = await ModelAsync(host, await ProviderAsync(host, Server(), "P"), "M");
        var result = await host.RunToCompletionAsync(Start(model));

        var cancel = await host.SendAsync<CancelRun, RunId>(new CancelRun(result.Value));

        Assert.True(cancel.IsSuccess);
        Assert.Equal(ExecutionStatus.Succeeded, Assert.Single((await GetAsync(host, result.Value)).Executions).Status);
    }

    [Fact]
    public async Task Cancelling_an_unknown_run_is_not_found()
    {
        await using var host = await SandboxHost.StartAsync();

        var cancel = await host.SendAsync<CancelRun, RunId>(new CancelRun(RunId.New()));

        Assert.Equal("not_found", cancel.Error!.Code);
    }

    [Fact]
    public async Task Subscribers_hear_each_execution_and_the_end_of_the_run()
    {
        await using var host = await SandboxHost.StartAsync();
        var a = await ModelAsync(host, await ProviderAsync(host, Server(delay: TimeSpan.FromMilliseconds(500)), "A"), "A");
        var b = await ModelAsync(host, await ProviderAsync(host, Server(status: 500, delay: TimeSpan.FromMilliseconds(500)), "B"), "B");
        var events = new List<IRunEvent>();
        var result = await host.SendAsync<StartRun, RunId>(Start(a, b));
        using var subscription = host.Notifier.Subscribe(result.Value, e => { lock (events) { events.Add(e); } });

        await host.WaitForCompletionAsync(result.Value);

        lock (events)
        {
            Assert.Single(events.OfType<ExecutionCompleted>());
            Assert.Equal("http.500", Assert.Single(events.OfType<ExecutionFailed>()).ErrorCode);
            Assert.IsType<RunCompleted>(events[^1]);
        }
    }

    private static async Task<RunView> WaitUntilAsync(SandboxHost host, RunId id, Func<RunView, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var run = await GetAsync(host, id);
            if (condition(run))
            {
                return run;
            }

            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the run to reach the expected state.");
            await Task.Delay(25);
        }
    }
}
