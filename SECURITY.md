# Security policy

Please report suspected vulnerabilities privately to the maintainers rather than opening a public issue. Include affected version, reproduction steps, impact, and any suggested mitigation. Do not include real credentials or sensitive tenant data.

This repository demonstrates defense in depth; it is not a claim that an LLM system can be made fully secure. The local development-token issuer and optional test-header scheme are synthetic test facilities, not production identity systems. Production startup requires OIDC mode with configured authority and audience. Deployments must also use managed secrets, network controls, document malware scanning, tenant-scoped persistence policies, and an externally reviewed authorization model.

State-changing tools are stored as immutable proposals. Only a SHA-256 digest of the short-lived approval token is persisted; the clear token is returned once to the requesting client. Approval rechecks tenant, permission, tool version, argument integrity, expiry, and atomic state transition before execution. This reduces accidental or replayed execution but does not replace connector-side authorization and idempotency.
