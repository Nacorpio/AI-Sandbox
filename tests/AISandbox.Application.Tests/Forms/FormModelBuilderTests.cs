using AISandbox.Application.Abstractions;
using AISandbox.Application.Forms;
using AISandbox.Application.Tests.Support;
using AISandbox.Domain.Catalog.Models;

namespace AISandbox.Application.Tests.Forms;

/// <summary>
/// One test per row of the widget mapping, plus the rules that keep the dialog concise.
/// </summary>
public sealed class FormModelBuilderTests : IAsyncLifetime
{
    private SandboxHost _host = null!;

    public async Task InitializeAsync() => _host = await SandboxHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private FormNode Build(string schema, UiHints? hints = null)
    {
        var result = _host.Service<IFormModelBuilder>().Build(new JsonSchemaDocument(schema), hints ?? UiHints.Empty);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value.Root;
    }

    private FormNode Field(string propertySchema, UiHints? hints = null, bool required = true)
    {
        var requiredPart = required ? """, "required": ["x"]""" : string.Empty;
        var root = (ObjectField)Build($$"""{ "type": "object", "properties": { "x": {{propertySchema}} }{{requiredPart}} }""", hints);
        return Assert.Single(root.Children);
    }

    [Fact]
    public void String_becomes_a_text_field()
    {
        var field = Assert.IsType<TextField>(Field("""{ "type": "string", "title": "Name" }"""));

        Assert.Equal(TextFormat.Text, field.Format);
        Assert.False(field.Multiline);
        Assert.Equal("Name", field.Label);
    }

    [Fact]
    public void Long_string_becomes_a_textarea()
    {
        Assert.True(Assert.IsType<TextField>(Field("""{ "type": "string", "maxLength": 201 }""")).Multiline);
        Assert.False(Assert.IsType<TextField>(Field("""{ "type": "string", "maxLength": 200 }""")).Multiline);
    }

    [Fact]
    public void Textarea_hint_makes_a_short_string_multiline()
    {
        var hints = UiHints.Parse("""{ "/x": { "widget": "textarea" } }""").Value;

        Assert.True(Assert.IsType<TextField>(Field("""{ "type": "string" }""", hints)).Multiline);
    }

    [Theory]
    [InlineData("""["a","b","c","d","e"]""", true)]
    [InlineData("""["a","b","c","d","e","f"]""", false)]
    public void String_enum_is_segmented_up_to_five_options_then_a_select(string values, bool segmented)
    {
        var field = Assert.IsType<EnumField>(Field($$"""{ "type": "string", "enum": {{values}} }"""));

        Assert.Equal(segmented, field.Segmented);
    }

    [Theory]
    [InlineData("uri", TextFormat.Uri)]
    [InlineData("email", TextFormat.Email)]
    [InlineData("date", TextFormat.Date)]
    public void String_formats_map_to_matching_inputs(string format, TextFormat expected)
    {
        var field = Assert.IsType<TextField>(Field($$"""{ "type": "string", "format": "{{format}}" }"""));

        Assert.Equal(expected, field.Format);
    }

    [Theory]
    [InlineData("number")]
    [InlineData("integer")]
    public void Number_with_both_bounds_becomes_a_slider(string type)
    {
        var field = Assert.IsType<NumberField>(Field($$"""{ "type": "{{type}}", "minimum": 0, "maximum": 2 }"""));

        Assert.True(field.Slider);
        Assert.Equal(type == "integer", field.IsInteger);
    }

    [Fact]
    public void Number_without_both_bounds_stays_a_plain_number_box()
    {
        Assert.False(Assert.IsType<NumberField>(Field("""{ "type": "number", "minimum": 0 }""")).Slider);
        Assert.False(Assert.IsType<NumberField>(Field("""{ "type": "integer" }""")).Slider);
    }

    [Fact]
    public void Boolean_becomes_a_switch() =>
        Assert.IsType<SwitchField>(Field("""{ "type": "boolean" }"""));

    [Fact]
    public void Optional_object_is_a_collapsible_fieldset()
    {
        var nested = """{ "type": "object", "properties": { "a": { "type": "string" } } }""";

        Assert.True(Assert.IsType<ObjectField>(Field(nested, required: false)).Collapsible);
        Assert.False(Assert.IsType<ObjectField>(Field(nested, required: true)).Collapsible);
    }

    [Fact]
    public void Array_of_scalars_becomes_chips()
    {
        Assert.False(Assert.IsType<ChipsField>(Field("""{ "type": "array", "items": { "type": "string" } }""")).NumericItems);
        Assert.True(Assert.IsType<ChipsField>(Field("""{ "type": "array", "items": { "type": "number" } }""")).NumericItems);
    }

    [Fact]
    public void Array_of_objects_becomes_a_card_list()
    {
        var list = Assert.IsType<CardListField>(Field(
            """{ "type": "array", "items": { "type": "object", "properties": { "n": { "type": "string" } } } }"""));

        Assert.IsType<ObjectField>(list.Item);
    }

    [Theory]
    [InlineData("oneOf")]
    [InlineData("anyOf")]
    public void One_of_and_any_of_become_a_tab_per_variant(string keyword)
    {
        var field = Assert.IsType<OneOfField>(Field($$"""
            { "{{keyword}}": [
                { "title": "Text", "type": "string" },
                { "title": "Count", "type": "integer" } ] }
            """));

        Assert.Equal(["Text", "Count"], field.Variants.Select(v => v.Title));
        Assert.IsType<TextField>(field.Variants[0].Node);
        Assert.IsType<NumberField>(field.Variants[1].Node);
    }

    [Theory]
    [InlineData("""{ "type": ["string", "object"] }""")]
    [InlineData("""{ "type": "object", "additionalProperties": { "type": "string" } }""")]
    [InlineData("""{}""")]
    [InlineData("""{ "type": "array" }""")]
    public void Unknown_or_complex_schemas_fall_back_to_a_json_editor(string schema) =>
        Assert.IsType<JsonField>(Field(schema));

    [Fact]
    public void Local_refs_are_followed()
    {
        var root = (ObjectField)Build("""
            { "type": "object",
              "properties": { "x": { "$ref": "#/$defs/flag" } },
              "$defs": { "flag": { "type": "boolean", "title": "Flag" } } }
            """);

        Assert.Equal("Flag", Assert.IsType<SwitchField>(Assert.Single(root.Children)).Label);
    }

    [Fact]
    public void Labels_come_from_title_then_the_humanised_key_and_help_from_description()
    {
        var root = (ObjectField)Build("""
            { "type": "object", "properties": {
                "maxTokens": { "type": "integer" },
                "top_p": { "type": "number", "title": "Nucleus", "description": "Sampling mass." } } }
            """);

        Assert.Equal("Max tokens", root.Children[0].Label);
        Assert.Equal("Nucleus", root.Children[1].Label);
        Assert.Equal("Sampling mass.", root.Children[1].Help);
    }

    [Fact]
    public void Hints_set_label_help_group_and_order_and_order_is_stable_for_the_rest()
    {
        var hints = UiHints.Parse("""
            { "/c": { "order": 1, "group": "Tuning", "label": "Third", "help": "Last in schema, first by hint" } }
            """).Value;
        var root = (ObjectField)Build("""
            { "type": "object", "properties": { "a": { "type": "string" }, "b": { "type": "string" }, "c": { "type": "string" } } }
            """, hints);

        Assert.Equal(["c", "a", "b"], root.Children.Select(c => c.Key));
        Assert.Equal("Third", root.Children[0].Label);
        Assert.Equal("Tuning", root.Children[0].Group);
        Assert.Equal("Last in schema, first by hint", root.Children[0].Help);
    }

    [Fact]
    public void Inline_x_ui_is_read_and_the_overlay_wins()
    {
        var hints = UiHints.Parse("""{ "/x": { "label": "From overlay" } }""").Value;
        var field = Field("""{ "type": "string", "x-ui": { "label": "Inline", "group": "G" } }""", hints);

        Assert.Equal("From overlay", field.Label);
        Assert.Equal("G", field.Group);
    }

    [Fact]
    public void Json_widget_hint_forces_the_json_editor() =>
        Assert.IsType<JsonField>(Field("""{ "type": "string" }""", UiHints.Parse("""{ "/x": { "widget": "json" } }""").Value));

    [Fact]
    public void Required_flags_follow_the_schema()
    {
        var root = (ObjectField)Build("""
            { "type": "object", "required": ["a"], "properties": { "a": { "type": "string" }, "b": { "type": "string" } } }
            """);

        Assert.True(root.Children[0].Required);
        Assert.False(root.Children[1].Required);
    }

    [Fact]
    public void Unknown_inline_widget_is_rejected()
    {
        var result = _host.Service<IFormModelBuilder>().Build(
            new JsonSchemaDocument("""{ "type": "object", "properties": { "x": { "type": "string", "x-ui": { "widget": "carousel" } } } }"""),
            UiHints.Empty);

        Assert.True(result.IsFailure);
        Assert.Contains("carousel", result.Error!.Message);
    }

    [Fact]
    public void Unknown_widget_in_overlay_hints_is_rejected()
    {
        var hints = UiHints.Parse("""{ "/x": { "widget": "carousel" } }""");

        Assert.True(hints.IsFailure);
        Assert.Contains("Known widgets", hints.Error!.Message);
    }

    [Theory]
    [InlineData("""{ "/x": { "colour": "red" } }""")]
    [InlineData("""{ "x": { "label": "No leading slash" } }""")]
    [InlineData("""[]""")]
    [InlineData("""{ "/x": "label" }""")]
    public void Hints_outside_the_closed_vocabulary_are_rejected(string json) =>
        Assert.True(UiHints.Parse(json).IsFailure);

    [Fact]
    public void Hints_round_trip_through_json()
    {
        var hints = UiHints.Parse("""{ "/a": { "label": "A", "order": 2, "widget": "slider" } }""").Value;

        var again = UiHints.Parse(hints.ToJson()).Value;

        Assert.Equal(hints.Fields["/a"], again.Fields["/a"]);
    }

    [Fact]
    public void The_system_one_request_schema_builds()
    {
        var root = (ObjectField)Build(TemplateInputSchema());

        Assert.Equal(["model", "state", "questions"], root.Children.Select(c => c.Key));
        Assert.IsType<TextField>(root.Children[0]);
        Assert.IsType<JsonField>(root.Children[1]);
    }

    private string TemplateInputSchema() =>
        _host.Service<ITemplateCatalog>().Find("typesafe.jev")!.InputSchema.Json;
}
