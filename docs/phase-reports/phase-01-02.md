# Phase 1 and 2 report

## Delivered

The solution foundation and the first working vertical slice are complete. A request is authenticated with development identity context, normalized, safety-checked, redacted, context-resolved, classified, policy-evaluated, routed by a typed MAF workflow, executed by the Knowledge Agent, grounded in tenant-filtered evidence, citation-validated, persisted, audited, and exposed as SSE events.

## Acceptance evidence

- A conversation can be created and listed.
- A message requires an idempotency key and produces a completed run.
- The MAF supervisor chooses only registered agents with a deterministic fallback.
- The Knowledge Agent can invoke only an allowed and permission-checked search tool.
- Search excludes data from another tenant and applies access tags.
- The deterministic provider produces an answer without a cloud account.
- Citations that do not match retrieved chunks fail closed.
- Repeating an idempotency key replays the stored run.
- OpenTelemetry spans and structured audit entries are emitted without full prompt/document telemetry.

## Open risks carried forward

- Real OIDC/JWT and durable approvals are phase 4 work.
- The database bootstrap uses `EnsureCreated`; versioned migrations are required before release.
- Search is deterministic in-memory data until the phase-3 Qdrant pipeline.
- Provider resilience policies need contract tests with failure injection.
- Compose and clean-machine instructions need verification in CI and on a Docker host.
