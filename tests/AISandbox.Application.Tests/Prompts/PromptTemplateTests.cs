using AISandbox.Application.Features.Prompts;
using AISandbox.Application.Tests.Support;
using AISandbox.Domain.Authoring.Prompts;

namespace AISandbox.Application.Tests.Prompts;

public sealed class PromptTemplateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Placeholders_are_listed_once_in_order_across_both_prompts()
    {
        var template = PromptTemplate.Create("T", "You are a {{role}}.", "Explain {{topic}} to a {{ role }} in {{topic}} terms", null, Now).Value;

        Assert.Equal(["role", "topic"], template.Placeholders);
    }

    [Theory]
    [InlineData("{{bad name}}")]
    [InlineData("{{a-b}}")]
    [InlineData("{ {x} }")]
    public void Malformed_placeholders_are_plain_text(string text)
    {
        var template = PromptTemplate.Create("T", null, text, null, Now).Value;

        Assert.Empty(template.Placeholders);
    }

    [Fact]
    public void Name_is_required_and_at_most_100_characters()
    {
        Assert.Equal("validation.name", PromptTemplate.Create(" ", null, "hi", null, Now).Error!.Code);
        Assert.Equal("validation.name", PromptTemplate.Create(new string('x', 101), null, "hi", null, Now).Error!.Code);
        Assert.True(PromptTemplate.Create(new string('x', 100), null, "hi", null, Now).IsSuccess);
    }

    [Fact]
    public void User_prompt_is_required_but_system_prompt_is_not()
    {
        Assert.Equal("validation.userPrompt", PromptTemplate.Create("T", "sys", "  ", null, Now).Error!.Code);
        Assert.Null(PromptTemplate.Create("T", "  ", "hi", null, Now).Value.SystemPrompt);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    public void Output_schema_must_be_a_json_object(string schema)
    {
        Assert.Equal("validation.outputSchema", PromptTemplate.Create("T", null, "hi", schema, Now).Error!.Code);
    }

    [Fact]
    public void Render_fills_placeholders_and_ignores_extra_values()
    {
        var template = PromptTemplate.Create("T", "Speak {{language}}", "Say {{word}} and {{word}}", """{ "type": "object" }""", Now).Value;

        var prompt = template.Render(new Dictionary<string, string> { ["language"] = "German", ["word"] = "hi", ["unused"] = "x" }).Value;

        Assert.Equal("Speak German", prompt.System);
        Assert.Equal("Say hi and hi", prompt.User);
        Assert.NotNull(prompt.OutputSchema);
        Assert.Equal("T", prompt.Template!.Name);
    }

    [Fact]
    public void Render_reports_every_missing_or_blank_placeholder()
    {
        var template = PromptTemplate.Create("T", null, "{{a}} {{b}} {{c}}", null, Now).Value;

        var result = template.Render(new Dictionary<string, string> { ["a"] = "1", ["b"] = " " });

        Assert.Equal("validation.placeholders", result.Error!.Code);
        Assert.Contains("{{b}}", result.Error.Message);
        Assert.Contains("{{c}}", result.Error.Message);
        Assert.DoesNotContain("{{a}}", result.Error.Message);
    }

    [Fact]
    public async Task Templates_can_be_created_updated_and_listed()
    {
        await using var host = await SandboxHost.StartAsync();
        var id = (await host.SendAsync<CreatePromptTemplate, PromptTemplateId>(new CreatePromptTemplate("Zed", null, "Hi {{x}}"))).Value;
        await host.SendAsync<CreatePromptTemplate, PromptTemplateId>(new CreatePromptTemplate("alpha", "sys", "Yo"));

        var update = await host.SendAsync<UpdatePromptTemplate, PromptTemplateId>(
            new UpdatePromptTemplate(id, "Zed 2", "Be nice", "Hi {{x}} {{y}}", """{ "type": "object" }"""));

        Assert.True(update.IsSuccess);
        var list = await host.QueryAsync<ListPromptTemplates, IReadOnlyList<PromptTemplateView>>(new ListPromptTemplates());
        Assert.Equal(["alpha", "Zed 2"], list.Select(t => t.Name));
        var zed = list[1];
        Assert.Equal(["x", "y"], zed.Placeholders);
        Assert.Equal("Be nice", zed.SystemPrompt);
        Assert.NotNull(zed.OutputSchema);
        var invalid = await host.SendAsync<UpdatePromptTemplate, PromptTemplateId>(new UpdatePromptTemplate(id, "", null, "x"));
        Assert.True(invalid.IsFailure);
    }
}
