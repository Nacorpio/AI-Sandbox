using System.Diagnostics;
using AISandbox.Application.Features.Models;
using AISandbox.Application.Features.Providers;
using AISandbox.Application.Features.Runs;
using AISandbox.Application.Tests.Support;
using AISandbox.Domain.Authoring.Questions;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;
using AISandbox.Domain.Experimentation;
using AISandbox.Infrastructure.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WireMock;
using WireMock.Types;
using WireMock.Util;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace AISandbox.Application.Tests.Runs;

public sealed class ResilienceRunTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    private static readonly IReadOnlyList<QuestionInput> Questions =
        [new(QuestionType.Noul, "is_urgent", "The message conveys urgency")];

    private static Dictionary<string, string?> Settings(int maxRetries = 3, string? attemptTimeout = null, int? minimumThroughput = null, string? maxRetryAfter = null)
    {
        var settings = new Dictionary<string, string?> { ["ProviderResilience:MaxRetries"] = maxRetries.ToString() };
        if (attemptTimeout is not null)
        {
            settings["ProviderResilience:AttemptTimeout"] = attemptTimeout;
        }

        if (minimumThroughput is { } m)
        {
            settings["ProviderResilience:CircuitMinimumThroughput"] = m.ToString();
        }

        if (maxRetryAfter is not null)
        {
            settings["ProviderResilience:MaxRetryAfter"] = maxRetryAfter;
        }

        return settings;
    }

    private sealed record Reply(int Status, string Body, string? RetryAfter = null, TimeSpan? Delay = null);

    private static Reply Ok(TimeSpan? delay = null) => new(200, SystemOnePayloads.QuickStartResponse, null, delay);

    private static Reply Fail(int status, string? retryAfter = null) => new(status, """{ "error": { "message": "nope" } }""", retryAfter);

    /// <summary>Serves the given replies in order, then the last one for every later request.</summary>
    private void Sequence(params Reply[] replies)
    {
        var served = 0;
        var response = Response.Create().WithCallback(_ =>
        {
            var reply = replies[Math.Min(Interlocked.Increment(ref served) - 1, replies.Length - 1)];
            var headers = new Dictionary<string, WireMockList<string>> { ["Content-Type"] = new("application/json") };
            if (reply.RetryAfter is not null)
            {
                headers["Retry-After"] = new(reply.RetryAfter);
            }

            return new ResponseMessage
            {
                StatusCode = reply.Status,
                Headers = headers,
                BodyData = new BodyData { DetectedBodyType = BodyType.String, BodyAsString = reply.Body },
            };
        });
        if (replies.Any(r => r.Delay is not null))
        {
            response = response.WithDelay(replies.First(r => r.Delay is not null).Delay!.Value);
        }

        _server.Given(Request.Create().WithPath("/v1/systemone").UsingPost()).RespondWith(response);
    }

    private async Task<ModelDefinitionId> ModelAsync(SandboxHost host)
    {
        var provider = await host.SendAsync<RegisterProvider, ProviderId>(new RegisterProvider(
            "TypeSafe", ProviderKind.TypeSafe, $"{_server.Url}/v1", AuthSchemeKind.Bearer, null, "test-typesafe-key-000000000000000"));
        var model = await host.SendAsync<CreateModelFromTemplate, ModelDefinitionId>(new CreateModelFromTemplate("typesafe.jev", provider.Value, null));
        return model.Value;
    }

    private static async Task<ExecutionView> RunAsync(SandboxHost host, ModelDefinitionId model)
    {
        var run = await host.RunToCompletionAsync(new StartRun(SystemOnePayloads.State, Questions, [model]));
        Assert.True(run.IsSuccess, run.Error?.Message);
        return Assert.Single((await host.QueryAsync<GetRun, RunView?>(new GetRun(run.Value)))!.Executions);
    }

    private int Requests => _server.LogEntries.Count();

    [Fact]
    public async Task Rate_limit_then_success_is_retried_and_counts_two_attempts()
    {
        Sequence(Fail(429), Ok());
        await using var host = await SandboxHost.StartAsync(Settings());

        var execution = await RunAsync(host, await ModelAsync(host));

        Assert.Equal(ExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(2, execution.Attempts);
        Assert.Equal(2, Requests);
    }

    [Fact]
    public async Task Retry_after_seconds_is_honoured()
    {
        Sequence(Fail(429, "1"), Ok());
        await using var host = await SandboxHost.StartAsync(Settings());

        var watch = Stopwatch.StartNew();
        var execution = await RunAsync(host, await ModelAsync(host));
        watch.Stop();

        Assert.Equal(ExecutionStatus.Succeeded, execution.Status);
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(0.9), $"waited only {watch.Elapsed}");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Retry_after_http_date_is_honoured()
    {
        Sequence(Fail(503, DateTimeOffset.UtcNow.AddSeconds(3).ToString("R")), Ok());
        await using var host = await SandboxHost.StartAsync(Settings());

        var watch = Stopwatch.StartNew();
        var execution = await RunAsync(host, await ModelAsync(host));
        watch.Stop();

        Assert.Equal(ExecutionStatus.Succeeded, execution.Status);
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(0.5), $"waited only {watch.Elapsed}");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Hostile_retry_after_is_capped()
    {
        Sequence(Fail(429, "3600"), Ok());
        await using var host = await SandboxHost.StartAsync(Settings(maxRetryAfter: "00:00:00.100"));

        var watch = Stopwatch.StartNew();
        var execution = await RunAsync(host, await ModelAsync(host));
        watch.Stop();

        Assert.Equal(ExecutionStatus.Succeeded, execution.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"waited {watch.Elapsed}");
    }

    [Fact]
    public async Task Persistent_rate_limit_fails_as_rate_limited_after_all_retries()
    {
        Sequence(Fail(429, "0"));
        await using var host = await SandboxHost.StartAsync(Settings(maxRetries: 2));

        var execution = await RunAsync(host, await ModelAsync(host));

        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.Equal("rate_limited", execution.Error!.Code);
        Assert.Equal(429, execution.Error.HttpStatus);
        Assert.Contains("retry-after", execution.Error.Message);
        Assert.Equal(3, execution.Attempts);
        Assert.Equal(3, Requests);
    }

    [Fact]
    public async Task Server_error_then_success_is_retried()
    {
        Sequence(Fail(500), Ok());
        await using var host = await SandboxHost.StartAsync(Settings());

        var execution = await RunAsync(host, await ModelAsync(host));

        Assert.Equal(ExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(2, execution.Attempts);
    }

    [Fact]
    public async Task Client_error_is_not_retried()
    {
        Sequence(Fail(401), Ok());
        await using var host = await SandboxHost.StartAsync(Settings());

        var execution = await RunAsync(host, await ModelAsync(host));

        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.Equal("http.401", execution.Error!.Code);
        Assert.Equal(1, execution.Attempts);
        Assert.Equal(1, Requests);
    }

    [Fact]
    public async Task Attempt_timeout_fails_as_timeout()
    {
        Sequence(Ok(TimeSpan.FromSeconds(3)));
        await using var host = await SandboxHost.StartAsync(Settings(maxRetries: 1, attemptTimeout: "00:00:00.200"));

        var execution = await RunAsync(host, await ModelAsync(host));

        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.Equal("timeout", execution.Error!.Code);
        Assert.Equal(2, execution.Attempts);
    }

    [Fact]
    public async Task Circuit_opens_after_repeated_failures()
    {
        Sequence(Fail(500));
        await using var host = await SandboxHost.StartAsync(Settings(maxRetries: 0, minimumThroughput: 2));
        var model = await ModelAsync(host);

        var first = await RunAsync(host, model);
        var second = await RunAsync(host, model);
        var third = await RunAsync(host, model);

        Assert.Equal("http.500", first.Error!.Code);
        Assert.Equal("http.500", second.Error!.Code);
        Assert.Equal("circuit_open", third.Error!.Code);
        Assert.Equal(2, Requests);
    }

    [Theory]
    [InlineData("ProviderResilience:MaxRetries", "-1")]
    [InlineData("ProviderResilience:BaseDelay", "00:00:00")]
    [InlineData("ProviderResilience:MaxRetryAfter", "-00:00:01")]
    [InlineData("ProviderResilience:AttemptTimeout", "00:10:00")]
    [InlineData("ProviderResilience:CircuitFailureRatio", "0")]
    [InlineData("ProviderResilience:CircuitMinimumThroughput", "1")]
    public async Task Invalid_options_fail_validation(string key, string value)
    {
        await using var host = await SandboxHost.StartAsync(new Dictionary<string, string?> { [key] = value });

        var exception = Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<ProviderResilience>>().Value);
        Assert.Contains("ProviderResilience", exception.Message);
    }
}
