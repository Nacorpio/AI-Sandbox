using AngleSharp.Dom;
using Bunit;
using MudBlazor;

namespace AISandbox.Web.Tests;

/// <summary>
/// One test per row of the widget mapping. Each field renders inside a wrapper carrying a
/// data-widget attribute, which is what these tests assert on, plus the MudBlazor component.
/// </summary>
public sealed class SchemaFormWidgetTests : SchemaFormTestContext
{
    private static string Widget(IElement wrapper) => wrapper.GetAttribute("data-widget")!;

    private IReadOnlyList<string> Widgets(string schema, string? hints = null) =>
        RenderForm(schema, hints).FindAll("[data-widget]").Select(Widget).ToList();

    private string WidgetOfX(string propertySchema, string? hints = null, bool required = true) =>
        Widget(RenderForm(Single(propertySchema, required), hints, showSubmit: false)
            .FindAll("[data-widget]").Single(e => e.GetAttribute("data-pointer") == "/x"));

    [Fact]
    public void String_renders_a_text_field()
    {
        var cut = RenderForm(Single("""{ "type": "string", "title": "Name" }"""));

        Assert.Equal("text", Widget(cut.Find("[data-pointer='/x']")));
        Assert.Single(cut.FindComponents<MudTextField<string>>());
        Assert.Contains("Name", cut.Markup);
    }

    [Fact]
    public void Long_string_renders_a_textarea()
    {
        var cut = RenderForm(Single("""{ "type": "string", "maxLength": 500 }"""));

        Assert.Equal("textarea", Widget(cut.Find("[data-pointer='/x']")));
        Assert.NotEmpty(cut.FindAll("textarea"));
    }

    [Fact]
    public void Textarea_hint_renders_a_textarea() =>
        Assert.Equal("textarea", WidgetOfX("""{ "type": "string" }""", """{ "/x": { "widget": "textarea" } }"""));

    [Fact]
    public void Enum_up_to_five_renders_segmented_buttons()
    {
        var cut = RenderForm(Single("""{ "type": "string", "enum": ["a","b","c","d","e"] }"""));

        Assert.Equal("segmented", Widget(cut.Find("[data-pointer='/x']")));
        Assert.Equal(5, cut.FindComponents<MudToggleItem<string>>().Count);
    }

    [Fact]
    public void Enum_over_five_renders_a_select()
    {
        var cut = RenderForm(Single("""{ "type": "string", "enum": ["a","b","c","d","e","f"] }"""));

        Assert.Equal("select", Widget(cut.Find("[data-pointer='/x']")));
        Assert.Single(cut.FindComponents<MudSelect<string>>());
    }

    [Theory]
    [InlineData("uri", "url")]
    [InlineData("email", "email")]
    [InlineData("date", "date")]
    public void String_formats_render_the_matching_input_type(string format, string inputType)
    {
        var cut = RenderForm(Single($$"""{ "type": "string", "format": "{{format}}" }"""));

        Assert.Equal(format, Widget(cut.Find("[data-pointer='/x']")));
        Assert.Equal(inputType, cut.Find("[data-pointer='/x'] input").GetAttribute("type"));
    }

    [Theory]
    [InlineData("number")]
    [InlineData("integer")]
    public void Bounded_number_renders_a_slider_and_a_numeric_box(string type)
    {
        var cut = RenderForm(Single($$"""{ "type": "{{type}}", "minimum": 0, "maximum": 10 }"""));

        Assert.Equal("slider", Widget(cut.Find("[data-pointer='/x']")));
        Assert.Single(cut.FindComponents<MudSlider<decimal>>());
        Assert.Single(cut.FindComponents<MudNumericField<decimal?>>());
    }

    [Fact]
    public void Unbounded_number_renders_only_a_numeric_box()
    {
        var cut = RenderForm(Single("""{ "type": "number" }"""));

        Assert.Equal("number", Widget(cut.Find("[data-pointer='/x']")));
        Assert.Empty(cut.FindComponents<MudSlider<decimal>>());
        Assert.Single(cut.FindComponents<MudNumericField<decimal?>>());
    }

    [Fact]
    public void Boolean_renders_a_switch()
    {
        var cut = RenderForm(Single("""{ "type": "boolean" }"""));

        Assert.Equal("switch", Widget(cut.Find("[data-pointer='/x']")));
        Assert.Single(cut.FindComponents<MudSwitch<bool>>());
    }

    [Fact]
    public void Nested_object_renders_a_fieldset()
    {
        var cut = RenderForm(Single("""{ "type": "object", "title": "Options", "properties": { "a": { "type": "string" } } }"""));

        Assert.Equal("fieldset", Widget(cut.Find("[data-pointer='/x']")));
        Assert.NotEmpty(cut.FindAll("fieldset.schema-fieldset"));
        Assert.Contains("Options", cut.Find("fieldset.schema-fieldset legend").TextContent);
        Assert.NotNull(cut.Find("[data-pointer='/x/a']"));
    }

    [Fact]
    public void Optional_nested_object_is_collapsed_until_opened()
    {
        var cut = RenderForm(Single("""{ "type": "object", "title": "Extras", "properties": { "a": { "type": "string" } } }""", required: false));
        cut.Find("[data-more-options] button").Click();

        Assert.Empty(cut.FindAll("fieldset.schema-fieldset"));
        Assert.Contains(cut.FindComponents<MudCollapse>(), c => !c.Instance.Expanded && c.Markup.Contains("data-pointer=\"/x/a\""));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Extras")).Click();
        Assert.Contains(cut.FindComponents<MudCollapse>(), c => c.Instance.Expanded && c.Markup.Contains("data-pointer=\"/x/a\""));
    }

    [Fact]
    public void Array_of_scalars_renders_chips_and_adding_one_updates_the_value()
    {
        string? latest = null;
        var cut = RenderForm(Single("""{ "type": "array", "items": { "type": "string" } }"""), value: """{ "x": ["one"] }""", onChange: v => latest = v);

        Assert.Equal("chips", Widget(cut.Find("[data-pointer='/x']")));
        Assert.Single(cut.FindComponents<MudChip<string>>());

        var input = cut.Find("[data-pointer='/x'] input");
        input.Input("two");
        input.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        Assert.Equal(2, cut.FindComponents<MudChip<string>>().Count);
        Assert.Equal("""{"x":["one","two"]}""", latest);
    }

    [Fact]
    public void Array_of_objects_renders_a_card_list_you_can_add_to_and_remove_from()
    {
        string? latest = null;
        var cut = RenderForm(
            Single("""{ "type": "array", "items": { "type": "object", "properties": { "n": { "type": "string" } } } }"""),
            onChange: v => latest = v);

        Assert.Equal("cards", Widget(cut.Find("[data-pointer='/x']")));
        Assert.Empty(cut.FindComponents<MudCard>());

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Add").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Add").Click();
        Assert.Equal(2, cut.FindComponents<MudCard>().Count);
        Assert.NotNull(cut.Find("[data-pointer='/x/1/n']"));
        Assert.Equal("""{"x":[{},{}]}""", latest);

        cut.FindAll("button[aria-label='Remove item']")[0].Click();
        Assert.Single(cut.FindComponents<MudCard>());
    }

    [Fact]
    public void One_of_renders_a_tab_per_variant_and_switching_resets_the_value()
    {
        string? latest = null;
        var cut = RenderForm(
            Single("""{ "oneOf": [ { "title": "Text", "type": "string" }, { "title": "Count", "type": "integer" } ] }"""),
            value: """{ "x": "hello" }""",
            onChange: v => latest = v);

        Assert.Equal("tabs", Widget(cut.Find("[data-pointer='/x']")));
        var tabs = cut.FindComponent<MudTabs>();
        Assert.Equal(["Text", "Count"], tabs.FindComponents<MudTabPanel>().Select(p => p.Instance.Text));

        cut.FindAll(".mud-tab")[1].Click();

        Assert.Equal("{}", latest);
    }

    [Theory]
    [InlineData("""{ "type": ["string", "object"] }""")]
    [InlineData("""{ "type": "object", "additionalProperties": { "type": "string" } }""")]
    [InlineData("""{}""")]
    public void Unknown_or_complex_schemas_render_the_json_fallback(string propertySchema)
    {
        string? latest = null;
        var cut = RenderForm(Single(propertySchema), onChange: v => latest = v);

        Assert.Equal("json", Widget(cut.Find("[data-pointer='/x']")));
        cut.Find("[data-pointer='/x'] textarea").Change("""{ "a": 1 }""");

        Assert.Equal("""{"x":{"a":1}}""", latest);
    }

    [Fact]
    public void The_json_fallback_reports_invalid_json_without_changing_the_value()
    {
        string? latest = null;
        var cut = RenderForm(Single("{}"), onChange: v => latest = v);

        cut.Find("[data-pointer='/x'] textarea").Change("{ nope");

        Assert.Null(latest);
        Assert.Contains("Not valid JSON", cut.Markup);
    }

    [Fact]
    public void Json_widget_hint_overrides_the_default_widget() =>
        Assert.Equal("json", WidgetOfX("""{ "type": "string" }""", """{ "/x": { "widget": "json" } }"""));
}
