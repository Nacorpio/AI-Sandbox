using AISandbox.Domain.Catalog.Models;

namespace AISandbox.Application.Forms;

public enum TextFormat
{
    Text,
    Uri,
    Email,
    Date,
}

/// <summary>
/// One field of a schema-driven form. <see cref="Key"/> is the property name inside the parent
/// object, or "*" for the item of an array; the renderer composes instance pointers from it.
/// </summary>
public abstract record FormNode(string Key, string Label, string? Help, bool Required, string? Group, int? Order);

public sealed record TextField(string Key, string Label, string? Help, bool Required, string? Group, int? Order, TextFormat Format, bool Multiline)
    : FormNode(Key, Label, Help, Required, Group, Order);

public sealed record EnumField(string Key, string Label, string? Help, bool Required, string? Group, int? Order, IReadOnlyList<string> Options, bool Segmented)
    : FormNode(Key, Label, Help, Required, Group, Order);

public sealed record NumberField(
    string Key, string Label, string? Help, bool Required, string? Group, int? Order,
    bool IsInteger, decimal? Minimum, decimal? Maximum, bool Slider)
    : FormNode(Key, Label, Help, Required, Group, Order);

public sealed record SwitchField(string Key, string Label, string? Help, bool Required, string? Group, int? Order)
    : FormNode(Key, Label, Help, Required, Group, Order);

public sealed record ChipsField(string Key, string Label, string? Help, bool Required, string? Group, int? Order, bool NumericItems)
    : FormNode(Key, Label, Help, Required, Group, Order);

public sealed record ObjectField(string Key, string Label, string? Help, bool Required, string? Group, int? Order, IReadOnlyList<FormNode> Children)
    : FormNode(Key, Label, Help, Required, Group, Order)
{
    /// <summary>An optional object is collapsed until the user opens it.</summary>
    public bool Collapsible => !Required;
}

public sealed record CardListField(string Key, string Label, string? Help, bool Required, string? Group, int? Order, FormNode Item)
    : FormNode(Key, Label, Help, Required, Group, Order);

public sealed record OneOfVariant(string Title, FormNode Node);

public sealed record OneOfField(string Key, string Label, string? Help, bool Required, string? Group, int? Order, IReadOnlyList<OneOfVariant> Variants)
    : FormNode(Key, Label, Help, Required, Group, Order);

/// <summary>Anything the other widgets cannot express: the user edits raw JSON.</summary>
public sealed record JsonField(string Key, string Label, string? Help, bool Required, string? Group, int? Order)
    : FormNode(Key, Label, Help, Required, Group, Order);

public sealed record FormModel(FormNode Root);
