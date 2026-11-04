# ADR 0001: Microsoft Agent Framework behind an adapter

- Status: Accepted
- Date: 2026-10-28

## Context

The platform needs typed multi-agent workflows, streaming, sessions, human-in-the-loop primitives, and OpenTelemetry integration. Framework APIs may evolve independently of the domain.

## Decision

Use Microsoft Agent Framework for the orchestration runtime and isolate it in `Orchestrator.AgentFramework`. Application code depends on the project-owned `IAgentRuntime`; no MAF type is part of a domain, application, or public HTTP contract. Structured routing always has a bounded deterministic fallback.

## Consequences

The platform gains a supported workflow model while framework upgrades remain localized. The adapter adds translation code and must have integration tests against the pinned MAF version.
