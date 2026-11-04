# Azure AI Search data-plane templates

These templates target the stable Azure AI Search REST API `2026-04-01` and provision isolated data-plane objects inside an existing search service. They don't create or modify the search service itself.

## Required replacements

Replace every `REPLACE_WITH_...` value before submitting a template:

- Azure OpenAI resource endpoint
- `text-embedding-3-small` deployment name
- Azure OpenAI API key
- Azure Storage connection string

Never commit replaced files. Keep the committed templates unchanged and submit an untracked local copy. The Search admin API key belongs in the HTTP `api-key` header, not in these JSON bodies.

Free Azure AI Search doesn't support the managed-identity configuration required by the production profile, so these development templates use explicit Azure OpenAI and Storage credentials. Production deployment templates will replace them with identities and least-privilege RBAC.

## Blob container and metadata contract

Create the `eaokit-documents-dev` container before the data source. Each uploaded blob must contain these lower-case custom metadata entries:

| Metadata key | Example | Required |
|---|---|---|
| `Tenantid` | `demo-tenant` | yes |
| `Accesstag` | `employee` | yes |
| `Contenthash` | lower-case SHA-256 | recommended |
| `Ingestionversion` | `1` | recommended |

The initial Free-tier demo contract supports one access tag per blob. The application still treats `accessTags` as a collection, and the production Blob adapter must normalize multiple tags before indexing.

Blob metadata names are case-insensitive in Azure Storage, but enrichment-tree annotation paths are case-sensitive. The skillset paths therefore match the casing returned by Blob Storage. Verify the effective names with a Blob `HEAD` request when changing the upload adapter.

`ordinalPositions` is emitted as a sibling array to `pages` by the Text Split skill. It isn't projected into the `Edm.Int32` `chunkOrdinal` field because a direct sibling-array mapping can produce a string value or an ambiguous page-to-ordinal association. The generated projected key already contains the page ordinal (`..._pages_0`, `..._pages_1`). A future enrichment skill can explicitly materialize a typed per-chunk ordinal if the application needs numeric sorting.

## Creation order

Use the Search service admin key and submit the files in this order:

1. `01-index.json`
2. `02-data-source.json`
3. `03-skillset.json`
4. `04-indexer.json`

## Troubleshooting index projections

If Azure reports that the target index key field must have the `keyword` analyzer,
recreate the empty target index from `01-index.json`. Index projections require the
child index key (`chunkId`) to be searchable and use `analyzer: keyword`. Search and
analyzer settings aren't intended to be changed after the field is created.

REST endpoints:

```text
PUT https://SEARCH_SERVICE.search.windows.net/indexes('idx-eaokit-chunks-v1')?api-version=2026-04-01
PUT https://SEARCH_SERVICE.search.windows.net/datasources('ds-eaokit-documents-blob-v1')?api-version=2026-04-01
PUT https://SEARCH_SERVICE.search.windows.net/skillsets('ss-eaokit-document-enrichment-v1')?api-version=2026-04-01
PUT https://SEARCH_SERVICE.search.windows.net/indexers('ixr-eaokit-documents-v1')?api-version=2026-04-01
```

After creation, inspect the indexer execution history before querying. The indexer performs built-in incremental change detection using Blob timestamps.

Azure AI Search deletion detection policies don't apply to one-to-many index projections. The cloud ingestion adapter must therefore delete projected child chunks explicitly before a source blob is physically removed. This limitation is intentionally not hidden by the templates.
