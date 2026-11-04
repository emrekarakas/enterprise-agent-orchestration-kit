# ADR 0003: Capability-based provider abstraction

- Status: Accepted
- Date: 2026-10-28

## Context

Chat, structured output, embeddings, reranking, moderation, and token counting have different availability and cost characteristics. A single vendor client would couple the domain to one SDK.

## Decision

Define project-owned capability ports in the application layer. Provider adapters translate at the boundary. Tests default to deterministic local implementations; cloud adapters must pass shared contract tests.

## Consequences

Capabilities can be replaced independently and provider types cannot leak inward. Some lowest-common-denominator behavior is avoided by permitting provider-specific configuration only inside adapters.
