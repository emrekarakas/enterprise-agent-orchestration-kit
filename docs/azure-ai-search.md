# Azure AI Search adapter

The cloud retrieval adapter uses the stable Azure AI Search `2026-04-01` data-plane REST API. It sends keyword and vectorizable-text queries together, enables semantic ranking, requests 50 vector candidates for the semantic reranker, and applies tenant/access-tag filters as a prefilter.

This is deliberately behind `ISearchIndex`. Azure request/response shapes and Azure Identity types do not enter the application or domain projects.

## Configuration

```text
Search__Provider=AzureAiSearch
AzureAiSearch__Endpoint=https://YOUR-SERVICE.search.windows.net
AzureAiSearch__IndexName=idx-eaokit-chunks-v1
AzureAiSearch__SemanticConfiguration=sem-eaokit-grounding-v1
AzureAiSearch__VectorField=contentVector
AzureAiSearch__MinimumGroundingScore=0.5
AzureAiSearch__CredentialMode=ManagedIdentity
AzureAiSearch__ManagedIdentityClientId=
```

`ManagedIdentity` uses the system-assigned identity when the client ID is empty. `DefaultAzureCredential` is intended for local Azure CLI/IDE authentication. A query API key can be supplied through `AzureAiSearch__ApiKey`, but managed identity is the recommended deployment mode and no key belongs in source control.

`MinimumGroundingScore` is applied after semantic reranker scores are normalized from Azure's 0–4 range to 0–1. The default rejected weak false-positive matches in the synthetic live smoke test, but it must be tuned with a representative, tenant-safe evaluation set before production use. Readiness checks the index document count through the data plane, so both a query key and `Search Index Data Reader` identity can satisfy the probe.

For a local key-based smoke test, keep the query key outside committed settings:

```bash
dotnet user-secrets set "Search:Provider" "AzureAiSearch" --project src/Orchestrator.Api
dotnet user-secrets set "AzureAiSearch:Endpoint" "https://YOUR-SERVICE.search.windows.net" --project src/Orchestrator.Api
dotnet user-secrets set "AzureAiSearch:IndexName" "idx-eaokit-chunks-v1" --project src/Orchestrator.Api
dotnet user-secrets set "AzureAiSearch:SemanticConfiguration" "sem-eaokit-grounding-v1" --project src/Orchestrator.Api
dotnet user-secrets set "AzureAiSearch:ApiKey" "YOUR-QUERY-KEY" --project src/Orchestrator.Api
```

The runtime identity needs `Search Index Data Reader`. The search service identity needs access to the configured embedding deployment when the index uses an Azure OpenAI vectorizer.

## Qdrant and Azure AI Search concepts

| Platform capability | Qdrant/local implementation | Azure AI Search implementation |
|---|---|---|
| Search storage | Collection | Index |
| Document schema | Vector size plus payload conventions | Typed index fields and vector profiles |
| Ingestion orchestration | Python document worker | Indexer |
| Chunking/enrichment | Worker extractors and chunker | Skillset and index projections |
| Incremental change detection | Source URI, content hash, explicit deletion | Indexer change/deletion detection |
| Vector retrieval | Qdrant similarity search | Vector query or integrated vectorizer |
| Keyword + vector fusion | Application-side pipeline (planned) | Native hybrid query with RRF |
| Semantic reranking | Provider-neutral reranker port | Native semantic ranker |
| Tenant/document ACL | Payload filter | OData prefilter on filterable fields |

Qdrant does not have first-class Azure-style indexers or skillsets. In the local profile, the Python worker owns those responsibilities. The two deployment profiles share application contracts and metadata invariants, not provider-specific control-plane objects.

## Required index contract

The configured index must expose these retrievable fields:

- `chunkId` (key)
- `documentId`
- `title`
- `content`
- `sourceUri`
- `locator`
- `tenantId` (filterable)
- `accessTags` (filterable collection)
- `contentVector` (vector field with an assigned text vectorizer)

The semantic configuration must use `title` as the title field and `content` as prioritized content. Vectorizer dimensions must match the index field and embedding deployment.

## Provisioning templates

The `deploy/azure/search` directory contains stable REST `2026-04-01` JSON bodies for the index, Blob data source, chunking/embedding skillset, and scheduled indexer. They use explicit development credentials because the Free search tier doesn't support the managed-identity profile required for production. The production deployment will replace these credentials with identity and least-privilege RBAC.

## Current boundary

The query adapter, Blob upload/source adapter, managed-identity deployment profile, deterministic HTTP contract tests, and structurally validated JSON templates are implemented. The synthetic development index has been smoke-tested with hybrid semantic retrieval and query-key readiness. This is connectivity and behavior validation, not a quality or performance benchmark. Azure AI Search deletion policies don't apply to the one-to-many projection used here, so explicit projected-chunk deletion remains an application responsibility.
