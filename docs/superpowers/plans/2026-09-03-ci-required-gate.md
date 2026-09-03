# Stable `ci-required` Gate Implementation Record

## Goal

Provide a stable product-CI merge signal named `ci-required` while preserving permanent `repo-guard` as an independent governance check.

## Implemented workflow slice

Accepted by PR #92:

- `.github/workflows/ci.yml` is the product PR caller;
- `core.yml`, `linux-systemd.yml`, `windows-service.yml` and `virtual-scanner.yml` expose `workflow_call` and no longer run independently on PR events;
- their existing `push: main` behavior remains for later optimization in #9;
- current requirement flags are conservatively all `true`;
- `ci-required` uses `if: always()` and evaluates all reusable-job results fail-closed;
- truth-table self-test is embedded in governance-owned `ci.yml`;
- `repo-guard` is outside the product graph.

## Verification record

TDD/debugging on PR #92 first demonstrated RED structural expectations before the caller existed. Repo-guard then rejected a mixed governance/non-governance transaction. The final workflow-only diff corrected that root cause.

Exact accepted feature head:

```text
a66cbcb96408f517a26bba6d229fdfc323a94cca
```

Exact-head evidence before merge:

- repo-guard: 27 PASS / 0 FAIL;
- core reusable job: GREEN;
- ALT Linux systemd lifecycle: GREEN;
- Windows Service lifecycle: GREEN;
- Linux SANE direct SDK + HTTP E2E: GREEN;
- Windows TWAIN direct SDK + HTTP E2E: GREEN;
- `ci-required`: GREEN;
- `ci-required` actual inputs: all four requirements `true`, all four job results `success`;
- no separate component PR workflows launched;
- `behind_by=0`;
- merge used exact `expected_head_sha`.

Accepted `main` after merge:

```text
40353f11c471dfedf7ec5eb7b6878dcc55ff5039
```

## Remaining work in #3

Repository code is complete, but GitHub settings are not: `main` still reports `protected: false`. #3 remains open until branch protection/ruleset:

1. blocks ordinary direct push to `main`;
2. requires `ci-required`;
3. requires `repo-guard` separately.

After that settings transaction, perform a positive merge probe and a negative required-check bypass/direct-push probe if GitHub safely permits it, then close #3 and #25.
