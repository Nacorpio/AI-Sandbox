using System.Text.Json;
using System.Text.Json.Nodes;
using AISandbox.Application.Abstractions;
using AISandbox.Domain.Catalog.Models;
using Json.Schema;

namespace AISandbox.Infrastructure.Schemas;

/// <summary>
/// JSON Schema 2020-12 validation on JsonSchema.Net. Also enforces the closed x-ui vocabulary
/// inside schemas, so a schema that names an unknown widget never reaches the form renderer.
/// </summary>
internal sealed class JsonSchemaValidator : ISchemaValidator
{
    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List };

    public ValidationOutcome CheckSchema(string schemaJson)
    {
        var parsed = TryParse(schemaJson, out var error);
        if (parsed is null)
        {
            return Fail("", error!);
        }

        if (parsed is not JsonObject and not JsonValue)
        {
            return Fail("", "A schema must be a JSON object or a boolean.");
        }

        var result = MetaSchemas.Draft202012.Evaluate(ToElement(parsed), Options);
        if (!result.IsValid)
        {
            return new ValidationOutcome(Issues(result));
        }

        var unknown = new List<ValidationIssue>();
        FindUnknownWidgets(parsed, "", unknown);
        return unknown.Count == 0 ? ValidationOutcome.Valid : new ValidationOutcome(unknown);
    }

    public ValidationOutcome Validate(JsonSchemaDocument schema, string instanceJson)
    {
        var parsedSchema = TryParse(schema.Json, out var schemaError);
        if (parsedSchema is null)
        {
            return Fail("", $"The schema is not valid JSON: {schemaError}");
        }

        JsonElement instance;
        try
        {
            using var document = JsonDocument.Parse(instanceJson);
            instance = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            return Fail("", $"The value is not valid JSON: {exception.Message}");
        }

        JsonSchema compiled;
        try
        {
            compiled = JsonSchema.FromText(schema.Json);
        }
        catch (Exception exception) when (exception is JsonException or JsonSchemaException)
        {
            return Fail("", $"The schema cannot be used: {exception.Message}");
        }

        var result = compiled.Evaluate(instance, Options);
        return result.IsValid ? ValidationOutcome.Valid : new ValidationOutcome(Issues(result));
    }

    private static JsonElement ToElement(JsonNode node) => JsonSerializer.SerializeToElement(node);

    private static JsonNode? TryParse(string json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The schema is empty.";
            return null;
        }

        try
        {
            return JsonNode.Parse(json) ?? throw new JsonException("null is not a schema.");
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return null;
        }
    }

    private static ValidationOutcome Fail(string pointer, string message) => new([new ValidationIssue(pointer, message)]);

    private static List<ValidationIssue> Issues(EvaluationResults result)
    {
        var issues = new List<ValidationIssue>();
        foreach (var detail in (result.Details ?? []).Where(d => !d.IsValid && d.Errors is { Count: > 0 }))
        {
            foreach (var (keyword, message) in detail.Errors!)
            {
                issues.Add(new ValidationIssue(detail.InstanceLocation.ToString(), message.Length == 0 ? keyword : message));
            }
        }

        if (issues.Count == 0)
        {
            issues.Add(new ValidationIssue("", "The value does not match the schema."));
        }

        return issues
            .DistinctBy(i => (i.Pointer, i.Message))
            .Select(i => i with { Pointer = Normalise(i.Pointer) })
            .ToList();
    }

    // JsonSchema.Net writes pointers as "/a/b" or "#/a/b" depending on version; the forms use "/a/b".
    private static string Normalise(string pointer) => pointer.StartsWith('#') ? pointer[1..] : pointer;

    private static void FindUnknownWidgets(JsonNode? node, string path, List<ValidationIssue> issues)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["x-ui"] is JsonObject ui && ui["widget"] is JsonValue widget
                    && widget.TryGetValue<string>(out var name) && UiHints.ParseWidget(name) is null)
                {
                    issues.Add(new ValidationIssue(path, $"Unknown widget '{name}' in x-ui at '{(path.Length == 0 ? "/" : path)}'. Known widgets: {UiHints.KnownWidgets}."));
                }

                foreach (var (key, child) in obj)
                {
                    FindUnknownWidgets(child, $"{path}/{key}", issues);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    FindUnknownWidgets(array[i], $"{path}/{i}", issues);
                }

                break;
        }
    }
}
