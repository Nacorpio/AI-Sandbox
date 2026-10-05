namespace AISandbox.Domain.Experimentation;

/// <summary>
/// The single output shape all protocols are normalised into, so comparisons never care which
/// protocol produced a result.
/// </summary>
public abstract record NormalizedOutput;

/// <summary>
/// Answers from a decision model, keyed by question key.
/// </summary>
public sealed record DecisionOutput(IReadOnlyDictionary<string, Answer> Answers) : NormalizedOutput;

public abstract record Answer;

/// <param name="Choice">The option with the highest probability.</param>
/// <param name="Confidence">Provider-reported certainty in [0, 1], when given.</param>
/// <param name="Probabilities">Probability per option name.</param>
public sealed record ChoiceAnswer(string Choice, double? Confidence, IReadOnlyDictionary<string, double> Probabilities) : Answer;

/// <param name="Score">Probability-weighted level, where 0 is the lowest level.</param>
/// <param name="Legend">Level index to level description.</param>
/// <param name="Probabilities">Probability per level index.</param>
public sealed record ScoreAnswer(
    double Score,
    double? Confidence,
    IReadOnlyDictionary<string, string> Legend,
    IReadOnlyDictionary<string, double> Probabilities) : Answer;

/// <param name="Value">Probability in [0, 1] that the statement is true.</param>
public sealed record NoulAnswer(double Value) : Answer;

public sealed record TokenUsage(int InputTokens, int OutputTokens)
{
    public static TokenUsage None { get; } = new(0, 0);
}

/// <param name="Total">From sending the request to reading the whole response.</param>
/// <param name="TimeToFirstByte">From sending the request to receiving response headers.</param>
public sealed record Latency(TimeSpan Total, TimeSpan? TimeToFirstByte);

public enum CostSource
{
    Calculated,
    ProviderReported,
}

/// <param name="Code">Stable identifier such as "http.401" or "timeout".</param>
/// <param name="HttpStatus">Status code returned by the provider, when there was a response.</param>
public sealed record ExecutionError(string Code, string Message, int? HttpStatus);
