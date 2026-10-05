using AISandbox.Application;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Infrastructure;
using AISandbox.Web.Components.Forms;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace AISandbox.Web.Tests;

/// <summary>
/// bUnit context with MudBlazor and the real schema validator and form builder, so the form is
/// tested exactly as the app renders it.
/// </summary>
public abstract class SchemaFormTestContext : BunitContext, IAsyncLifetime, IDisposable
{
    protected SchemaFormTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddMudServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddApplication();
        Services.AddInfrastructure(new ConfigurationBuilder().Build());

        // MudSelect and MudTooltip render through the popover provider.
        Render<MudPopoverProvider>();
    }

    // MudBlazor's services implement only IAsyncDisposable, which bUnit's synchronous Dispose rejects.
    // Re-implementing both interfaces here makes xUnit dispose the context asynchronously instead.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    void IDisposable.Dispose()
    {
    }

    protected IRenderedComponent<SchemaForm> RenderForm(
        string schema,
        string? hints = null,
        string? value = null,
        Action<string>? onChange = null,
        Action<string>? onSubmit = null,
        bool showSubmit = false) =>
        Render<SchemaForm>(parameters =>
        {
            parameters
                .Add(p => p.Schema, new JsonSchemaDocument(schema))
                .Add(p => p.Hints, hints is null ? UiHints.Empty : UiHints.Parse(hints).Value)
                .Add(p => p.Value, value)
                .Add(p => p.ShowSubmit, showSubmit);
            if (onChange is not null)
            {
                parameters.Add(p => p.ValueChanged, EventCallback.Factory.Create<string>(this, onChange));
            }

            if (onSubmit is not null)
            {
                parameters.Add(p => p.OnSubmit, EventCallback.Factory.Create<string>(this, onSubmit));
            }
        });

    /// <summary>Wraps one property schema into an object schema with that single required property "x".</summary>
    protected static string Single(string propertySchema, bool required = true) =>
        $$"""{ "type": "object", "properties": { "x": {{propertySchema}} }{{(required ? """, "required": ["x"]""" : string.Empty)}} }""";
}
