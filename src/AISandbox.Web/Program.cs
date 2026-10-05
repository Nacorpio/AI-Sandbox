using AISandbox.Application;
using AISandbox.Application.Telemetry;
using AISandbox.Infrastructure;
using AISandbox.Infrastructure.Logging;
using AISandbox.Web;
using AISandbox.Web.Components;
using MudBlazor.Services;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddScoped<UseCases>();

var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("AISandbox"))
    .WithTracing(tracing => tracing
        .AddSource(ApplicationTelemetry.RunsSourceName)
        .AddAspNetCoreInstrumentation())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation());

// Export only when a collector is configured (for example the Aspire dashboard).
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    telemetry.WithLogging().UseOtlpExporter();
}

// Must run after every logger provider is registered: it wraps them all.
builder.Logging.AddSecretRedaction();

var app = builder.Build();

await app.Services.MigrateDatabaseAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
