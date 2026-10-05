using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AISandbox.Domain.Authoring.QuestionSets;
using AISandbox.Domain.Authoring.Questions;
using AISandbox.Domain.Catalog.Models;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Infrastructure.Persistence;

/// <summary>
/// JSON shapes for domain values stored in JSON columns. Domain types carry no serialization
/// attributes; polymorphism and private constructors are handled here.
/// </summary>
internal static class DomainJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Stored {typeof(T).Name} is null.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new StructuredTextConverter());
        options.Converters.Add(new QuestionConverter());
        options.Converters.Add(new RunInputConverter());
        options.Converters.Add(new PricingScheduleConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { AddPolymorphism },
        };
        return options;
    }

    private static void AddPolymorphism(JsonTypeInfo info)
    {
        if (info.Type == typeof(NormalizedOutput))
        {
            info.PolymorphismOptions = Polymorphism("kind", (typeof(DecisionOutput), "decision"));
        }
        else if (info.Type == typeof(Answer))
        {
            info.PolymorphismOptions = Polymorphism(
                "type",
                (typeof(ChoiceAnswer), "choice"),
                (typeof(ScoreAnswer), "score"),
                (typeof(NoulAnswer), "noul"));
        }
    }

    private static JsonPolymorphismOptions Polymorphism(
        string discriminator,
        params (Type Type, string Name)[] derived)
    {
        var options = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = discriminator,
            UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
        };
        foreach (var (type, name) in derived)
        {
            options.DerivedTypes.Add(new JsonDerivedType(type, name));
        }

        return options;
    }

    private sealed class StructuredTextConverter : JsonConverter<StructuredText>
    {
        public override StructuredText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return StructuredText.FromJson(document.RootElement.GetRawText()).Value;
        }

        public override void Write(Utf8JsonWriter writer, StructuredText value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value.Json);
    }

    private sealed record QuestionDocument(
        string Type,
        string Key,
        StructuredText Instructions,
        IReadOnlyList<OptionDocument>? Options,
        IReadOnlyList<StructuredText>? Levels,
        StructuredText? WhenTrue,
        StructuredText? WhenFalse);

    private sealed record OptionDocument(string Name, StructuredText? Description);

    private sealed class QuestionConverter : JsonConverter<Question>
    {
        public override Question Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var document = JsonSerializer.Deserialize<QuestionDocument>(ref reader, options)!;
            var key = QuestionKey.Create(document.Key).Value;
            return document.Type switch
            {
                "choice" => ChoiceQuestion.Create(
                    key,
                    document.Instructions,
                    (document.Options ?? []).Select(o => ChoiceOption.Create(o.Name, o.Description).Value).ToList()).Value,
                "score" => ScoreQuestion.Create(key, document.Instructions, document.Levels ?? []).Value,
                "noul" => new NoulQuestion(key, document.Instructions, document.WhenTrue, document.WhenFalse),
                _ => throw new JsonException($"Unknown question type '{document.Type}'."),
            };
        }

        public override void Write(Utf8JsonWriter writer, Question value, JsonSerializerOptions options)
        {
            var document = value switch
            {
                ChoiceQuestion c => new QuestionDocument("choice", c.Key.Value, c.Instructions,
                    c.Options.Select(o => new OptionDocument(o.Name, o.Description)).ToList(), null, null, null),
                ScoreQuestion s => new QuestionDocument("score", s.Key.Value, s.Instructions, null, s.Levels, null, null),
                NoulQuestion n => new QuestionDocument("noul", n.Key.Value, n.Instructions, null, null, n.WhenTrue, n.WhenFalse),
                _ => throw new JsonException($"Unknown question type {value.GetType().Name}."),
            };
            JsonSerializer.Serialize(writer, document, options);
        }
    }

    private sealed record QuestionSetRefDocument(Guid Id, int Version);

    private sealed record RunInputDocument(StructuredText State, IReadOnlyList<Question> Questions, QuestionSetRefDocument? QuestionSet = null);

    private sealed class RunInputConverter : JsonConverter<RunInput>
    {
        public override RunInput Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var document = JsonSerializer.Deserialize<RunInputDocument>(ref reader, options)!;
            var reference = document.QuestionSet is { } r ? new QuestionSetRef(new QuestionSetId(r.Id), r.Version) : null;
            return RunInput.Create(document.State, document.Questions, reference).Value;
        }

        public override void Write(Utf8JsonWriter writer, RunInput value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new RunInputDocument(
                value.State,
                value.Questions,
                value.QuestionSet is { } r ? new QuestionSetRefDocument(r.Id.Value, r.Version) : null),
                options);
    }

    private sealed record PricingDocument(string Currency, decimal InputPerMillion, decimal OutputPerMillion, decimal? CacheReadPerMillion);

    private sealed class PricingScheduleConverter : JsonConverter<PricingSchedule>
    {
        public override PricingSchedule Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var document = JsonSerializer.Deserialize<PricingDocument>(ref reader, options)!;
            return PricingSchedule.Create(document.Currency, document.InputPerMillion, document.OutputPerMillion, document.CacheReadPerMillion).Value;
        }

        public override void Write(Utf8JsonWriter writer, PricingSchedule value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(
                writer,
                new PricingDocument(value.Currency, value.InputPerMillion, value.OutputPerMillion, value.CacheReadPerMillion),
                options);
    }
}
