using AISandbox.Application.Features.Models;
using AISandbox.Application.Features.Providers;
using AISandbox.Application.Tests.Support;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Catalog.Providers;

namespace AISandbox.Application.Tests.Models;

public sealed class UpdateModelDefinitionTests
{
    private const string ValidSchema = """{ "type": "object", "properties": { "a": { "type": "string" } } }""";

    private static async Task<ModelDefinitionId> CreateJevAsync(SandboxHost host)
    {
        var provider = await host.SendAsync<RegisterProvider, ProviderId>(
            new RegisterProvider("TypeSafe", ProviderKind.TypeSafe, "https://api.example.test/v1", AuthSchemeKind.Bearer, null, "key-0000000000000000"));
        var model = await host.SendAsync<CreateModelFromTemplate, ModelDefinitionId>(
            new CreateModelFromTemplate("typesafe.jev", provider.Value, null));
        return model.Value;
    }

    private static Task<ModelDetails?> GetAsync(SandboxHost host, ModelDefinitionId id) =>
        host.QueryAsync<GetModelDefinition, ModelDetails?>(new GetModelDefinition(id));

    [Fact]
    public async Task Edits_are_saved_and_read_back()
    {
        await using var host = await SandboxHost.StartAsync();
        var id = await CreateJevAsync(host);
        const string hints = """{ "/a": { "label": "Alpha", "order": 1, "widget": "textarea" } }""";

        var result = await host.SendAsync<UpdateModelDefinition, ModelDefinitionId>(
            new UpdateModelDefinition(id, "  Renamed  ", "jev-next", ValidSchema, ValidSchema, hints));

        Assert.True(result.IsSuccess, result.Error?.Message);
        var details = (await GetAsync(host, id))!;
        Assert.Equal("Renamed", details.DisplayName);
        Assert.Equal("jev-next", details.RemoteId);
        Assert.Equal(ValidSchema, details.InputSchema);
        Assert.Equal("Alpha", UiHints.Parse(details.UiHints).Value.Fields["/a"].Label);
        Assert.Equal(UiWidget.Textarea, UiHints.Parse(details.UiHints).Value.Fields["/a"].Widget);
    }

    [Theory]
    [InlineData("{ nope", ValidSchema, "validation.inputSchema")]
    [InlineData(ValidSchema, """{ "type": "strng" }""", "validation.outputSchema")]
    public async Task Invalid_schemas_are_rejected_and_nothing_changes(string input, string output, string code)
    {
        await using var host = await SandboxHost.StartAsync();
        var id = await CreateJevAsync(host);
        var before = (await GetAsync(host, id))!;

        var result = await host.SendAsync<UpdateModelDefinition, ModelDefinitionId>(
            new UpdateModelDefinition(id, "Renamed", before.RemoteId, input, output, null));

        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(before, await GetAsync(host, id));
    }

    [Fact]
    public async Task Hints_with_an_unknown_widget_are_rejected()
    {
        await using var host = await SandboxHost.StartAsync();
        var id = await CreateJevAsync(host);

        var result = await host.SendAsync<UpdateModelDefinition, ModelDefinitionId>(
            new UpdateModelDefinition(id, "Jev", "jev-latest", ValidSchema, ValidSchema, """{ "/a": { "widget": "carousel" } }"""));

        Assert.Equal("validation.uiHints", result.Error!.Code);
    }

    [Fact]
    public async Task A_schema_naming_an_unknown_x_ui_widget_is_rejected()
    {
        await using var host = await SandboxHost.StartAsync();
        var id = await CreateJevAsync(host);

        var result = await host.SendAsync<UpdateModelDefinition, ModelDefinitionId>(new UpdateModelDefinition(
            id, "Jev", "jev-latest", """{ "type": "string", "x-ui": { "widget": "carousel" } }""", ValidSchema, null));

        Assert.Equal("validation.inputSchema", result.Error!.Code);
    }

    [Theory]
    [InlineData("", "jev-latest", "validation.displayName")]
    [InlineData("Jev", "  ", "validation.remoteId")]
    public async Task Display_name_and_remote_id_are_required(string name, string remoteId, string code)
    {
        await using var host = await SandboxHost.StartAsync();
        var id = await CreateJevAsync(host);

        var result = await host.SendAsync<UpdateModelDefinition, ModelDefinitionId>(
            new UpdateModelDefinition(id, name, remoteId, ValidSchema, ValidSchema, null));

        Assert.Equal(code, result.Error!.Code);
    }

    [Fact]
    public async Task Unknown_models_are_not_found()
    {
        await using var host = await SandboxHost.StartAsync();

        var result = await host.SendAsync<UpdateModelDefinition, ModelDefinitionId>(
            new UpdateModelDefinition(ModelDefinitionId.New(), "x", "y", ValidSchema, ValidSchema, null));

        Assert.Equal("not_found", result.Error!.Code);
        Assert.Null(await GetAsync(host, ModelDefinitionId.New()));
    }

    [Fact]
    public async Task The_shipped_jev_schemas_pass_the_same_check_the_editor_applies()
    {
        await using var host = await SandboxHost.StartAsync();
        var id = await CreateJevAsync(host);
        var details = (await GetAsync(host, id))!;

        var result = await host.SendAsync<UpdateModelDefinition, ModelDefinitionId>(
            new UpdateModelDefinition(id, details.DisplayName, details.RemoteId, details.InputSchema, details.OutputSchema, details.UiHints));

        Assert.True(result.IsSuccess, result.Error?.Message);
    }
}
