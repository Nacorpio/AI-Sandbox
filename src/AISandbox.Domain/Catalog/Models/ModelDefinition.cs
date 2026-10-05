using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Catalog.Providers;

namespace AISandbox.Domain.Catalog.Models;

/// <summary>
/// One callable model: a provider, the protocol it speaks there, the remote model id, its schemas,
/// what it accepts and what it costs.
/// </summary>
public sealed class ModelDefinition : AggregateRoot<ModelDefinitionId>
{
    public const int MaxDisplayNameLength = 100;

    // Used by persistence to materialise the aggregate.
    private ModelDefinition()
        : this(default)
    {
    }

    private ModelDefinition(ModelDefinitionId id)
        : base(id)
    {
        DisplayName = null!;
        Protocol = null!;
        RemoteId = null!;
        InputSchema = null!;
        OutputSchema = null!;
        Pricing = null!;
        Capabilities = null!;
    }

    public ProviderId ProviderId { get; private set; }

    public string DisplayName { get; private set; }

    public ModelKind Kind { get; private set; }

    public ProtocolId Protocol { get; private set; }

    public RemoteModelId RemoteId { get; private set; }

    public JsonSchemaDocument InputSchema { get; private set; }

    public JsonSchemaDocument OutputSchema { get; private set; }

    public PricingSchedule Pricing { get; private set; }

    public ModelCapabilities Capabilities { get; private set; }

    public TemplateOrigin? Origin { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Result<ModelDefinition> Create(
        Provider provider,
        string? displayName,
        ModelKind kind,
        ProtocolId protocol,
        RemoteModelId remoteId,
        JsonSchemaDocument inputSchema,
        JsonSchemaDocument outputSchema,
        PricingSchedule pricing,
        ModelCapabilities capabilities,
        TemplateOrigin? origin,
        DateTimeOffset now)
    {
        var name = displayName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxDisplayNameLength)
        {
            return Error.Validation("displayName", $"Display name is required and must be at most {MaxDisplayNameLength} characters.");
        }

        if ((kind == ModelKind.Decision) != protocol.IsDecisionProtocol && protocol != ProtocolId.Custom)
        {
            return Error.Validation("protocol", $"A {kind.ToString().ToLowerInvariant()} model cannot use the '{protocol}' protocol.");
        }

        var model = new ModelDefinition(ModelDefinitionId.New())
        {
            ProviderId = provider.Id,
            DisplayName = name,
            Kind = kind,
            Protocol = protocol,
            RemoteId = remoteId,
            InputSchema = inputSchema,
            OutputSchema = outputSchema,
            Pricing = pricing,
            Capabilities = capabilities,
            Origin = origin,
            CreatedAt = now,
        };
        model.Raise(new ModelDefinitionCreated(model.Id, name, now));
        return model;
    }
}

public sealed record ModelDefinitionCreated(ModelDefinitionId ModelId, string DisplayName, DateTimeOffset OccurredAt)
    : IDomainEvent;
