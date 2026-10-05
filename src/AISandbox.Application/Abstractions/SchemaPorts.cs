using AISandbox.Application.Forms;
using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Catalog.Models;

namespace AISandbox.Application.Abstractions;

/// <param name="Pointer">JSON Pointer of the offending value, "" for the document itself.</param>
public sealed record ValidationIssue(string Pointer, string Message);

public sealed record ValidationOutcome(IReadOnlyList<ValidationIssue> Issues)
{
    public static ValidationOutcome Valid { get; } = new([]);

    public bool IsValid => Issues.Count == 0;
}

/// <summary>
/// The one JSON Schema validator. The forms, the model editor and server-side use cases all
/// call it, so a value the form accepts is a value the server accepts.
/// </summary>
public interface ISchemaValidator
{
    /// <summary>Checks that the text is a JSON Schema (2020-12) and that its x-ui hints use known widgets.</summary>
    ValidationOutcome CheckSchema(string schemaJson);

    /// <summary>Validates an instance against a schema. An unusable schema is reported as an issue on "".</summary>
    ValidationOutcome Validate(JsonSchemaDocument schema, string instanceJson);
}

public interface IFormModelBuilder
{
    Result<FormModel> Build(JsonSchemaDocument schema, UiHints hints);
}
