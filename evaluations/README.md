# Evaluations

The evaluation runner exercises the real versioned HTTP API with the deterministic provider. It doesn't call a paid model and doesn't report invented benchmark numbers. When the API reports a document-worker readiness dependency, the runner first uploads its own tenant-scoped synthetic documents so the same dataset works with both the in-memory adapter and the Qdrant-backed local stack.

The versioned JSONL dataset covers routing, multi-agent execution, tool selection, grounded citations, no-answer behavior, and prompt-injection blocking. Thresholds are explicit in `thresholds.json`; generated reports are ignored by Git and uploaded as CI artifacts.

Run locally after starting the API in its default Development profile:

```bash
python3 evaluations/run.py
```

Use `--base-url`, `--dataset`, `--thresholds`, `--report`, and `--seed-directory` to target another environment. Run the smoke dataset only against local or disposable environments because it creates synthetic evaluation documents in `demo-tenant`. Token counts in the deterministic smoke report are whitespace-based estimates and cost is fixed to zero. Paid-provider evaluations must use provider-reported usage and a separately versioned manual workflow before publishing any cost or quality comparison.

The `manual-model-evaluation` GitHub Actions workflow runs only through `workflow_dispatch` and reads credentials from the protected `model-evaluation` environment. Pass `--provider-label` when invoking the runner outside that workflow. Real-provider reports mark cost as `null`/`not-collected`; do not interpret that as zero cost.
