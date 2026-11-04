# Phase 4 — Identity, authorization, and human approval

Date: 2026-11-03

## Delivered

- ASP.NET Core authentication with three explicit modes:
  - `DevelopmentTokens`: ephemeral in-process signing key and short-lived local JWTs
  - `DevelopmentHeaders`: deterministic integration-test scheme, rejected outside Development
  - `Oidc`: authority/audience-based JWT validation for deployed environments
- Global authenticated-user fallback policy and claims-to-tenant/user/permission/access-tag mapping
- Swagger bearer scheme and automatic local development-token acquisition
- Tenant-partitioned rate limiting after authentication
- Registration-based Workflow Agent and high-risk `ticket-create` mock tool
- Durable approval model and PostgreSQL/in-memory approval stores
- Immutable argument JSON plus SHA-256 integrity digest
- Random short-lived approval token with only its digest persisted
- Atomic `Pending -> Executing -> Executed` transition and single-use enforcement
- Permission, tenant, expiry, tool version, schema, and parameter revalidation at approval time
- Rejection, failure, run-state, conversation-memory, SSE event, and audit updates
- Versioned EF Core initial migration and repository-local migration tool manifest

## Verification

- 16 .NET tests passed across unit, architecture, integration, and end-to-end suites.
- 5 Python document-worker tests passed in its container image.
- `dotnet format --verify-no-changes` and Docker Compose configuration validation passed.
- NuGet reported no known vulnerable direct or transitive package for the configured source.
- A fresh PostgreSQL 17 instance applied `InitialCreate`; a live JWT request persisted and executed one approval record.
- Anonymous business API request returns `401`.
- A locally issued JWT carries tenant/user claims and scopes conversation persistence.
- A state-changing request remains `PendingApproval`; no ticket exists before approval.
- Cross-tenant approval lookup returns `404`.
- A caller without `approvals.approve` receives `403`.
- An invalid approval token receives `403` without consuming the request.
- A valid token executes the mock connector and completes the run.
- Reusing the token receives `409` and does not execute a second ticket.

These are correctness and control-flow checks, not security certification or performance benchmarks.

## Open risks

- OIDC mode is configurable and uses the platform JWT bearer handler, but CI does not provision an external identity provider.
- Connector-side idempotency remains mandatory for real external systems.
- Approval expiration is enforced on access; a scheduled cleanup worker is not yet included.
- PostgreSQL tenant isolation is query-enforced rather than database row-level security.
- The synthetic ticket connector demonstrates the boundary but does not represent a real service desk integration.
