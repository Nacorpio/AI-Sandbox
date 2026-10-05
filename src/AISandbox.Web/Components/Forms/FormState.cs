using System.Text.Json.Nodes;
using AISandbox.Application.Abstractions;
using AISandbox.Application.Forms;

namespace AISandbox.Web.Components.Forms;

/// <summary>
/// The value being edited plus the validation issues shown against it. Fields read and write
/// it by JSON Pointer; empty values are removed so optional fields stay absent.
/// </summary>
public sealed class FormState(Func<string, IReadOnlyList<ValidationIssue>> validate)
{
    private IReadOnlyList<ValidationIssue> _issues = [];

    public JsonNode? Root { get; private set; }

    public event Action? Changed;

    public bool IsValid => _issues.Count == 0;

    public IEnumerable<ValidationIssue> IssuesAt(string pointer) => _issues.Where(i => i.Pointer == pointer);

    public IEnumerable<ValidationIssue> IssuesUnder(string pointer) =>
        _issues.Where(i => i.Pointer == pointer || i.Pointer.StartsWith(pointer + "/", StringComparison.Ordinal));

    public string ToJson() => Root?.ToJsonString() ?? "null";

    public void Load(JsonNode? root)
    {
        Root = root;
        _issues = [];
    }

    public JsonNode? Get(string pointer)
    {
        var node = Root;
        foreach (var segment in Segments(pointer))
        {
            node = node switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count => array[index],
                _ => null,
            };
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    /// <summary>Writes a value, creating missing parent objects. A null value removes the entry.</summary>
    public void Set(string pointer, JsonNode? value)
    {
        var segments = Segments(pointer).ToList();
        if (segments.Count == 0)
        {
            Root = value;
            Changed?.Invoke();
            return;
        }

        Root ??= new JsonObject();
        var parent = Root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var next = Child(parent, segments[i]);
            if (next is null)
            {
                next = new JsonObject();
                Assign(parent, segments[i], next);
            }

            parent = next;
        }

        Assign(parent, segments[^1], value);
        Changed?.Invoke();
    }

    public void Validate()
    {
        _issues = validate(ToJson());
        Changed?.Invoke();
    }

    public static JsonNode? DefaultFor(FormNode node) => node switch
    {
        ObjectField => new JsonObject(),
        CardListField or ChipsField => new JsonArray(),
        SwitchField => JsonValue.Create(false),
        OneOfField oneOf when oneOf.Variants.Count > 0 => DefaultFor(oneOf.Variants[0].Node),
        _ => null,
    };

    private static JsonNode? Child(JsonNode parent, string segment) => parent switch
    {
        JsonObject obj => obj[segment],
        JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count => array[index],
        _ => null,
    };

    private static void Assign(JsonNode parent, string segment, JsonNode? value)
    {
        switch (parent)
        {
            case JsonObject obj:
                if (value is null)
                {
                    obj.Remove(segment);
                }
                else
                {
                    obj[segment] = value;
                }

                break;
            case JsonArray array when int.TryParse(segment, out var index):
                while (array.Count <= index)
                {
                    array.Add(null);
                }

                array[index] = value;
                break;
        }
    }

    private static IEnumerable<string> Segments(string pointer) =>
        pointer.Length == 0
            ? []
            : pointer.TrimStart('/').Split('/').Select(s => s.Replace("~1", "/").Replace("~0", "~"));

    public static string Child(string pointer, string key) => $"{pointer}/{key.Replace("~", "~0").Replace("/", "~1")}";
}
