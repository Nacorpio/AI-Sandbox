using System.Text.Json;
using AISandbox.Domain.Abstractions;

namespace AISandbox.Domain.Catalog.Models;

/// <summary>
/// The closed set of widgets a schema form can render. Hints may only name these.
/// </summary>
public enum UiWidget
{
    Text,
    Textarea,
    Segmented,
    Select,
    Slider,
    Switch,
    Chips,
    Date,
    Email,
    Uri,
    Json,
}

/// <summary>
/// Presentation overrides for one field. Every member is optional.
/// </summary>
public sealed record FieldHint(string? Label = null, string? Help = null, string? Group = null, int? Order = null, UiWidget? Widget = null)
{
    public static Result<FieldHint> FromJson(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return Error.Validation("uiHints", $"The hint for '{where}' must be an object.");
        }

        string? label = null, help = null, group = null;
        int? order = null;
        UiWidget? widget = null;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "label" when property.Value.ValueKind == JsonValueKind.String:
                    label = property.Value.GetString();
                    break;
                case "help" when property.Value.ValueKind == JsonValueKind.String:
                    help = property.Value.GetString();
                    break;
                case "group" when property.Value.ValueKind == JsonValueKind.String:
                    group = property.Value.GetString();
                    break;
                case "order" when property.Value.TryGetInt32(out var number):
                    order = number;
                    break;
                case "widget" when property.Value.ValueKind == JsonValueKind.String:
                    var parsed = UiHints.ParseWidget(property.Value.GetString());
                    if (parsed is null)
                    {
                        return Error.Validation(
                            "uiHints",
                            $"Unknown widget '{property.Value.GetString()}' for '{where}'. Known widgets: {UiHints.KnownWidgets}.");
                    }

                    widget = parsed;
                    break;
                default:
                    return Error.Validation("uiHints", $"Unknown or malformed hint '{property.Name}' for '{where}'. Allowed: label, help, group, order, widget.");
            }
        }

        return new FieldHint(label, help, group, order, widget);
    }
}

/// <summary>
/// An optional overlay on a model's schemas: label, help, group, order and widget per field.
/// Fields are addressed by the JSON Pointer of the instance value ("/options/temperature");
/// array items use "*" in place of the index ("/tags/*").
/// </summary>
public sealed record UiHints(IReadOnlyDictionary<string, FieldHint> Fields)
{
    public static UiHints Empty { get; } = new(new Dictionary<string, FieldHint>());

    public static string KnownWidgets => string.Join(", ", Enum.GetNames<UiWidget>().Select(n => n.ToLowerInvariant()));

    public static UiWidget? ParseWidget(string? name) =>
        Enum.TryParse<UiWidget>(name, ignoreCase: true, out var widget) && Enum.IsDefined(widget) ? widget : null;

    public FieldHint? For(string path) => Fields.GetValueOrDefault(path);

    public static Result<UiHints> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            return Error.Validation("uiHints", $"UI hints are not valid JSON: {exception.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Error.Validation("uiHints", "UI hints must be a JSON object keyed by field path.");
            }

            var fields = new Dictionary<string, FieldHint>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.StartsWith('/') && property.Name.Length > 0)
                {
                    return Error.Validation("uiHints", $"Hint key '{property.Name}' must be a JSON Pointer such as '/options/temperature'.");
                }

                var hint = FieldHint.FromJson(property.Value, property.Name);
                if (hint.IsFailure)
                {
                    return hint.Error!;
                }

                fields[property.Name] = hint.Value;
            }

            return new UiHints(fields);
        }
    }

    public string ToJson()
    {
        var writerOptions = new JsonWriterOptions { Indented = true };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, writerOptions))
        {
            writer.WriteStartObject();
            foreach (var (path, hint) in Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject(path);
                if (hint.Label is not null) writer.WriteString("label", hint.Label);
                if (hint.Help is not null) writer.WriteString("help", hint.Help);
                if (hint.Group is not null) writer.WriteString("group", hint.Group);
                if (hint.Order is { } order) writer.WriteNumber("order", order);
                if (hint.Widget is { } widget) writer.WriteString("widget", widget.ToString().ToLowerInvariant());
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
