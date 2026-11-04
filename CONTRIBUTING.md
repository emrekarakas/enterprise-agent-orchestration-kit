# Contributing

Thank you for improving Enterprise Agent Orchestration Kit.

## Development loop

1. Install the SDK pinned by `global.json` and Docker.
2. Run `dotnet restore`, `dotnet build`, and `dotnet test`.
3. Run `dotnet format EnterpriseAgentOrchestrationKit.slnx` before opening a pull request.
4. Add tests at the lowest useful level and update an ADR when a decision changes.

Keep domain and application code independent of provider SDK types. External systems belong behind ports, and state-changing tools must remain approval-gated. Never submit credentials, customer data, proprietary prompts, or code copied from non-public repositories. Contributions are expected to be independently authored or compatible with this project's MIT license.

## Pull requests

Describe the behavior change, security impact, tests run, and any operational migration. Small, reviewable changes are preferred. CI must pass and new public behavior must be documented.
