# ModelLab — Implementation Plan

A .NET 10 / C# web application for configuring AI chat models and decision models, running them
individually or in parallel against the same user data, chaining them into pipelines, and comparing
cost, latency and output side by side.

> Working name: **ModelLab**. Rename freely; nothing below depends on it.
> Research date: 2026-10-05. Prices and latencies below are snapshots taken that day and will drift.

---

## 1. Research findings (what the reference models actually are)

The most important finding shapes the whole design: **four of the five reference models do not need
bespoke JSON configuration.** Jev, Clef Flash and Perplexity Decider all speak the same wire contract,
the **System One** protocol that TypeSafe introduced. Span-01 is served through the same OpenRouter
Decisions router. Only the chat model (DeepSeek) uses a different protocol (OpenAI-compatible chat
completions).

### 1.1 The System One protocol (decision models)

Request:

```json
{
  "model": "jev-latest",
  "state": "free text, or any JSON object/array",
  "questions": {
    "department": { "type": "choice", "instructions": "...", "criteria": { "billing": "...", "technical": "..." } },
    "frustration": { "type": "score", "instructions": "...", "criteria": ["Calm", "Frustrated", "Angry"] },
    "is_urgent": { "type": "noul", "instructions": "...", "criteria": { "true": "...", "false": "..." } }
  }
}
```

Response:

```json
{
  "model": "jev-1.13.0",
  "answers": {
    "department": { "type": "choice", "choice": "technical", "confidence": 0.78, "probabilities": { "technical": 0.85, "billing": 0.15, "sales": 0.0 } },
    "frustration": { "type": "score", "score": 1.0, "confidence": 1.0, "legend": { "0": "Calm", "1": "Frustrated", "2": "Angry" }, "probabilities": { "0": 0, "1": 1, "2": 0 } },
    "is_urgent": { "type": "noul", "noul": 1.0 }
  },
  "usage": { "input_tokens": 392, "output_tokens": 65 }
}
```

Three question primitives:

| Type   | Asks                       | Returns                                  |
|--------|----------------------------|------------------------------------------|
| choice | Pick one option from a map | `choice`, `probabilities`, `confidence`  |
| score  | Place state on an ordinal rubric | `score` (probability-weighted), `legend`, `probabilities`, `confidence` |
| noul   | Is this statement true?    | `noul` (0–1)                             |

`instructions`, choice option values, score levels and noul `true`/`false` criteria all accept
**string, object, array or null**. Questions are evaluated in parallel and in isolation against one
state. Many questions per call barely change latency.

### 1.2 Per-model facts

| Model | Kind | Endpoint(s) | Input price | Output price | Context | Notes |
|---|---|---|---|---|---|---|
| **Jev 1.13** (TypeSafe) | Decision | `POST https://api.typesafe.ai/v1/systemone` (Bearer); OpenRouter `typesafe/jev-1.13`, alias `~typesafe/jev-latest` | $0.042 / Mtok | free | 64k native (32k state + longest question); 32k via OpenRouter | Aliases `jev-latest`, `jev-preview`. Response `model` reports the resolved version. 429 with `retry-after`. Text only. |
| **Clef Flash** (Cloudflare) | Decision | Workers AI `@cf/cloudflare/clef-flash` (REST: `POST https://api.cloudflare.com/client/v4/accounts/{account_id}/ai/run/@cf/cloudflare/clef-flash`); OpenRouter `cloudflare/clef-flash` | $0.09 / Mtok | free | 65,536 | 9B fine-tune of Qwen3.5-9B, open weights. Body needs `"model": "clef-flash"`. 1–64 questions; ids `[A-Za-z0-9_.-]`, max 100 chars. **Extension:** `images[]` (max 4, base64 PNG/JPEG/WebP, no URLs). Sibling `cloudflare/clef` (27B, $0.24/Mtok). |
| **Decider V1 27B** (Perplexity) | Decision | OpenRouter `perplexity/pplx-decider-v1-27b` | $0.04 / Mtok | free | 262,144 | Text + image input. p50 latency ≈ 321 ms, p99 ≈ 3.5 s. |
| **Span-01** (Respan) | Decision (behavior scoring) | OpenRouter `respan/span-01` | $0.02 / Mtok | free | not published | Reads a **conversation span**; for each plain-language behavior returns the probability the behavior is present. Built for evals, guardrails, monitoring. Free tier `respan/span-01-lite:free`. p50 ≈ 300 ms. |
| **DeepSeek V4.1 Flash** | Chat | OpenRouter `deepseek/deepseek-v4.1-flash` (OpenAI-compatible `/chat/completions`) | $0.003–$0.45 / Mtok depending on upstream provider | $0.18–$2.40 / Mtok | ~1M | Sparse MoE. Supports `reasoning`, `reasoning_effort`, `tools`, `response_format`, `structured_outputs` (provider-dependent). DeepSeek's own endpoint has **time-of-day pricing overrides** (2× on weekday UTC windows). |

OpenRouter exposes decision models two ways:

- `POST https://openrouter.ai/api/v1/systemone` — drop-in System One endpoint. Bare ids like
  `jev-latest` map onto `typesafe/`.
- `POST …/alpha/decisions` — the alpha Decisions router. Its response adds `id`, `provider` and
  **`usage.cost`** (actual USD charged). Verify the exact path in the OpenAPI spec before coding.

Other decision models already listed on OpenRouter that are worth shipping as extra templates:
`liquid/d1`, `upstage/solar-decide`, `inception/mercury-decide:free`, `togethercomputer/tev1-4b-experimental`
(choice only, 2–24 options), `jaredpalmer/kev-4b`.

Live per-endpoint pricing, latency percentiles and uptime are available, unauthenticated, from
`GET https://openrouter.ai/api/v1/models/{author}/{slug}/endpoints`. The app can sync pricing from it.

---

## 2. Goals and non-goals

**Goals**

1. Configure providers (base URL, auth, rate limits) and models (protocol, remote id, schemas, pricing).
2. Ship templates for Jev, Clef Flash, Decider V1 27B, Span-01 and DeepSeek V4.1 Flash.
3. Run one model, or many in parallel, against the same input, collected by a schema-driven dialog.
4. Compare cost, latency and output across models, visually.
5. Chain models into pipelines (output of one feeds input of another) and show the data flow as a
   hierarchical graph.
6. Strict DDD layering and constructor-injected DI throughout.

**Non-goals for v1**

- Multi-tenant SaaS, user accounts, billing. (Single user, local or self-hosted.)
- Fine-tuning or hosting models.
- A general-purpose workflow engine. Pipelines stay acyclic and request/response only.

---

## 3. Architecture overview

### 3.1 Style

Modular monolith, Clean/Onion layering, DDD tactical patterns. One deployable, four production
projects, dependencies pointing inward only.

```
ModelLab.Web  ──►  ModelLab.Application  ──►  ModelLab.Domain
      │                    ▲
      └──► ModelLab.Infrastructure ─┘  (implements Application ports)
```

- **Domain** — aggregates, value objects, domain events, domain services. No package references
  except the BCL. No EF, no HTTP, no JSON library types in public signatures.
- **Application** — use cases (commands/queries + handlers), ports (interfaces), DTOs, validation.
  References Domain only.
- **Infrastructure** — EF Core persistence, HTTP adapters per protocol, secret storage, pricing sync,
  schema tooling. References Application and Domain.
- **Web** — Blazor Web App (Interactive Server render mode), composition root, UI components.

Enforce the dependency rule with an architecture test project (ArchUnitNET or NetArchTest), so "strict
DDD" is verified in CI, not by convention.

### 3.2 Bounded contexts

| Context | Responsibility | Core aggregates |
|---|---|---|
| **Catalog** | Providers, models, templates, pricing | `Provider`, `ModelDefinition`, `ModelTemplate` (read-only seed) |
| **Authoring** | Reusable inputs: question sets for decision models, prompt templates for chat models, saved input fixtures | `QuestionSet`, `PromptTemplate`, `InputFixture` |
| **Experimentation** | Single and parallel runs, comparisons | `Run` (with `ModelExecution` entities) |
| **Pipelines** | Graph definitions and their executions | `Pipeline`, `PipelineRun` |

Contexts live as folders/namespaces inside each layer project, not separate assemblies. Split later
only if a context grows a reason to deploy alone.

### 3.3 Ubiquitous language (start a `GLOSSARY.md` on day one)

- **Provider** — an organisation plus an endpoint family plus credentials (TypeSafe, Cloudflare, OpenRouter).
- **Protocol** — a wire contract (System One, OpenAI Chat Completions, Workers AI envelope, OpenRouter Decisions).
- **Model definition** — one callable model: provider + protocol + remote model id + schemas + pricing.
- **State** — the content a decision model evaluates.
- **Question** — one typed decision request: choice, score or noul.
- **Question set** — a named, versioned group of questions.
- **Run** — one input sent to one or more model definitions at once.
- **Execution** — one model's result inside a run.
- **Pipeline** — a directed acyclic graph of model nodes joined by mapped edges.

Keep the term **"decision model"** in the UI. Use "System One" only as the protocol name.

---

## 4. Domain model

### 4.1 Catalog

```csharp
public sealed class Provider : AggregateRoot<ProviderId>
{
    public string Name { get; private set; }
    public ProviderKind Kind { get; private set; }          // TypeSafe, CloudflareWorkersAi, OpenRouter, Custom
    public EndpointUri BaseUrl { get; private set; }
    public AuthScheme Auth { get; private set; }            // Bearer, Header(name), None
    public SecretReference Secret { get; private set; }     // pointer only, never the key
    public RateLimitPolicy RateLimit { get; private set; }  // max concurrency, requests/s, tokens/s
    public IReadOnlyDictionary<string, string> PathVariables { get; } // e.g. account_id for Cloudflare
}

public sealed class ModelDefinition : AggregateRoot<ModelDefinitionId>
{
    public ProviderId ProviderId { get; private set; }
    public string DisplayName { get; private set; }
    public ModelKind Kind { get; private set; }             // Chat, Decision
    public ProtocolId Protocol { get; private set; }        // systemone, openai-chat, workers-ai, openrouter-decisions
    public RemoteModelId RemoteId { get; private set; }     // "jev-latest", "@cf/cloudflare/clef-flash", ...
    public string? PathOverride { get; private set; }
    public JsonSchemaDocument InputSchema { get; private set; }
    public JsonSchemaDocument OutputSchema { get; private set; }
    public UiHints UiHints { get; private set; }            // optional x-ui overlay, see §7
    public RequestTemplate? RequestTemplate { get; private set; }   // only for Custom protocol
    public ResponseMapping? ResponseMapping { get; private set; }   // only for Custom protocol
    public PricingSchedule Pricing { get; private set; }
    public ModelCapabilities Capabilities { get; private set; }     // images, max questions, context, structured output
    public TemplateOrigin? Origin { get; private set; }     // template id + version it came from
}
```

Value objects: `EndpointUri`, `RemoteModelId`, `JsonSchemaDocument` (raw JSON string plus a validated
flag; parsing happens in Infrastructure), `Money` (decimal + ISO currency), `TokenRate` (Money per
million tokens), `PricingSchedule` (base input/output/cache-read rates plus optional time-window
overrides, matching DeepSeek's), `TokenUsage`, `Latency`, `RateLimitPolicy`.

Invariants enforced inside the aggregates:

- A `Decision` model must use a decision protocol, and vice versa.
- `RequestTemplate`/`ResponseMapping` are required if and only if the protocol is `Custom`.
- Pricing rates are non-negative; currency is consistent across a schedule.

### 4.2 Authoring

```csharp
public sealed class QuestionSet : AggregateRoot<QuestionSetId>
{
    public string Name { get; private set; }
    public int Version { get; private set; }               // bumped on every change; runs pin a version
    public IReadOnlyList<Question> Questions { get; }
}

public abstract record Question(QuestionKey Key, StructuredText Instructions);
public sealed record ChoiceQuestion(QuestionKey Key, StructuredText Instructions,
    IReadOnlyDictionary<string, StructuredText?> Options) : Question(Key, Instructions);
public sealed record ScoreQuestion(QuestionKey Key, StructuredText Instructions,
    IReadOnlyList<StructuredText> Levels) : Question(Key, Instructions);
public sealed record NoulQuestion(QuestionKey Key, StructuredText Instructions,
    StructuredText? WhenTrue, StructuredText? WhenFalse) : Question(Key, Instructions);
```

`StructuredText` models the "string, object, array or null" `EntryType`. `QuestionKey` enforces the
Clef id rule (`[A-Za-z0-9_.-]{1,100}`), which is the strictest of the known providers.
`QuestionSet` enforces 1–64 questions and unique keys. Per-model limits (for example Tev1's 2–24
options) are checked at run time against `ModelCapabilities`.

`PromptTemplate` holds a system prompt, a user prompt with `{{placeholders}}`, and an optional output
JSON schema for structured outputs.

### 4.3 Experimentation

```csharp
public sealed class Run : AggregateRoot<RunId>
{
    public RunInput Input { get; private set; }            // immutable snapshot: state, question set version, prompt, images
    public IReadOnlyList<ModelExecution> Executions { get; }
    public RunStatus Status { get; private set; }
    public Money EstimatedCost { get; private set; }
    public Money? BudgetCap { get; private set; }

    public void Start(IClock clock);
    public void RecordSuccess(ModelExecutionId id, NormalizedOutput output, TokenUsage usage, Money cost, Latency latency, string resolvedModel);
    public void RecordFailure(ModelExecutionId id, ExecutionError error, Latency latency);
}
```

`ModelExecution` stores a **snapshot** of the model definition used (protocol, remote id, pricing),
the raw request and response bodies, the normalised output, token usage, cost (provider-reported
where available, otherwise calculated), latency (total and time-to-first-byte), the resolved model
version, and the attempt count.

Domain events: `RunStarted`, `ExecutionCompleted`, `ExecutionFailed`, `RunCompleted`. The UI subscribes
to these for live progress.

### 4.4 Pipelines

```csharp
public sealed class Pipeline : AggregateRoot<PipelineId>
{
    public IReadOnlyList<PipelineNode> Nodes { get; }
    public IReadOnlyList<PipelineEdge> Edges { get; }

    public void AddNode(PipelineNode node);
    public void Connect(PipelineNodeId from, PipelineNodeId to, EdgeMapping mapping, EdgeCondition? condition);
    // Connect() rejects cycles (via IPipelineGraphValidator domain service)
}

public sealed record PipelineNode(PipelineNodeId Id, ModelDefinitionId Model, NodeBinding Binding);
public sealed record EdgeMapping(IReadOnlyList<FieldMapping> Fields);   // source JSON Pointer → target JSON Pointer, optional transform
public sealed record EdgeCondition(string SourcePointer, ComparisonOperator Op, JsonScalar Value); // e.g. /answers/team/confidence < 0.7
```

Domain services:

- `PipelineGraphValidator` — acyclicity (Kahn's algorithm), one root or explicit entry nodes,
  every required target field is fed by a mapping or by the run input.
- `SchemaCompatibilityChecker` — a mapping's source type must be assignable to the target type.
- `CostEstimator` — tokens estimated from input size × pricing schedule × number of nodes.

`PipelineRun` records one `ModelExecution` per node, plus the resolved edge payloads, so the graph
view can show exactly what data crossed each edge.

---

## 5. Application layer

### 5.1 Ports (interfaces owned by Application, implemented by Infrastructure)

```csharp
public interface IModelInvoker            // one implementation per protocol, resolved by keyed DI
{
    ProtocolId Protocol { get; }
    Task<InvocationResult> InvokeAsync(InvocationRequest request, CancellationToken ct);
}

public interface IModelInvokerResolver { IModelInvoker For(ProtocolId protocol); }
public interface ISecretStore { ValueTask<string> GetAsync(SecretReference reference, CancellationToken ct); }
public interface ISchemaValidator { ValidationResult Validate(JsonSchemaDocument schema, JsonPayload payload); }
public interface IFormModelBuilder { FormModel Build(JsonSchemaDocument schema, UiHints hints); }
public interface IUiHintGenerator { Task<UiHints> SuggestAsync(JsonSchemaDocument schema, CancellationToken ct); }
public interface IPricingCatalog { Task<PricingSchedule?> FetchAsync(ProviderKind kind, RemoteModelId id, CancellationToken ct); }
public interface IRunNotifier { ValueTask PublishAsync(IDomainEvent evt, CancellationToken ct); }
public interface IClock { DateTimeOffset UtcNow { get; } }
// Repositories: IProviderRepository, IModelDefinitionRepository, IQuestionSetRepository, IRunRepository, IPipelineRepository
// IUnitOfWork
```

### 5.2 Use cases (vertical slices)

Organise by feature folder; each slice holds its command/query, validator and handler. Use a
small in-house dispatcher or plain injected handler interfaces. A mediator library is optional;
avoid one whose licence changed recently unless you accept its terms.

| Slice | Kind | Notes |
|---|---|---|
| `CreateProvider`, `UpdateProvider`, `TestProviderConnection` | Command | Connection test calls the model list endpoint where one exists (`GET /v1/models` on TypeSafe, `GET /api/v1/models` on OpenRouter). |
| `CreateModelFromTemplate` | Command | Copies a template into an editable `ModelDefinition`, records `TemplateOrigin`. |
| `UpdateModelDefinition` | Command | Validates both schemas against the JSON Schema 2020-12 meta-schema. |
| `SyncModelPricing` | Command | Pulls OpenRouter endpoint pricing; asks user to confirm the diff. |
| `SuggestUiHints` | Command | AI-assisted, see §7.3. Result is a draft until accepted. |
| `CreateQuestionSet`, `UpdateQuestionSet` | Command | Version bump on every save. |
| `EstimateRunCost` | Query | Shown in the run dialog before the user clicks Run. |
| `StartRun` | Command | Fans out executions; returns `RunId` immediately; progress via events. |
| `CancelRun` | Command | Propagates `CancellationToken`. |
| `GetRunComparison` | Query | Read model shaped for the comparison view. |
| `CreatePipeline`, `ConnectNodes`, `ValidatePipeline` | Command/Query | |
| `StartPipelineRun` | Command | Topological execution, see §6.3. |
| `ExportRun` | Query | JSON or CSV. |

### 5.3 Parallel execution (single runs)

```csharp
var tasks = run.Executions.Select(e => ExecuteOneAsync(e, ct));
await Task.WhenAll(tasks);   // each task catches its own exceptions and records failure on the aggregate
```

- Concurrency is limited **per provider** with `System.Threading.RateLimiting`
  (`ConcurrencyLimiter` plus `TokenBucketRateLimiter` from the provider's `RateLimitPolicy`), so three
  OpenRouter models share one budget and Jev direct gets its own.
- Every execution has its own `Stopwatch`. Measure total latency and time-to-first-byte separately.
- Results are written to the aggregate as they arrive and published through `IRunNotifier`, so the UI
  fills in cards one by one instead of waiting for the slowest model.
- Optional **repeat count** (1–N) per run gives p50/p95 latency and variance in outputs. One sample is
  not a performance measurement.

---

## 6. Infrastructure layer

### 6.1 Protocol adapters

| Adapter | Protocol id | Request building | Response normalising | Cost source |
|---|---|---|---|---|
| `SystemOneInvoker` | `systemone` | `{ model, state, questions }` from `RunInput` + `QuestionSet` | `answers` map → `DecisionOutput` | calculated from `usage.input_tokens` × rate |
| `WorkersAiInvoker` | `workers-ai` | Same body plus `model` selector and optional `images[]`; URL built from `account_id` path variable | Unwraps Cloudflare's `{ result, success, errors }` envelope, then reuses the System One normaliser | calculated |
| `OpenRouterDecisionsInvoker` | `openrouter-decisions` | System One body, OpenRouter auth headers (`HTTP-Referer`, `X-Title` optional) | System One normaliser | **`usage.cost`** from the response |
| `OpenAiChatInvoker` | `openai-chat` | Messages from `PromptTemplate`; adds `response_format: { type: "json_schema", … }` when the model has an output schema and the capability flag | `choices[0].message.content` parsed against output schema; reasoning tokens recorded separately | OpenRouter `usage.cost` when present, else calculated |
| `CustomHttpInvoker` | `custom` | User-defined `RequestTemplate` (Scriban or a minimal `{{json.pointer}}` syntax) | User-defined `ResponseMapping` (JSON Pointers for output, tokens, model id) | calculated |

All adapters share one `NormalizedOutput` shape so the comparison view does not care which protocol
produced a result:

```csharp
public abstract record NormalizedOutput;
public sealed record DecisionOutput(IReadOnlyDictionary<QuestionKey, Answer> Answers) : NormalizedOutput;
public sealed record ChatOutput(string Text, JsonPayload? Structured, string? Reasoning) : NormalizedOutput;
```

### 6.2 HTTP and resilience

- One typed/named `HttpClient` per provider via `IHttpClientFactory`.
- `Microsoft.Extensions.Http.Resilience` standard pipeline: retry with jitter, honour `Retry-After`
  on 429, circuit breaker, per-attempt and total timeouts. Record the attempt count on the execution
  so retries do not hide inside the latency figure without explanation.
- Register invokers as **keyed services** (`AddKeyedScoped<IModelInvoker, SystemOneInvoker>("systemone")`),
  resolved by `ProtocolId`. Adding a protocol is one class plus one registration line.

### 6.3 Pipeline executor

1. Validate the graph (domain service).
2. Topologically sort into **levels**. Nodes in the same level run in parallel (§5.3 rules apply).
3. Before a node runs, build its input from the run input plus all incoming edge mappings. Validate
   against the node model's input schema. Skip the node if an incoming `EdgeCondition` is false and
   mark it `Skipped` (shown greyed out in the graph).
4. Record each edge's resolved payload on the `PipelineRun`.
5. Stop downstream nodes of a failed node; let unrelated branches finish.

The confidence-routing pattern from the TypeSafe docs maps directly to conditional edges: run a cheap
decision model first; if `confidence < 0.7`, route the same state to a chat model or a larger decision
model.

### 6.4 Persistence

- EF Core 10 with **SQLite** by default (zero setup), PostgreSQL as an option behind the same
  `DbContext`.
- Aggregates map with owned types and value converters. Schemas, raw request/response bodies and
  normalised outputs are stored as JSON columns.
- Optimistic concurrency token on every aggregate root.
- Read models for the comparison and graph views are plain projection queries; no separate store needed.

### 6.5 Secrets

- API keys never live in a `ModelDefinition`, in exported templates, or in logs.
- `ISecretStore` implementation: ASP.NET Core Data Protection to encrypt keys at rest in the database,
  with environment variables (`TYPESAFE_API_KEY`, `OPENROUTER_API_KEY`, `CLOUDFLARE_API_TOKEN`) and
  user-secrets as overrides for development.
- A logging enricher redacts `Authorization` headers and known key patterns.

### 6.6 Templates

Templates are versioned JSON files embedded in Infrastructure (`Templates/*.json`) and loaded by an
`ITemplateCatalog`. Example:

```json
{
  "templateId": "typesafe.jev",
  "version": 1,
  "displayName": "Jev (TypeSafe)",
  "kind": "Decision",
  "protocol": "systemone",
  "providerKinds": ["TypeSafe", "OpenRouter"],
  "remoteIds": { "TypeSafe": "jev-latest", "OpenRouter": "typesafe/jev-1.13" },
  "capabilities": { "images": false, "maxQuestions": 64, "contextTokens": { "TypeSafe": 64000, "OpenRouter": 32000 }, "questionTypes": ["choice", "score", "noul"] },
  "pricing": { "currency": "USD", "inputPerMTok": 0.042, "outputPerMTok": 0 },
  "inputSchemaRef": "schemas/systemone.request.json",
  "outputSchemaRef": "schemas/systemone.response.json",
  "docs": "https://docs.typesafe.ai/introduction"
}
```

Template behaviour per reference model:

| Template | Protocol | Behaviour specific to this model |
|---|---|---|
| **Jev** | `systemone` (direct) or `openrouter-decisions` | Warn when the resolved `model` differs from the last run (alias moved). Offer "pin version". Enforce 32k state budget per question. Text-only input. |
| **Clef Flash** | `workers-ai` (direct) or `openrouter-decisions` | Image upload enabled (max 4, size limits validated client-side). Requires `account_id` path variable. Optional "upgrade to Clef 27B" sibling template. |
| **Decider V1 27B** | `openrouter-decisions` | Image upload enabled. Large state allowed (262k). |
| **Span-01** | `openrouter-decisions` | Input editor switches to a **conversation span** editor (role + message rows). Question editor offers **behaviors** (noul-style statements) only. Output view shows behavior probabilities as a ranked bar list. Ship a Span-01 Lite (free) variant for development. *Verify the exact request shape on first integration — the public listing describes behavior semantics but not the body.* |
| **DeepSeek V4.1 Flash** | `openai-chat` via OpenRouter | Exposes `temperature`, `max_tokens`, `reasoning_effort`, `response_format`. Provider routing preference (cheapest / fastest / pinned provider) because upstream prices range ~100× on input. Applies time-of-day pricing overrides when the DeepSeek upstream is used. |

---

## 7. Web layer and UX

### 7.1 Technology

- **Blazor Web App**, Interactive Server render mode (SignalR is already in place for live run
  progress; no separate API needed for v1).
- One component library for consistency. MudBlazor or Fluent UI Blazor both fit; pick one and do not mix.
- Graph canvas: **Z.Blazor.Diagrams** for the pipeline editor; a layered (Sugiyama) layout via a small
  JS interop call to **elkjs** or **dagre** for the hierarchical read-only view.
- Charts: one library (ApexCharts for Blazor or a thin Chart.js interop).

### 7.2 Screens

1. **Providers** — list + edit drawer. Fields: kind, base URL, key (masked), path variables, rate limits,
   "Test connection".
2. **Models** — catalogue grid grouped by Chat / Decision. "New from template" opens a template picker
   with docs link, price and capability chips. Model editor tabs: *General*, *Schemas* (two JSON editors
   with live validation), *Form preview* (renders the generated dialog), *Pricing*, *Advanced* (custom
   request/response mapping, only shown for `custom` protocol).
3. **Question sets** — card editor per question with a type switch (Choice / Score / Noul), drag to
   reorder, "test against sample state" button.
4. **Run dialog** — see §7.4.
5. **Run results / comparison** — see §7.5.
6. **Pipelines** — canvas editor plus run history; each run opens the hierarchical view (§7.6).
7. **History** — searchable list of runs, filter by model, cost, date; re-run with the same input.

### 7.3 Schema-driven forms (the "dialog")

`IFormModelBuilder` turns a JSON Schema plus optional `x-ui` hints into a `FormModel` tree, and one
generic `SchemaForm` component renders it. Default mapping:

| Schema | Widget |
|---|---|
| `string` | text field; `textarea` when `maxLength > 200` or `x-ui.widget = textarea` |
| `string` + `enum` (≤ 5) | segmented buttons; > 5 → select |
| `string` + `format: uri`/`email`/`date` | matching input type |
| `number`/`integer` with `minimum` + `maximum` | slider plus numeric box |
| `boolean` | switch |
| `object` | fieldset; collapsible when not required |
| `array` of scalars | chip input |
| `array` of objects | repeatable card list |
| `oneOf`/`anyOf` | tab per variant |
| unknown/complex | JSON editor fallback |

Rules that keep the dialog concise:

- Required fields first, optional fields behind "More options".
- Group by `x-ui.group`; order by `x-ui.order`, then schema order.
- Labels from `title`, help text from `description` (as a tooltip, not inline paragraphs).
- Validate on blur and on submit with the same `ISchemaValidator` the backend uses.

### 7.4 Run dialog flow

A three-step dialog, each step one screen, no scrolling walls:

1. **Input** — State editor that adapts to the selected models: plain text, JSON (with schema form if
   the user picked an input schema), conversation span (Span-01), image drop zone (Clef, Decider).
   Saved fixtures dropdown.
2. **Ask** — For decision models: pick a question set or quick-add questions inline. For chat models:
   pick a prompt template; placeholders become form fields. If both kinds are selected, offer
   **"Ask chat models the same questions"** (see suggestion S1).
3. **Models & run** — Checklist of models with price chips, the cost estimate, optional repeat count
   and budget cap. Run button.

### 7.5 Comparison view

- **Header strip:** per model — status, latency (p50 if repeated), tokens in/out, cost, resolved model id.
- **Answer matrix (decision runs):** rows = questions, columns = models. Each cell shows the answer
  and a small probability bar. Cells where models disagree are highlighted. Confidence shown as
  opacity.
- **Charts:** latency bar chart, cost bar chart, cost-vs-latency scatter, "projected cost per 1,000
  runs".
- **Raw tab:** request and response JSON per model, diffable side by side.
- **Chat outputs:** rendered markdown with collapsible reasoning; structured outputs shown as a tree.

### 7.6 Hierarchical pipeline view

- Layered top-to-bottom layout. Root = run input. Each node card shows model, status, latency, cost.
- Edges labelled with the mapped fields; click an edge to see the exact payload that crossed it.
- Conditional edges drawn dashed; skipped branches greyed out.
- Totals in a footer: wall-clock time (critical path), summed cost, number of calls.
- Live updates while running: nodes change colour as they start, finish or fail.

---

## 8. AI-assisted schema and UI design

This answers the "maybe the testing workflow itself should integrate AI models" idea. Use AI at
**design time**, not at run time:

1. **Schema drafting.** Paste a sample request/response, a cURL command, or a docs URL. A chat model
   (default: DeepSeek V4.1 Flash, the cheapest configured chat model) drafts `inputSchema` and
   `outputSchema`. The draft is validated against the JSON Schema meta-schema and shown as a diff
   for the user to accept.
2. **UI hints.** The same assistant proposes an `x-ui` overlay: field labels, help text, groups,
   order, widget choices. Output is constrained by a fixed JSON schema for `UiHints`, so the model
   cannot invent widgets the renderer does not know.
3. **Question drafting.** Describe a decision in plain language ("route support tickets and flag
   churn risk"); the assistant proposes a decomposed question set (atomic Choice/Score/Noul
   questions), following TypeSafe's "atomic questions, composed in code" guidance.
4. **Sample data.** Generate realistic fixtures that satisfy an input schema, for quick testing.

Every AI suggestion is a **draft**: stored, previewed in the form preview tab, and applied only on
user confirmation. The deterministic renderer always works without AI.

---

## 9. Cross-cutting concerns

- **Observability:** OpenTelemetry traces per run and per execution (`ActivitySource("ModelLab.Runs")`),
  metrics for latency, tokens and cost by model. Exportable to the .NET Aspire dashboard in dev.
- **Logging:** structured, with redaction (§6.5). Raw bodies go to the database, not to logs.
- **Validation:** domain invariants in aggregates; input validation in Application (FluentValidation
  or hand-written validators); schema validation via `JsonSchema.Net`.
- **Time:** `TimeProvider` (built into .NET) instead of a custom `IClock` if you prefer the BCL type.
- **Configuration:** `IOptions<T>` with `ValidateOnStart()` for every options class.
- **Error model:** `Result<T>` in Application for expected failures (validation, provider 4xx);
  exceptions only for bugs and infrastructure faults.

---

## 10. Solution layout

```
ModelLab.sln
├─ src/
│  ├─ ModelLab.Domain/
│  │  ├─ Abstractions/           AggregateRoot, Entity, ValueObject, IDomainEvent
│  │  ├─ Catalog/                Provider, ModelDefinition, PricingSchedule, ...
│  │  ├─ Authoring/              QuestionSet, Question types, PromptTemplate, InputFixture
│  │  ├─ Experimentation/        Run, ModelExecution, NormalizedOutput, events
│  │  └─ Pipelines/              Pipeline, PipelineRun, PipelineGraphValidator
│  ├─ ModelLab.Application/
│  │  ├─ Abstractions/           ports (IModelInvoker, ISecretStore, repositories, ...)
│  │  └─ Features/               one folder per slice (§5.2)
│  ├─ ModelLab.Infrastructure/
│  │  ├─ Persistence/            DbContext, configurations, migrations, repositories
│  │  ├─ Protocols/              SystemOne/, WorkersAi/, OpenRouterDecisions/, OpenAiChat/, Custom/
│  │  ├─ Schemas/                JsonSchema validation, FormModelBuilder
│  │  ├─ Templates/              *.json + schemas/*.json (embedded)
│  │  ├─ Secrets/                DataProtectionSecretStore
│  │  ├─ Pricing/                OpenRouterPricingCatalog
│  │  └─ DependencyInjection.cs  AddInfrastructure(this IServiceCollection, IConfiguration)
│  ├─ ModelLab.Web/
│  │  ├─ Components/             SchemaForm, AnswerMatrix, PipelineCanvas, HierarchyView, ...
│  │  ├─ Pages/                  Providers, Models, QuestionSets, Runs, Pipelines, History
│  │  └─ Program.cs              composition root
│  └─ ModelLab.AppHost/          optional .NET Aspire host (dashboard, Postgres container)
└─ tests/
   ├─ ModelLab.Domain.Tests/          pure unit tests, no mocks needed
   ├─ ModelLab.Application.Tests/     handlers with fake ports
   ├─ ModelLab.Infrastructure.Tests/  WireMock.Net contract tests per protocol, EF with SQLite in-memory
   ├─ ModelLab.Architecture.Tests/    dependency rules (Domain references nothing, Web never touches Domain internals, ...)
   └─ ModelLab.Web.Tests/             bUnit component tests (SchemaForm mappings especially)
```

Composition root sketch:

```csharp
builder.Services
    .AddDomainServices()
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
```

---

## 11. Testing strategy

| Level | What | Tooling |
|---|---|---|
| Domain | Invariants: question key rules, 1–64 questions, pipeline acyclicity, pricing with time windows, run state transitions | xUnit, no mocks |
| Application | Handlers with in-memory fakes for ports; parallel fan-out records every result even when one fails | xUnit, fakes |
| Contract | Each protocol adapter against recorded fixtures (the example payloads in §1.1 are a starting set); 429 + `Retry-After` handling; Cloudflare envelope unwrapping | WireMock.Net |
| Schema → form | Every row of the §7.3 table produces the right widget | bUnit |
| Architecture | Layer dependency rules | ArchUnitNET or NetArchTest |
| Live smoke (opt-in) | One call per template using free tiers (`span-01-lite:free`, `mercury-decide:free`) so CI costs nothing | xUnit trait `Category=Live`, skipped without keys |

Write domain and contract tests first; they are cheap and they pin the protocol behaviour.

---

## 12. Delivery roadmap

| Phase | Scope | Exit criteria |
|---|---|---|
| **0. Skeleton** (≈1 week) | Solution, layering, architecture tests, EF + SQLite, Blazor shell, DI, OpenTelemetry | Architecture tests green; app boots; empty pages navigate |
| **1. Catalog** | Providers, secrets, model definitions, template catalog with all five templates, connection test | Can create Jev, Clef Flash, Decider, Span-01 and DeepSeek from templates and pass "Test connection" |
| **2. Single run** | Question sets, prompt templates, SystemOne + WorkersAi + OpenRouterDecisions + OpenAiChat invokers, run dialog, single-model results | One run per template succeeds end to end with cost and latency recorded |
| **3. Parallel compare** | Fan-out, per-provider limiters, live progress, comparison view, cost estimate, budget cap, repeat count | Five models run against one input; answer matrix and charts render |
| **4. Pipelines** | Pipeline aggregate, editor canvas, mappings, conditional edges, executor, hierarchical view | A two-level confidence-routing pipeline runs and shows edge payloads |
| **5. AI assist** | Schema drafting, UI hints, question drafting, fixture generation | Drafts applied only after user confirmation; renderer still works with assist disabled |
| **6. Hardening** | Pricing sync, export, history search, Custom protocol, PostgreSQL option | Docs, sample data, release build |

---

## 13. Suggested changes to the original idea

These are recommendations that change or sharpen the brief. Each has a reason and a concrete action.

### S1. Make cross-kind comparison fair: let chat models answer decision questions

**Why.** The core purpose is comparing decision-making. A chat model and a decision model only
compare cleanly if they answer the *same* question in the *same* shape.
**Change.** Add a `DecisionQuestionsOverChat` adapter: it turns a question set into a structured-outputs
JSON schema and a short system prompt, calls the chat model, and maps the reply into the same
`DecisionOutput`. Where the provider returns `logprobs`, derive approximate probabilities; otherwise
mark probabilities as "not available". DeepSeek V4.1 Flash supports `structured_outputs` and
`logprobs` on several upstream providers.

### S2. Configure protocols once, not every model's JSON by hand

**Why.** The brief asks for each model to be configured with its own endpoint and input/output
schema. Research shows four of five reference models share one protocol. Hand-written schemas per
model would be duplicated and drift.
**Change.** Introduce **Protocol** as a first-class concept (§3.3). Built-in protocols ship their
schemas. Per-model configuration then only covers remote id, endpoint override, capabilities and
pricing. Keep the fully manual request/response mapping as the `custom` protocol for anything new.

### S3. Treat question sets as the user-defined input for decision models

**Why.** For decision models the request schema is fixed by the protocol; what the user really designs
is the set of questions. Modelling that explicitly makes the dialog much simpler.
**Change.** `QuestionSet` aggregate, versioned, reusable across runs and pipelines (§4.2). The run
dialog asks for *state* plus *question set*, not for a raw JSON body.

### S4. Use AI for design-time assistance, not run-time UI generation

**Why.** Generating UI with an LLM on every run is slow, costs money, and is non-deterministic, which
undermines a tool whose job is to measure models.
**Change.** Deterministic schema-to-form renderer (§7.3) plus an optional AI assistant that writes
`x-ui` hints and schema drafts once, behind user approval (§8).

### S5. Add datasets and ground truth, not only one-off prompts

**Why.** "Which model decides better" needs labelled examples. A single dialog run shows behaviour but
not accuracy.
**Change.** Add an **Evaluation** context: a dataset is a list of inputs with expected answers. Batch
runs compute accuracy per question, agreement between models, and calibration (Brier score, expected
calibration error) — decision models return probabilities, so calibration is the metric they are
built for. Plan it as Phase 7.

### S6. Measure performance properly

**Why.** One request is noise. Cold starts, retries and network jitter dominate single samples.
**Change.** Repeat count per run, warm-up call option, p50/p95, separate time-to-first-byte from total,
record retry attempts, and show OpenRouter's published latency percentiles next to measured ones for
context.

### S7. Prefer provider-reported cost; snapshot pricing on every run

**Why.** Prices vary by upstream provider (~100× for DeepSeek V4.1 Flash input), by time of day
(DeepSeek native) and over time. A cost computed later from today's price list would be wrong.
**Change.** Use `usage.cost` from OpenRouter when present; otherwise compute from a pricing snapshot
stored on the execution. Support time-window overrides in `PricingSchedule`. Add a pricing sync from
OpenRouter's public endpoints API.

### S8. Pin model versions and surface alias drift

**Why.** `jev-latest` moves when TypeSafe ships a release; results can change with no change on your
side.
**Change.** Store the resolved model id from each response; warn in the comparison view when two runs
of "the same model" resolved to different versions; one-click "pin this version".

### S9. Pipelines as DAGs with conditional edges, shown hierarchically

**Why.** The brief says "route output from one model to another" and "visualised hierarchically". A
strict tree cannot express fan-in (two models feeding one judge); a free graph without a layout looks
chaotic.
**Change.** Model pipelines as DAGs, lay them out in layers (Sugiyama) so they *read* as a hierarchy,
and add conditional edges for confidence-based routing (§4.4, §6.3).

### S10. Budget guard before every run

**Why.** Parallel runs and pipelines multiply cost silently.
**Change.** Show an estimate before running; let the user set a per-run cap; abort remaining
executions when the cap is reached.

### S11. Add more templates, including free ones

**Why.** Free tiers make development and CI cost nothing, and more decision models make comparisons
more interesting.
**Change.** Ship `respan/span-01-lite:free`, `inception/mercury-decide:free`, `liquid/d1`,
`upstage/solar-decide`, `cloudflare/clef` (27B) and `jaredpalmer/kev-4b` as additional templates.

### S12. Support image input where models allow it

**Why.** Clef Flash and Decider V1 27B accept images; ignoring that hides a real capability difference.
**Change.** Capability flag `images` on the model; the run dialog shows an image drop zone only when a
selected model supports it, and warns which selected models will ignore the images.

### S13. Keep DDD strict where it pays, light where it does not

**Why.** "Strictly DDD" can turn a small tool into ceremony: a repository, a specification and a
mapper for every lookup table.
**Change.** Full aggregates and invariants for `ModelDefinition`, `QuestionSet`, `Run`, `Pipeline`.
Read-only views (history lists, comparison tables) use plain projection queries, not repositories.
Enforce boundaries with architecture tests instead of extra layers.

### S14. Store reproducible run records

**Why.** A comparison is only trustworthy if you can see exactly what was sent.
**Change.** Each execution stores the raw request, raw response, template version, question set
version and model snapshot. "Re-run" uses the snapshot, not the current definitions, unless the user
chooses otherwise.

### S15. Secrets handling as a first-class feature

**Why.** The app stores several paid API keys. Template export and logs are the usual leak paths.
**Change.** Secret references only (§6.5), encrypted at rest, redacted logs, exports that strip
credentials, and a startup warning if the app binds to a non-local address without authentication.

### S16. Export and share results

**Why.** Comparisons are often shown to someone else.
**Change.** Export runs as JSON/CSV; a printable comparison report page.

---

## 14. Risks and open questions

| # | Item | Mitigation / decision needed |
|---|---|---|
| R1 | Span-01 request shape is not documented on its public listing. | Spike in Phase 2; adjust its template. |
| R2 | OpenRouter Decisions router is an **alpha** API. | Prefer the stable `/systemone` path where possible; isolate behind the adapter. |
| R3 | Decision-model rate limits are "adjusting dynamically" (TypeSafe docs). | Per-provider limiters; honour `Retry-After`; show 429s distinctly in results. |
| R4 | Context limits differ between direct and OpenRouter access (Jev 64k vs 32k). | Capabilities are per provider+model, not per model. |
| R5 | Logprob-derived probabilities from chat models are approximations. | Label them clearly; never mix them silently into calibration metrics. |
| Q1 | Single user local tool, or hosted for a team? | Decides whether authentication and multi-tenancy enter v1. |
| Q2 | SQLite only, or PostgreSQL from day one? | SQLite recommended for v1. |
| Q3 | MudBlazor or Fluent UI Blazor? | Either; choose once. |
| Q4 | Should the Evaluation context (S5) be in v1? | Recommended as Phase 7, after pipelines. |

---

## 15. Sources consulted (2026-10-05)

- TypeSafe docs: introduction, quick start, primitives (advanced structure), models reference — `docs.typesafe.ai`
- Cloudflare Workers AI model page for `@cf/cloudflare/clef-flash` — `developers.cloudflare.com`
- OpenRouter public model and endpoint APIs (`/api/v1/models?output_modalities=decisions`,
  `/api/v1/models/{id}/endpoints`) for Decider V1 27B, Span-01 and DeepSeek V4.1 Flash
- OpenRouter API reference: System One and Decisions endpoints (`openrouter.ai/docs`)
