using Bunit;
using MudBlazor;

namespace AISandbox.Web.Tests;

/// <summary>
/// The rules that keep the dialog concise, and the validation flow.
/// </summary>
public sealed class SchemaFormBehaviourTests : SchemaFormTestContext
{
    private const string TwoFieldSchema = """
        { "type": "object", "required": ["name"],
          "properties": {
            "name": { "type": "string", "title": "Name", "description": "Shown to users." },
            "nickname": { "type": "string", "title": "Nickname" } } }
        """;

    [Fact]
    public void Required_fields_come_first_and_optional_ones_sit_behind_more_options()
    {
        var cut = RenderForm(TwoFieldSchema);

        var more = cut.Find("[data-more-options]");
        Assert.Contains("More options (1)", more.TextContent);
        Assert.Contains("data-pointer=\"/nickname\"", more.InnerHtml);
        Assert.DoesNotContain("data-pointer=\"/name\"", more.InnerHtml);
        Assert.True(cut.Markup.IndexOf("data-pointer=\"/name\"", StringComparison.Ordinal)
                    < cut.Markup.IndexOf("data-more-options", StringComparison.Ordinal));
        Assert.False(cut.FindComponent<MudCollapse>().Instance.Expanded);

        cut.Find("[data-more-options] button").Click();

        Assert.True(cut.FindComponent<MudCollapse>().Instance.Expanded);
    }

    [Fact]
    public void A_schema_with_only_required_fields_has_no_more_options()
    {
        var cut = RenderForm(Single("""{ "type": "string" }"""));

        Assert.Empty(cut.FindAll("[data-more-options]"));
    }

    [Fact]
    public void Hints_group_and_order_fields()
    {
        var cut = RenderForm(
            """
            { "type": "object", "required": ["a", "b", "c"],
              "properties": { "a": { "type": "string" }, "b": { "type": "string" }, "c": { "type": "string" } } }
            """,
            hints: """{ "/c": { "order": 1 }, "/b": { "group": "Advanced" } }""");

        var pointers = cut.FindAll("[data-pointer]").Select(e => e.GetAttribute("data-pointer")).ToList();

        Assert.Equal(["", "/c", "/a", "/b"], pointers);
        Assert.Equal("Advanced", cut.Find("[data-group]").GetAttribute("data-group"));
        Assert.Contains("data-pointer=\"/b\"", cut.Find("[data-group]").InnerHtml);
    }

    [Fact]
    public void Help_is_a_tooltip_and_not_inline_text()
    {
        var cut = RenderForm(TwoFieldSchema);

        Assert.DoesNotContain("Shown to users.", cut.Markup);
        Assert.Contains(cut.FindComponents<MudTooltip>(), t => t.Instance.Text == "Shown to users.");
    }

    [Fact]
    public void Hint_labels_replace_schema_titles()
    {
        var cut = RenderForm(TwoFieldSchema, hints: """{ "/name": { "label": "Full name", "help": "From hints." } }""");

        Assert.Contains("Full name", cut.Markup);
        Assert.Contains(cut.FindComponents<MudTooltip>(), t => t.Instance.Text == "From hints.");
    }

    [Fact]
    public void Typing_updates_the_json_value_and_empty_values_are_removed()
    {
        string? latest = null;
        var cut = RenderForm(TwoFieldSchema, onChange: v => latest = v);

        cut.Find("[data-pointer='/name'] input").Input("Ada");
        Assert.Equal("""{"name":"Ada"}""", latest);

        cut.Find("[data-pointer='/name'] input").Input("");
        Assert.Equal("{}", latest);
    }

    [Fact]
    public void An_initial_value_is_shown()
    {
        var cut = RenderForm(TwoFieldSchema, value: """{ "name": "Grace" }""");

        Assert.Equal("Grace", cut.Find("[data-pointer='/name'] input").GetAttribute("value"));
    }

    [Fact]
    public void Submitting_an_invalid_value_shows_the_error_and_does_not_submit()
    {
        string? submitted = null;
        var cut = RenderForm(TwoFieldSchema, onSubmit: v => submitted = v, showSubmit: true);

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Submit").Click();

        Assert.Null(submitted);
        Assert.NotEmpty(cut.FindAll("[data-form-issue]"));
    }

    [Fact]
    public void Submitting_a_valid_value_raises_on_submit_with_the_json()
    {
        string? submitted = null;
        var cut = RenderForm(TwoFieldSchema, onSubmit: v => submitted = v, showSubmit: true);

        cut.Find("[data-pointer='/name'] input").Input("Ada");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Submit").Click();

        Assert.Equal("""{"name":"Ada"}""", submitted);
        Assert.Empty(cut.FindAll("[data-form-issue]"));
    }

    [Fact]
    public void Field_validation_runs_on_blur_with_the_shared_validator()
    {
        var cut = RenderForm(Single("""{ "type": "string", "minLength": 3 }"""));

        cut.Find("[data-pointer='/x'] input").Input("ab");
        cut.Find("[data-pointer='/x'] input").Blur();

        Assert.Contains("mud-input-error", cut.Find("[data-pointer='/x']").InnerHtml);
    }

    [Fact]
    public void A_broken_schema_shows_a_warning_instead_of_a_form()
    {
        var cut = RenderForm("{ nope");

        Assert.NotEmpty(cut.FindAll(".mud-alert"));
        Assert.Empty(cut.FindAll("[data-widget]"));
    }

    [Fact]
    public void Unknown_widgets_in_the_schema_are_not_rendered()
    {
        var cut = RenderForm(Single("""{ "type": "string", "x-ui": { "widget": "carousel" } }"""));

        Assert.Contains("carousel", cut.Find(".mud-alert").TextContent);
        Assert.Empty(cut.FindAll("[data-widget]"));
    }

    [Fact]
    public void Re_rendering_the_parent_does_not_reset_what_the_user_typed()
    {
        var cut = RenderForm(TwoFieldSchema);
        cut.Find("[data-pointer='/name'] input").Input("Ada");

        cut.Render();

        Assert.Equal("Ada", cut.Find("[data-pointer='/name'] input").GetAttribute("value"));
    }
}
