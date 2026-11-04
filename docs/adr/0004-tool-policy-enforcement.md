# ADR 0004: Enforce policy at the tool boundary

- Status: Accepted
- Date: 2026-10-28

## Context

Model-generated tool names and arguments are untrusted. Prompt instructions alone cannot authorize access or side effects.

## Decision

Resolve tools only through a server-owned registry. The dispatcher checks request policy, caller permissions, mutation risk, argument schema, timeout, result validation, and idempotency before returning a result. Unknown tools fail closed.

## Consequences

Authorization remains deterministic and auditable even when a model is manipulated. Each tool requires explicit metadata and tests.
