# ADR 0006: Human approval for state-changing tools

- Status: Accepted
- Date: 2026-10-28

## Context

Model output is probabilistic and can be influenced by untrusted input. Side effects such as ticket creation must not execute solely on a model decision.

## Decision

All state-changing tools default to `PendingApproval`. Persist an immutable proposal and parameter digest, issue a short-lived single-use approval token, reauthorize and revalidate at approval time, then execute with an idempotency key. Rejection and expiry are terminal audited outcomes.

## Consequences

Risky actions have a deterministic control point and replay protection. Users accept an additional interaction and approval state must be durable.
