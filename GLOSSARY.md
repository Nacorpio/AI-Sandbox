# Glossary

The ubiquitous language of AI Sandbox. Use these terms, with these meanings, in code, UI and issues.

| Term | Meaning |
|---|---|
| **Provider** | An organisation's endpoint family plus the credentials used to call it (TypeSafe, Cloudflare Workers AI, OpenRouter, or a custom endpoint). |
| **Protocol** | A wire contract a model speaks: System One, OpenAI-compatible chat completions, the Workers AI envelope, OpenRouter Decisions, or Custom. |
| **System One** | The protocol shared by decision models: a *state* plus named, typed *questions* in; typed, probabilistic *answers* out. Use the name only for the protocol, never for the models. |
| **Model definition** | One callable model: provider + protocol + remote model id + schemas + capabilities + pricing. |
| **Template** | A versioned, read-only starting point for a model definition (Jev, Clef Flash, Decider V1 27B, Span-01, DeepSeek V4.1 Flash, …). |
| **Chat model** | A model that generates text. |
| **Decision model** | A model that evaluates a state against questions and returns typed answers with probabilities. The UI term for System One models. |
| **State** | The content a decision model evaluates: text, JSON, a conversation span, or images where supported. |
| **Question** | One typed decision request: **choice** (pick one option), **score** (place on an ordinal rubric) or **noul** (is this statement true, 0–1). |
| **Question set** | A named, versioned group of questions, reused across runs and pipelines. |
| **Behavior** | A plain-language statement scored by a behavior-scoring decision model such as Span-01. |
| **Prompt template** | System prompt, user prompt with placeholders, and optional output schema for chat models. |
| **Run** | One input sent to one or more model definitions at the same time. |
| **Execution** | One model's result inside a run: output, tokens, cost, latency, resolved model version. |
| **Comparison** | The side-by-side view of a run's executions. |
| **Pipeline** | A directed acyclic graph of model nodes joined by mapped, optionally conditional edges. |
| **Secret reference** | A pointer to a credential in the secret store. Aggregates hold references, never keys. |
