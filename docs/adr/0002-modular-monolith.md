# ADR 0002: Start as a modular monolith

- Status: Accepted
- Date: 2026-10-28

## Context

Premature service boundaries increase deployment, consistency, tracing, and local-development costs. Most orchestration operations share one consistency and operational boundary.

## Decision

Deploy the API, application workflows, catalogs, and persistence adapters as a modular monolith with enforced project dependencies. Split a Python document worker only when file conversion requires a distinct runtime and resource boundary.

## Consequences

The system is easy to run and change while retaining explicit seams. Modules can be extracted later from measured scaling, isolation, or ownership needs.
