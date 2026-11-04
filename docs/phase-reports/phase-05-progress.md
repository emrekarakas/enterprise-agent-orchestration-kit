# Phase 5 — Azure adapters

Completed: 2026-11-04

## Delivered in this increment

- Azure AI Search runtime query adapter:
  - stable REST API `2026-04-01`
  - keyword plus integrated-vectorization hybrid query
  - semantic reranking with 50 vector candidates
  - tenant and access-tag prefiltering plus response-side defense in depth
  - API-key, developer credential, and managed-identity authentication
- Azure OpenAI adapter using stable `Azure.AI.OpenAI` 2.1:
  - provider-neutral chat completion
  - strict JSON-schema structured completion
  - deterministic typed fallback for invalid model JSON
  - embeddings and service-reported token usage
  - API-key, developer credential, and managed-identity authentication
  - safe domain error translation and content-free OpenTelemetry attributes
- Config-driven AI and search provider selection; zero-cloud fake/local defaults remain unchanged.
- Concrete Azure Blob listing/stream adapter and Azure Service Bus run-event publisher. No credential value was added.
- Local user-secrets support and Azure OpenAI operator documentation.
- Stable REST `2026-04-01` templates for a chunk index, Blob data source, split/embedding skillset, and scheduled indexer.
- A fail-closed tenant/access metadata contract and an explicit warning that projected child deletion requires application coordination.
- OpenAI-compatible HTTP adapter for non-Azure and local gateways.
- Bicep foundation for identity, Blob, Search, Service Bus, optional Azure OpenAI, and least-privilege role assignments.

## Verification

- API build succeeded with zero warnings and zero errors.
- Fast .NET suites, a real PostgreSQL Testcontainers test, and a Docker Compose full-stack E2E journey passed locally.
- Formatting verification passed outside the restricted sandbox used by the development agent.
- NuGet reported no known vulnerable direct or transitive packages from the configured official source.
- All committed Azure Search template files passed strict JSON parsing.
- Default startup and all existing tests remain independent of Azure resources and paid models.

Azure AI Search was also validated manually against a synthetic three-document index with tenant filtering. This is an operator validation, not a portable CI benchmark, and no latency, quality, or cost claim is made.

## Deliberately deferred operational work

- Private endpoints, Key Vault references, and an approved application compute topology are deployment-specific.
- Live-cloud CI is opt-in because it requires credentials, quota, cost ownership, and reliable cleanup.
- Service Bus is used for durable fan-out; multi-instance SSE replay still needs an event-store design.

## Open risks

- Stable Azure OpenAI SDK 2.1 is intentionally isolated because the provider API surface continues to evolve.
- Structured output requires a compatible deployed model; an incompatible deployment will fail at the provider boundary.
- Local API-key mode is supported for development, but the deployment design must prefer managed identity.
- Azure AI Search index field, vectorizer, and semantic configuration names must remain aligned with the versioned provisioning templates under `deploy/azure/search`.
- Live-cloud behavior, RBAC propagation, quotas, regional model availability, and private networking are not covered by local tests.
