using System.Diagnostics;

namespace AISandbox.Application.Telemetry;

public static class ApplicationTelemetry
{
    public const string RunsSourceName = "AISandbox.Runs";

    public static ActivitySource Runs { get; } = new(RunsSourceName);
}
