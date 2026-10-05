# AI-Sandbox

An interactive testing playground for testing both decision- and chat models.

Configure providers and models, run them side by side against the same input, and compare cost,
latency and decisions. See [PLAN.md](PLAN.md) for the design and [GLOSSARY.md](GLOSSARY.md) for the
domain language. Work is tracked in the repository's issues, starting from the spec in issue #1.

## Requirements

- .NET SDK 10.0.400 (pinned in `global.json`)

## Run

```bash
dotnet run --project src/AISandbox.Web --launch-profile http
```

Then open http://localhost:5253. The SQLite database (`aisandbox.db`) is created and migrated on start.

API keys entered in the UI are encrypted with ASP.NET Core Data Protection. These environment
variables override stored keys when set: `TYPESAFE_API_KEY`, `OPENROUTER_API_KEY`,
`CLOUDFLARE_API_TOKEN`.

Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export traces, metrics and logs (for example to the Aspire dashboard).

## Test

```bash
dotnet test AISandbox.slnx
```

## Layout

| Project | Role |
|---|---|
| `AISandbox.Domain` | Aggregates, value objects, domain events. Depends on nothing. |
| `AISandbox.Application` | Use cases (commands and queries) and the ports they need. |
| `AISandbox.Infrastructure` | EF Core persistence, secret store, log redaction, provider adapters. |
| `AISandbox.Web` | Blazor Web App (Interactive Server) and composition root. |
| `tests/AISandbox.Application.Tests` | Use-case seam tests over real infrastructure and in-memory SQLite. |
| `tests/AISandbox.Architecture.Tests` | Layer dependency rules. |

## Migrations

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/AISandbox.Infrastructure --output-dir Persistence/Migrations
```
