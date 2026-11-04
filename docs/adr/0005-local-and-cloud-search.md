# ADR 0005: Local and cloud search adapters

- Status: Accepted
- Date: 2026-10-28

## Context

Contributors need a complete offline demo, while production users may need managed semantic and hybrid search.

## Decision

Keep retrieval behind tenant-aware search ports. Use Qdrant plus project-owned keyword fusion locally and Azure AI Search in the cloud profile. Both adapters must implement the same access filtering, result normalization, citation identity, and no-answer semantics.

## Consequences

Local development requires no cloud account. Search parity must be tested because ranking details differ between engines.
