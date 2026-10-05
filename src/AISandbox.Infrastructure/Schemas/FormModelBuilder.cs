using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AISandbox.Application.Abstractions;
using AISandbox.Application.Forms;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Catalog.Models;

namespace AISandbox.Infrastructure.Schemas;

/// <summary>
/// Turns a JSON Schema plus UI hints into the widget tree the form renders. The mapping is
/// deterministic: anything it cannot express falls back to a JSON editor, never to a guess.
/// </summary>
internal sealed class FormModelBuilder : IFormModelBuilder
{
    private const int EnumSegmentLimit = 5;
    private const int TextareaThreshold = 200;
    private const int MaxDepth = 12;

    public Result<FormModel> Build(JsonSchemaDocument schema, UiHints hints)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(schema.Json);
        }
        catch (JsonException exception)
        {
            return Error.Validation("schema", $"The schema is not valid JSON: {exception.Message}");
        }

        if (root is not JsonObject rootSchema)
        {
            return Error.Validation("schema", "The schema must be a JSON object.");
        }

        try
        {
            var node = BuildNode(new Context(rootSchema, hints), rootSchema, key: string.Empty, hintPath: string.Empty, required: true, depth: 0);
            return new FormModel(node);
        }
        catch (FormBuildException exception)
        {
            return Error.Validation("schema", exception.Message);
        }
    }

    private sealed record Context(JsonObject Root, UiHints Hints);

    private sealed class FormBuildException(string message) : Exception(message);

    private static FormNode BuildNode(Context context, JsonObject schema, string key, string hintPath, bool required, int depth)
    {
        var resolved = Resolve(context.Root, schema);
        var hint = HintFor(context, resolved, hintPath);
        var label = hint.Label ?? Text(resolved, "title") ?? Humanise(key);
        var help = hint.Help ?? Text(resolved, "description");

        if (depth > MaxDepth || hint.Widget == UiWidget.Json)
        {
            return new JsonField(key, label, help, required, hint.Group, hint.Order);
        }

        var variants = resolved["oneOf"] as JsonArray ?? resolved["anyOf"] as JsonArray;
        if (variants is { Count: > 0 })
        {
            var built = variants
                .Select((variant, index) => variant as JsonObject is { } variantSchema
                    ? new OneOfVariant(
                        VariantTitle(context.Root, variantSchema, index),
                        BuildNode(context, variantSchema, key, hintPath, required, depth + 1))
                    : null)
                .ToList();
            return built.Any(v => v is null)
                ? new JsonField(key, label, help, required, hint.Group, hint.Order)
                : new OneOfField(key, label, help, required, hint.Group, hint.Order, built!);
        }

        var type = SingleType(resolved);
        return type switch
        {
            "string" => BuildString(resolved, hint, key, label, help, required),
            "integer" or "number" => BuildNumber(resolved, hint, key, label, help, required, type == "integer"),
            "boolean" => new SwitchField(key, label, help, required, hint.Group, hint.Order),
            "object" => BuildObject(context, resolved, hint, key, hintPath, label, help, required, depth),
            "array" => BuildArray(context, resolved, hint, key, hintPath, label, help, required, depth),
            _ => new JsonField(key, label, help, required, hint.Group, hint.Order),
        };
    }

    private static FormNode BuildString(JsonObject schema, FieldHint hint, string key, string label, string? help, bool required)
    {
        var options = StringEnum(schema);
        if (options is not null)
        {
            var segmented = hint.Widget switch
            {
                UiWidget.Segmented => true,
                UiWidget.Select => false,
                _ => options.Count <= EnumSegmentLimit,
            };
            return new EnumField(key, label, help, required, hint.Group, hint.Order, options, segmented);
        }

        var format = hint.Widget switch
        {
            UiWidget.Uri => TextFormat.Uri,
            UiWidget.Email => TextFormat.Email,
            UiWidget.Date => TextFormat.Date,
            _ => Text(schema, "format") switch
            {
                "uri" => TextFormat.Uri,
                "email" => TextFormat.Email,
                "date" => TextFormat.Date,
                _ => TextFormat.Text,
            },
        };
        var multiline = hint.Widget == UiWidget.Textarea
            || (hint.Widget != UiWidget.Text && format == TextFormat.Text && Number(schema, "maxLength") > TextareaThreshold);
        return new TextField(key, label, help, required, hint.Group, hint.Order, format, multiline && format == TextFormat.Text);
    }

    private static FormNode BuildNumber(JsonObject schema, FieldHint hint, string key, string label, string? help, bool required, bool integer)
    {
        var minimum = Number(schema, "minimum");
        var maximum = Number(schema, "maximum");
        var slider = minimum is not null && maximum is not null && hint.Widget is null or UiWidget.Slider;
        return new NumberField(key, label, help, required, hint.Group, hint.Order, integer, minimum, maximum, slider);
    }

    private static FormNode BuildObject(
        Context context, JsonObject schema, FieldHint hint, string key, string hintPath, string label, string? help, bool required, int depth)
    {
        if (schema["properties"] is not JsonObject properties || properties.Count == 0)
        {
            return new JsonField(key, label, help, required, hint.Group, hint.Order);
        }

        var requiredNames = (schema["required"] as JsonArray)?
            .Select(n => n?.GetValue<string>()).OfType<string>().ToHashSet() ?? [];
        var children = properties
            .Select((property, index) => (Property: property, Index: index))
            .Where(p => p.Property.Value is JsonObject)
            .Select(p => (
                p.Index,
                Node: BuildNode(context, (JsonObject)p.Property.Value!, p.Property.Key, $"{hintPath}/{Escape(p.Property.Key)}", requiredNames.Contains(p.Property.Key), depth + 1)))
            .OrderBy(c => c.Node.Order ?? int.MaxValue)
            .ThenBy(c => c.Index)
            .Select(c => c.Node)
            .ToList();
        return new ObjectField(key, label, help, required, hint.Group, hint.Order, children);
    }

    private static FormNode BuildArray(
        Context context, JsonObject schema, FieldHint hint, string key, string hintPath, string label, string? help, bool required, int depth)
    {
        if (schema["items"] is not JsonObject items)
        {
            return new JsonField(key, label, help, required, hint.Group, hint.Order);
        }

        var resolvedItems = Resolve(context.Root, items);
        var itemType = SingleType(resolvedItems);
        if (itemType is "string" or "integer" or "number" or "boolean" && resolvedItems["oneOf"] is null && resolvedItems["anyOf"] is null)
        {
            return new ChipsField(key, label, help, required, hint.Group, hint.Order, itemType is "integer" or "number");
        }

        var item = BuildNode(context, items, "*", $"{hintPath}/*", required: true, depth + 1);
        return item is JsonField
            ? new JsonField(key, label, help, required, hint.Group, hint.Order)
            : new CardListField(key, label, help, required, hint.Group, hint.Order, item);
    }

    private static FieldHint HintFor(Context context, JsonObject schema, string hintPath)
    {
        var inline = schema["x-ui"];
        FieldHint? fromSchema = null;
        if (inline is JsonObject ui)
        {
            var parsed = FieldHint.FromJson(JsonSerializer.SerializeToElement(ui), hintPath.Length == 0 ? "/" : hintPath);
            if (parsed.IsFailure)
            {
                throw new FormBuildException(parsed.Error!.Message);
            }

            fromSchema = parsed.Value;
        }

        var overlay = context.Hints.For(hintPath);
        return new FieldHint(
            overlay?.Label ?? fromSchema?.Label,
            overlay?.Help ?? fromSchema?.Help,
            overlay?.Group ?? fromSchema?.Group,
            overlay?.Order ?? fromSchema?.Order,
            overlay?.Widget ?? fromSchema?.Widget);
    }

    // Follows a local "$ref": "#/$defs/name". Remote or cyclic references are left as they are,
    // which makes the node a JSON field.
    private static JsonObject Resolve(JsonObject root, JsonObject schema)
    {
        for (var hops = 0; hops < 8 && schema["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var path); hops++)
        {
            if (!path.StartsWith("#/", StringComparison.Ordinal))
            {
                return schema;
            }

            JsonNode? target = root;
            foreach (var segment in path[2..].Split('/'))
            {
                target = (target as JsonObject)?[Unescape(segment)];
            }

            if (target is not JsonObject targetSchema)
            {
                return schema;
            }

            schema = targetSchema;
        }

        return schema;
    }

    private static string VariantTitle(JsonObject root, JsonObject variant, int index)
    {
        if (Text(variant, "title") is { } title)
        {
            return title;
        }

        if (variant["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var path))
        {
            return Humanise(path[(path.LastIndexOf('/') + 1)..]);
        }

        return Text(Resolve(root, variant), "title") ?? $"Option {index + 1}";
    }

    // A single non-null type, or null when the schema is untyped or a union (those fall back to JSON).
    private static string? SingleType(JsonObject schema)
    {
        var type = schema["type"];
        if (type is JsonValue value && value.TryGetValue<string>(out var name))
        {
            return name;
        }

        if (type is JsonArray array)
        {
            var names = array.Select(n => n?.GetValue<string>()).Where(n => n is not (null or "null")).ToList();
            return names.Count == 1 ? names[0] : null;
        }

        return schema["enum"] is JsonArray && StringEnum(schema) is not null ? "string" : null;
    }

    private static List<string>? StringEnum(JsonObject schema)
    {
        if (schema["enum"] is not JsonArray values || values.Count == 0)
        {
            return null;
        }

        var options = new List<string>(values.Count);
        foreach (var option in values)
        {
            if (option is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                return null;
            }

            options.Add(text);
        }

        return options;
    }

    private static string? Text(JsonObject schema, string name) =>
        schema[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static decimal? Number(JsonObject schema, string name) =>
        schema[name] is JsonValue value && value.TryGetValue<decimal>(out var number) ? number : null;

    // "maxTokens" and "max_tokens" both read as "Max tokens".
    private static string Humanise(string key)
    {
        if (key.Length == 0)
        {
            return string.Empty;
        }

        var words = new StringBuilder();
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (c is '_' or '-' or '.')
            {
                words.Append(' ');
            }
            else if (char.IsUpper(c) && i > 0 && char.IsLower(key[i - 1]))
            {
                words.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                words.Append(i == 0 ? char.ToUpperInvariant(c) : c);
            }
        }

        return words.ToString();
    }

    private static string Escape(string segment) => segment.Replace("~", "~0").Replace("/", "~1");

    private static string Unescape(string segment) => segment.Replace("~1", "/").Replace("~0", "~");
}
