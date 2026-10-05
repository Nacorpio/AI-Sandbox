using System.Text.Json;
using AISandbox.Application.Features.Models;
using AISandbox.Application.Features.Providers;
using AISandbox.Application.Features.QuestionSets;
using AISandbox.Application.Features.Runs;
using AISandbox.Application.Tests.Support;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Authoring.QuestionSets;
using AISandbox.Domain.Authoring.Questions;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;
using AISandbox.Domain.Experimentation;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace AISandbox.Application.Tests.QuestionSets;

public sealed class QuestionSetTests : IDisposable
{
    private readonly WireMockServer _typeSafe = WireMockServer.Start();

    public void Dispose() => _typeSafe.Stop();

    private static QuestionInput Noul(string key, string text = "Is it urgent?") => new(QuestionType.Noul, key, text);

    private static Task<Result<QuestionSetId>> CreateAsync(SandboxHost host, string? name, params QuestionInput[] questions) =>
        host.SendAsync<CreateQuestionSet, QuestionSetId>(new CreateQuestionSet(name, questions));

    private async Task<ModelDefinitionId> CreateJevAsync(SandboxHost host)
    {
        _typeSafe
            .Given(Request.Create().WithPath("/v1/systemone").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(SystemOnePayloads.QuickStartResponse));
        var provider = await host.SendAsync<RegisterProvider, ProviderId>(
            new RegisterProvider("TypeSafe", ProviderKind.TypeSafe, $"{_typeSafe.Url}/v1", AuthSchemeKind.Bearer, null, "key-0000000000000000"));
        var model = await host.SendAsync<CreateModelFromTemplate, ModelDefinitionId>(
            new CreateModelFromTemplate("typesafe.jev", provider.Value, null));
        return model.Value;
    }

    private static void AssertJson(string expected, string? actual) =>
        Assert.Equal(JsonSerializer.Serialize(JsonDocument.Parse(expected).RootElement), JsonSerializer.Serialize(JsonDocument.Parse(actual!).RootElement));

    [Fact]
    public async Task Created_set_starts_at_version_one_and_keeps_question_order()
    {
        await using var host = await SandboxHost.StartAsync();

        var id = await CreateAsync(host, "  Support triage ", Noul("b"), Noul("a"), Noul("c"));

        var set = (await host.QueryAsync<GetQuestionSet, QuestionSetView?>(new GetQuestionSet(id.Value)))!;
        Assert.Equal("Support triage", set.Name);
        Assert.Equal(1, set.Version);
        Assert.Equal(["b", "a", "c"], set.Questions.Select(q => q.Key));
        var summary = Assert.Single(await host.QueryAsync<ListQuestionSets, IReadOnlyList<QuestionSetSummary>>(new ListQuestionSets()));
        Assert.Equal((id.Value, 3, 1), (summary.Id, summary.QuestionCount, summary.Version));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Name_is_required(string name)
    {
        await using var host = await SandboxHost.StartAsync();

        Assert.Equal("validation.name", (await CreateAsync(host, name, Noul("a"))).Error!.Code);
    }

    [Fact]
    public async Task Name_is_limited_to_100_characters()
    {
        await using var host = await SandboxHost.StartAsync();

        Assert.True((await CreateAsync(host, new string('n', 100), Noul("a"))).IsSuccess);
        Assert.Equal("validation.name", (await CreateAsync(host, new string('n', 101), Noul("a"))).Error!.Code);
    }

    [Theory]
    [InlineData("bad key!")]
    [InlineData("")]
    [InlineData("has space")]
    public async Task Question_key_rule_is_enforced(string key)
    {
        await using var host = await SandboxHost.StartAsync();

        Assert.Equal("validation.key", (await CreateAsync(host, "Set", Noul(key))).Error!.Code);
    }

    [Fact]
    public async Task Key_may_use_letters_digits_underscore_dot_and_hyphen_up_to_100_characters()
    {
        await using var host = await SandboxHost.StartAsync();

        Assert.True((await CreateAsync(host, "Set", Noul("A1_b.c-d"), Noul(new string('k', 100)))).IsSuccess);
        Assert.Equal("validation.key", (await CreateAsync(host, "Set", Noul(new string('k', 101)))).Error!.Code);
    }

    [Fact]
    public async Task A_set_needs_between_one_and_sixty_four_questions()
    {
        await using var host = await SandboxHost.StartAsync();

        Assert.Equal("validation.questions", (await CreateAsync(host, "Empty")).Error!.Code);
        Assert.True((await CreateAsync(host, "Full", Enumerable.Range(0, 64).Select(i => Noul($"q{i}")).ToArray())).IsSuccess);
        Assert.Equal("validation.questions",
            (await CreateAsync(host, "Too many", Enumerable.Range(0, 65).Select(i => Noul($"q{i}")).ToArray())).Error!.Code);
    }

    [Fact]
    public async Task Duplicate_keys_are_rejected()
    {
        await using var host = await SandboxHost.StartAsync();

        var result = await CreateAsync(host, "Dupes", Noul("a"), Noul("a"));

        Assert.Equal("validation.questions", result.Error!.Code);
        Assert.Empty(await host.QueryAsync<ListQuestionSets, IReadOnlyList<QuestionSetSummary>>(new ListQuestionSets()));
    }

    [Fact]
    public async Task Updating_replaces_the_questions_and_bumps_the_version_each_time()
    {
        await using var host = await SandboxHost.StartAsync();
        var id = (await CreateAsync(host, "Set", Noul("a"))).Value;

        var second = await host.SendAsync<UpdateQuestionSet, QuestionSetRef>(new UpdateQuestionSet(id, "Set v2", [Noul("x"), Noul("y")]));
        var third = await host.SendAsync<UpdateQuestionSet, QuestionSetRef>(new UpdateQuestionSet(id, "Set v2", [Noul("y"), Noul("x")]));

        Assert.Equal(2, second.Value.Version);
        Assert.Equal(3, third.Value.Version);
        var set = (await host.QueryAsync<GetQuestionSet, QuestionSetView?>(new GetQuestionSet(id)))!;
        Assert.Equal("Set v2", set.Name);
        Assert.Equal(["y", "x"], set.Questions.Select(q => q.Key));
    }

    [Fact]
    public async Task A_rejected_update_keeps_the_version()
    {
        await using var host = await SandboxHost.StartAsync();
        var id = (await CreateAsync(host, "Set", Noul("a"))).Value;

        var result = await host.SendAsync<UpdateQuestionSet, QuestionSetRef>(new UpdateQuestionSet(id, "Set", [Noul("a"), Noul("a")]));

        Assert.False(result.IsSuccess);
        Assert.Equal(1, (await host.QueryAsync<GetQuestionSet, QuestionSetView?>(new GetQuestionSet(id)))!.Version);
    }

    [Fact]
    public async Task Updating_a_missing_set_is_not_found()
    {
        await using var host = await SandboxHost.StartAsync();

        var result = await host.SendAsync<UpdateQuestionSet, QuestionSetRef>(new UpdateQuestionSet(QuestionSetId.New(), "Set", [Noul("a")]));

        Assert.Equal("not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Structured_text_and_all_question_types_round_trip()
    {
        await using var host = await SandboxHost.StartAsync();
        QuestionInput[] questions =
        [
            new(QuestionType.Choice, "team", """{ "ask": "Which team?", "hints": ["a", "b"] }""",
                Options: [new("billing", """{ "about": "payments" }"""), new("sales", "Pricing questions")]),
            new(QuestionType.Score, "anger", "How angry", Levels: ["calm", """["mild", "annoyed"]""", "furious"]),
            new(QuestionType.Noul, "urgent", "Is it urgent?", WhenTrue: """{ "when": "deadline mentioned" }""", WhenFalse: "no hurry"),
        ];

        var id = (await CreateAsync(host, "Rich", questions)).Value;

        var loaded = (await host.QueryAsync<GetQuestionSet, QuestionSetView?>(new GetQuestionSet(id)))!.Questions;
        AssertJson("""{"ask":"Which team?","hints":["a","b"]}""", loaded[0].Instructions);
        Assert.Equal(["billing", "sales"], loaded[0].Options!.Select(o => o.Name));
        AssertJson("""{"about":"payments"}""", loaded[0].Options![0].Description);
        Assert.Equal("Pricing questions", loaded[0].Options![1].Description);
        Assert.Equal("calm", loaded[1].Levels![0]);
        AssertJson("""["mild","annoyed"]""", loaded[1].Levels![1]);
        Assert.Equal("furious", loaded[1].Levels![2]);
        AssertJson("""{"when":"deadline mentioned"}""", loaded[2].WhenTrue);
        Assert.Equal("no hurry", loaded[2].WhenFalse);
    }

    [Fact]
    public async Task A_run_started_from_a_set_pins_its_version_and_ignores_later_edits()
    {
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateJevAsync(host);
        var id = (await CreateAsync(host, "Triage", Noul("is_urgent", "Original wording"))).Value;

        var first = await host.SendAsync<StartRun, RunId>(new StartRun(SystemOnePayloads.State, [], [model], id));
        await host.SendAsync<UpdateQuestionSet, QuestionSetRef>(new UpdateQuestionSet(id, "Triage", [Noul("is_urgent", "New wording"), Noul("extra")]));
        var second = await host.SendAsync<StartRun, RunId>(new StartRun(SystemOnePayloads.State, [], [model], id));

        var firstRun = (await host.QueryAsync<GetRun, RunView?>(new GetRun(first.Value)))!;
        var secondRun = (await host.QueryAsync<GetRun, RunView?>(new GetRun(second.Value)))!;
        Assert.Equal(new QuestionSetRef(id, 1), firstRun.QuestionSet);
        Assert.Equal(["Original wording"], firstRun.Questions.Select(q => q.Instructions));
        Assert.Equal(new QuestionSetRef(id, 2), secondRun.QuestionSet);
        Assert.Equal(["New wording", "Is it urgent?"], secondRun.Questions.Select(q => q.Instructions));

        using var body = JsonDocument.Parse(_typeSafe.LogEntries.First().RequestMessage!.Body!);
        Assert.Equal("Original wording", body.RootElement.GetProperty("questions").GetProperty("is_urgent").GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task Inline_questions_still_work_and_carry_no_set_reference()
    {
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateJevAsync(host);

        var run = await host.SendAsync<StartRun, RunId>(new StartRun(SystemOnePayloads.State, [Noul("a")], [model]));

        Assert.Null((await host.QueryAsync<GetRun, RunView?>(new GetRun(run.Value)))!.QuestionSet);
    }

    [Fact]
    public async Task Run_without_a_set_or_inline_questions_is_rejected()
    {
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateJevAsync(host);

        var run = await host.SendAsync<StartRun, RunId>(new StartRun(SystemOnePayloads.State, [], [model]));

        Assert.Equal("validation.questions", run.Error!.Code);
    }

    [Fact]
    public async Task Run_with_an_unknown_set_is_not_found()
    {
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateJevAsync(host);

        var run = await host.SendAsync<StartRun, RunId>(new StartRun(SystemOnePayloads.State, [Noul("a")], [model], QuestionSetId.New()));

        Assert.Equal("not_found", run.Error!.Code);
    }

    [Fact]
    public async Task Runs_stored_before_question_sets_existed_still_load()
    {
        await using var host = await SandboxHost.StartAsync();
        var model = await CreateJevAsync(host);
        var run = await host.SendAsync<StartRun, RunId>(new StartRun(SystemOnePayloads.State, [Noul("a")], [model]));
        await using (var command = host.Database.CreateCommand())
        {
            command.CommandText = "UPDATE Runs SET Input = json_remove(Input, '$.questionSet')";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var loaded = await host.QueryAsync<GetRun, RunView?>(new GetRun(run.Value));

        Assert.Equal(["a"], loaded!.Questions.Select(q => q.Key));
        Assert.Null(loaded.QuestionSet);
    }
}
