using AISandbox.Application.Abstractions;
using AISandbox.Application.Tests.Support;
using AISandbox.Domain.Catalog.Models;

namespace AISandbox.Application.Tests.Forms;

public sealed class SchemaValidatorTests : IAsyncLifetime
{
    private SandboxHost _host = null!;

    public async Task InitializeAsync() => _host = await SandboxHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private ISchemaValidator Validator => _host.Service<ISchemaValidator>();

    [Fact]
    public void A_real_schema_passes_the_meta_schema_check() =>
        Assert.True(Validator.CheckSchema("""{ "type": "object", "properties": { "a": { "type": "string" } } }""").IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("""{ "type": "strng" }""")]
    [InlineData("""{ "properties": 5 }""")]
    [InlineData("""[]""")]
    public void Broken_schemas_fail_the_meta_schema_check(string schema)
    {
        var outcome = Validator.CheckSchema(schema);

        Assert.False(outcome.IsValid);
        Assert.NotEmpty(outcome.Issues[0].Message);
    }

    [Fact]
    public void Unknown_x_ui_widgets_fail_the_schema_check()
    {
        var outcome = Validator.CheckSchema("""{ "type": "string", "x-ui": { "widget": "carousel" } }""");

        Assert.False(outcome.IsValid);
        Assert.Contains("carousel", outcome.Issues[0].Message);
    }

    [Fact]
    public void Known_x_ui_widgets_pass_the_schema_check() =>
        Assert.True(Validator.CheckSchema("""{ "type": "string", "x-ui": { "widget": "textarea" } }""").IsValid);

    [Fact]
    public void Matching_instances_are_valid() =>
        Assert.True(Validator.Validate(new JsonSchemaDocument("""{ "type": "object", "required": ["a"] }"""), """{ "a": 1 }""").IsValid);

    [Fact]
    public void Issues_carry_the_pointer_of_the_offending_value()
    {
        var schema = new JsonSchemaDocument("""{ "type": "object", "properties": { "n": { "type": "integer", "maximum": 5 } } }""");

        var outcome = Validator.Validate(schema, """{ "n": 9 }""");

        Assert.False(outcome.IsValid);
        Assert.Contains(outcome.Issues, i => i.Pointer == "/n");
    }

    [Fact]
    public void Missing_required_properties_are_reported()
    {
        var outcome = Validator.Validate(new JsonSchemaDocument("""{ "type": "object", "required": ["a"] }"""), "{}");

        Assert.False(outcome.IsValid);
    }

    [Fact]
    public void An_instance_that_is_not_json_is_an_issue_not_an_exception() =>
        Assert.False(Validator.Validate(new JsonSchemaDocument("{}"), "{ nope").IsValid);
}
