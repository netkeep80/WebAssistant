# Stable `ci-required` Gate Design

## Context

WebAssistant historically ran core, ALT Linux systemd, Windows Service and virtual-scanner acceptance as separate PR workflows. A stable merge contract needs one product-CI result without weakening the independent `repo-guard` governance boundary.

GitHub Actions dependencies are workflow-local, so product jobs must share one caller graph before a normal downstream aggregator can evaluate their results.

## Accepted architecture

`.github/workflows/ci.yml` is the only product-CI PR caller. It invokes four reusable component workflows through `workflow_call`:

- core;
- ALT Linux systemd acceptance;
- Windows Service acceptance;
- virtual scanner acceptance.

Component workflows retain their current `push: main` behavior until #9; this slice changes PR orchestration only.

The caller exposes explicit requirement flags. In the current baseline all four are `true`. #6 may later derive them from a repository-owned classifier without changing the external gate name.

## Fail-closed truth table

The final normal job is named exactly `ci-required` and uses `if: always()` after the requirement job and all reusable product jobs.

Accepted `(required, result)` pairs are only:

```text
true  + success
false + success
false + skipped
```

All other states fail, including required `skipped`, any `failure`, any `cancelled`, unknown results and malformed requirement flags. The governance-owned workflow runs a table-driven self-test of this truth table before evaluating actual job results.

If the entire workflow run is cancelled, `ci-required` may itself be cancelled; that is intentionally not merge-ready.

## Governance separation

`.github/workflows/repo-guard.yml` remains a separate PR workflow. `ci.yml` neither calls nor aggregates it. GitHub protection must eventually require both independently:

- `ci-required`;
- `repo-guard`.

This prevents product-CI orchestration from masking governance failure or cancellation.

## Transaction boundary

The workflow cutover was merged as a governance-only transaction because current repo-guard correctly rejects mixing governance and non-governance paths in one governance ChangeIntent. Documentation and structural regression coverage therefore live in a separate ordinary transaction.

## Settings-level completion

Repository code now provides the stable checks, but `main` is still unprotected. Issue #3 remains open until branch protection/ruleset blocks ordinary direct push and requires both checks independently.
