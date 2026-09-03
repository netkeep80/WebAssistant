# Stable `ci-required` Gate Design

## Context

WebAssistant currently exposes several independent GitHub Actions workflows for core tests, Linux systemd acceptance, Windows Service acceptance, virtual scanner acceptance, and permanent `repo-guard`. The repository roadmap requires one stable product-CI merge signal while keeping `repo-guard` as a separate governance trust boundary.

GitHub Actions `needs` works inside one workflow graph. Therefore a correct fail-closed aggregator cannot directly depend on jobs that remain isolated in unrelated PR workflows.

## Decision

Introduce `.github/workflows/ci.yml` as the only product-CI PR caller. Existing product workflows become reusable through `workflow_call`; their current `push: main` triggers remain until #9 so this slice does not silently perform post-merge optimization.

The caller has four reusable product jobs:

- core;
- ALT Linux systemd acceptance;
- Windows Service acceptance;
- virtual scanner acceptance.

A small requirement job exposes explicit booleans for each suite. In this slice every product suite is `true`; #6 will replace the constant policy with the repository-owned change classifier without renaming the external gate.

## Stable gate contract

The final normal job is named exactly `ci-required` and runs with `if: always()` after the requirement job and every reusable product job.

For each product job the gate evaluates `(required, result)`:

- required=true + success -> PASS;
- required=true + skipped/failure/cancelled/unknown -> FAIL;
- required=false + success -> PASS;
- required=false + skipped -> PASS;
- required=false + failure/cancelled/unknown -> FAIL.

Thus classifier-authorized skipping is representable later, while an unexpected failure or cancellation is never converted to success.

If the whole workflow is cancelled, `ci-required` itself may be cancelled; that is intentionally not merge-ready.

## Governance separation

`.github/workflows/repo-guard.yml` is not called by `ci.yml`, is not listed in `ci-required.needs`, and is not interpreted by the product aggregator. GitHub branch protection/ruleset must eventually require both checks independently:

- `ci-required`;
- `repo-guard`.

This prevents a product-CI aggregator implementation from masking governance failure or cancellation.

## Repository-owned evaluator

`.github/scripts/ci-required.sh` owns the result table and provides `--self-test`. The script is itself a governance path. The workflow runs the self-test before evaluating real job results.

## Structural verification

`tests/core/CiWorkflowContractTests.cs` verifies repository structure rather than product semantics:

- `ci.yml` exists and has a job named `ci-required`;
- the gate uses `always()` and depends on all reusable product jobs;
- component workflows expose `workflow_call` and do not retain direct PR triggers;
- component `push: main` triggers remain;
- `repo-guard.yml` remains separate and `ci.yml` does not call it;
- the evaluator path exists and is referenced by `ci.yml`.

## Policy integration

`repo-policy.json` is tightened to:

- declare `ci.yml` as a `ci_gate` integration workflow;
- protect `.github/scripts/ci-required.sh` as governance;
- require root README documentation to mention `ci-required` together with the separate `repo-guard` boundary.

No policy relaxation, contract/conformance change, or product change is part of this slice.

## Settings-level completion

The repository-side implementation is not sufficient by itself. `main` currently has no writable branch-protection/ruleset channel in the connected GitHub tool. Issue #3 therefore remains open after merge until settings-level evidence proves:

- direct push to `main` is blocked;
- `ci-required` is required;
- `repo-guard` is separately required.
