using System.Collections;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AISandbox.Infrastructure.Logging;

/// <summary>
/// Removes credentials from log text: Authorization header values and well-known API key shapes.
/// </summary>
public static partial class SecretRedactor
{
    public const string Placeholder = "[REDACTED]";

    private static readonly string[] SensitiveNames =
        ["authorization", "apikey", "api_key", "api-key", "token", "secret", "password", "x-api-key"];

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = AuthorizationHeader().Replace(text, m => $"{m.Groups["name"].Value}{Placeholder}");
        return KnownKeyShape().Replace(text, Placeholder);
    }

    /// <summary>
    /// True when a structured log property name suggests it carries a credential.
    /// </summary>
    public static bool IsSensitiveName(string name) =>
        SensitiveNames.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?<name>\bAuthorization\s*[:=]\s*""?)(?:Bearer\s+|Basic\s+)?[^\s"",;]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeader();

    // OpenRouter (sk-or-...), OpenAI-style (sk-...), GitHub (gh*_...), and generic bearer-looking tokens after "Bearer".
    [GeneratedRegex(@"\b(?:sk-or-v1-[A-Za-z0-9]{16,}|sk-[A-Za-z0-9_\-]{20,}|gh[pousr]_[A-Za-z0-9]{20,})|(?<=\bBearer\s)[A-Za-z0-9._\-]{8,}")]
    private static partial Regex KnownKeyShape();
}

internal sealed class RedactingLoggerProvider(ILoggerProvider inner) : ILoggerProvider, ISupportExternalScope
{
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        if (inner is ISupportExternalScope supportsScopes)
        {
            supportsScopes.SetScopeProvider(scopeProvider);
        }
    }

    public void Dispose() => inner.Dispose();

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = SecretRedactor.Redact(formatter(state, exception));
            var redactedState = new RedactedState(message, state as IEnumerable<KeyValuePair<string, object?>>);
            inner.Log(logLevel, eventId, redactedState, exception, static (s, _) => s.Message);
        }
    }

    /// <summary>
    /// Structured state with sensitive property values replaced, so providers that read
    /// properties (JSON console, OpenTelemetry) see the redacted form as well.
    /// </summary>
    private sealed class RedactedState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly List<KeyValuePair<string, object?>> _properties;

        public RedactedState(string message, IEnumerable<KeyValuePair<string, object?>>? original)
        {
            Message = message;
            _properties = (original ?? [])
                .Select(p => new KeyValuePair<string, object?>(p.Key, RedactValue(p.Key, p.Value)))
                .ToList();
        }

        public string Message { get; }

        public int Count => _properties.Count;

        public KeyValuePair<string, object?> this[int index] => _properties[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _properties.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => Message;

        private static object? RedactValue(string key, object? value)
        {
            if (key == "{OriginalFormat}")
            {
                return value;
            }

            if (SecretRedactor.IsSensitiveName(key))
            {
                return SecretRedactor.Placeholder;
            }

            return value is string text ? SecretRedactor.Redact(text) : value;
        }
    }
}

public static class LoggingBuilderExtensions
{
    /// <summary>
    /// Wraps every logger provider registered so far, so redaction applies whatever the sink.
    /// Call after all providers are added.
    /// </summary>
    public static ILoggingBuilder AddSecretRedaction(this ILoggingBuilder logging)
    {
        var services = logging.Services;
        var registrations = services.Where(d => d.ServiceType == typeof(ILoggerProvider)).ToList();
        foreach (var registration in registrations)
        {
            services.Remove(registration);
            services.AddSingleton<ILoggerProvider>(sp => new RedactingLoggerProvider(Resolve(sp, registration)));
        }

        return logging;
    }

    private static ILoggerProvider Resolve(IServiceProvider sp, ServiceDescriptor registration) => registration switch
    {
        { ImplementationInstance: ILoggerProvider instance } => instance,
        { ImplementationFactory: { } factory } => (ILoggerProvider)factory(sp),
        { ImplementationType: { } type } => (ILoggerProvider)ActivatorUtilities.CreateInstance(sp, type),
        _ => throw new InvalidOperationException("Unsupported logger provider registration."),
    };
}
