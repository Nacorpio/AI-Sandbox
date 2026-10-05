using AISandbox.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AISandbox.Application.Tests.Logging;

public sealed class SecretRedactionTests
{
    [Theory]
    [InlineData("Authorization: Bearer abc.def-ghi_123456")]
    [InlineData("authorization=\"Bearer abc.def-ghi_123456\"")]
    [InlineData("calling with key sk-or-v1-0123456789abcdef0123")]
    [InlineData("token ghp_abcdefghijklmnopqrstuvwxyz0123")]
    public void Credentials_are_removed_from_log_text(string text)
    {
        var redacted = SecretRedactor.Redact(text);

        Assert.Contains(SecretRedactor.Placeholder, redacted);
        Assert.DoesNotContain("abc.def-ghi_123456", redacted);
        Assert.DoesNotContain("0123456789abcdef", redacted);
        Assert.DoesNotContain("abcdefghijklmnop", redacted);
    }

    [Fact]
    public void Ordinary_text_is_untouched()
    {
        const string text = "Registered provider OpenRouter at https://openrouter.ai/api/v1";

        Assert.Equal(text, SecretRedactor.Redact(text));
    }

    [Fact]
    public void Wrapped_providers_receive_redacted_messages_and_properties()
    {
        var sink = new CapturingProvider();
        using var services = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(sink).AddSecretRedaction())
            .BuildServiceProvider();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("test");

        logger.LogInformation("Sending {Header} with {ApiKey} to {Url}",
            "Authorization: Bearer secret-token-value", "sk-or-v1-0123456789abcdef0123", "https://openrouter.ai");

        var entry = Assert.Single(sink.Entries);
        Assert.DoesNotContain("secret-token-value", entry.Message);
        Assert.DoesNotContain("0123456789abcdef", entry.Message);
        Assert.Contains("https://openrouter.ai", entry.Message);
        Assert.Equal(SecretRedactor.Placeholder, entry.Properties["ApiKey"]);
        Assert.DoesNotContain("secret-token-value", (string)entry.Properties["Header"]!);
        Assert.Equal("https://openrouter.ai", entry.Properties["Url"]);
    }

    private sealed record Entry(string Message, IReadOnlyDictionary<string, object?> Properties);

    private sealed class CapturingProvider : ILoggerProvider
    {
        public List<Entry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var properties = (state as IEnumerable<KeyValuePair<string, object?>> ?? [])
                    .ToDictionary(p => p.Key, p => p.Value);
                owner.Entries.Add(new Entry(formatter(state, exception), properties));
            }
        }
    }
}
