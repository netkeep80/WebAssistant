# WebAssistant repo-guard Bootstrap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Подключить current `repo-guard` к WebAssistant как fail-closed policy boundary, доказать current contract/conformance и product-root isolation, затем установить permanent `check-pr` gate.

**Architecture:** Bootstrap разделён на две атомарные PR-транзакции, потому что `check-pr` доверяет policy из base branch. Первая транзакция вводит policy/templates/docs и проверяет head policy через `check-diff` с пустым diff. После merge вторая транзакция удаляет bootstrap workflow, добавляет permanent `check-pr` workflow и монотонно добавляет его integration expectation. После этого отдельные probe PR доказывают positive product-only path и два отрицательных fail-closed сценария.

**Tech Stack:** GitHub Actions, `netkeep80/repo-guard@063169d658b2915392f42544fed260d07380a4cd`, policy format `0.3.0`, JSON contract/conformance, Markdown/YAML templates.

**Spec:** WebAssistant issue #25.

## Global Constraints

- Legacy `netkeep80/ScannerAgent` is read-only.
- Current WebAssistant bootstrap base is `984381e6300d94ab34118ed1dc98f6e724bc1495`; always refresh before merge.
- Current verified repo-guard pin is `063169d658b2915392f42544fed260d07380a4cd`; do not use `main` or `latest` in Actions.
- Accepted pair remains unchanged: `contracts/webassistant-contract-v0.1.json` + `contracts/webassistant-conformance-v0.1.json`.
- `webassist/**` is not modified by bootstrap/cutover transactions.
- Governance changes require the external `repo-guard-grant` stored in issue #25.
- `allow_policy_relaxation` remains empty; root `/` relaxation is forbidden.
- Permanent workflow uses blocking `check-pr`, full checkout history, read-only contents/pull-request/issues permissions and `GH_TOKEN` from `github.token`.
- Branch/ruleset protection remains owned by issue #3; #25 installs the governance check and records it as mandatory merge-readiness evidence.

---

### Task 1: Bootstrap executable policy

**Files:**
- Create: `repo-policy.json`
- Create: `.github/PULL_REQUEST_TEMPLATE.md`
- Create: `.github/ISSUE_TEMPLATE/change-intent.yml`
- Create: `.github/workflows/repo-guard-bootstrap.yml`
- Modify: `README.md`
- Existing plan: `docs/superpowers/plans/2026-09-03-repo-guard-bootstrap.md`

**Interfaces:**
- Consumes: accepted WebAssistant v0.1 contract/conformance and current repository paths.
- Produces: policy format `0.3.0` that Task 2 can trust from `main`.

- [ ] **Step 1: Define `contract_conformance` against the accepted v0.1 pair**

Use:

```json
{
  "current": {
    "contract": {"path": "contracts/webassistant-contract-v0.1.json", "format": "json"},
    "conformance": {"path": "contracts/webassistant-conformance-v0.1.json", "format": "json"}
  },
  "pair_fields": {
    "contract_id": "/schema",
    "conformance_contract_id": "/contract",
    "contract_conformance_path": "/conformanceCorpus",
    "contract_status": "/status",
    "conformance_status": "/status",
    "contract_accepted": "/accepted",
    "conformance_accepted": "/accepted"
  },
  "accepted_state": {"status": "accepted", "accepted": true},
  "required_paths": [{"document": "current.conformance", "pointer": "/requiredRepositoryPaths", "projection": "array_items"}],
  "cochange": ["current.contract", "current.conformance"],
  "control_paths": ["contracts/webassistant-contract-v*.json", "contracts/webassistant-conformance-v*.json"]
}
```

- [ ] **Step 2: Encode standalone-product leakage rules**

`content_rules` on added lines under `webassist/**` must reject parent-repository-only references, at minimum:

```text
netkeep80/WebAssistant
repo-policy.json
.github/workflows/
docs/superpowers/
contracts/webassistant-
```

These rules must not reject upstream NAPS2 links or product-local `.gitlab-ci.yml` documentation.

- [ ] **Step 3: Define governance paths and contributor surfaces**

Governance paths:

```text
repo-policy.json
contracts/**
.github/workflows/**
.github/PULL_REQUEST_TEMPLATE.md
.github/ISSUE_TEMPLATE/**
```

Product/test/docs surfaces remain ordinary non-governance surfaces, so normal product changes do not require `GovernanceGrant`.

- [ ] **Step 4: Create ChangeIntent templates**

PR template and issue form must expose a fenced `repo-guard-yaml` block with at least:

```yaml
change_type: feature
scope:
  - webassist/**
budgets:
  max_new_files: 5
  max_new_docs: 1
  max_net_added_lines: 500
anchors:
  affects: []
  implements: []
  verifies: []
must_touch: []
must_not_touch: []
expected_effects:
  - describe the observable effect
```

- [ ] **Step 5: Add bootstrap validation workflow**

The bootstrap workflow runs only while base branch has no policy. It checks out full history and invokes exact-pinned repo-guard in blocking `check-diff` mode with `base: HEAD` and `head: HEAD`, validating the head policy/current repository state with an empty diff. It must not use `continue-on-error`, manual clone, or temporary repo-guard CLI execution.

- [ ] **Step 6: Document governance entry points in root README**

README must mention `repo-guard`, `ChangeIntent`, `GovernanceGrant`, `repo-policy.json`, the PR template and accepted WebAssistant v0.1 contract/conformance paths. Product-local docs remain untouched.

- [ ] **Step 7: Open bootstrap PR and verify head-policy GREEN**

Expected evidence:

```text
repo-guard bootstrap workflow -> SUCCESS
core -> SUCCESS when triggered
```

Before merge: fresh main, exact head, `behind_by=0`, fixed-head merge, post-merge reread.

---

### Task 2: Cut over to permanent `check-pr`

**Files:**
- Delete: `.github/workflows/repo-guard-bootstrap.yml`
- Create: `.github/workflows/repo-guard.yml`
- Modify: `repo-policy.json`
- Modify: `README.md` only if the permanent workflow path is not already documented.

**Interfaces:**
- Consumes: trusted policy from Task 1 in `main`.
- Produces: stable blocking PR check named `repo-guard` using exact Action SHA.

- [ ] **Step 1: Add permanent workflow**

Permanent job properties:

```yaml
name: repo-guard
on:
  pull_request:
    types: [opened, synchronize, reopened, ready_for_review]
permissions:
  contents: read
  pull-requests: read
  issues: read
jobs:
  repo-guard:
    name: repo-guard
    runs-on: ubuntu-24.04
```

Checkout uses `fetch-depth: 0`; Action uses exact `063169d658b2915392f42544fed260d07380a4cd`, `mode: check-pr`, `enforcement: blocking`, and `GH_TOKEN: ${{ github.token }}`.

- [ ] **Step 2: Add exact integration expectation**

`repo-policy.json.integration.workflows` gains exactly one `repo_guard_pr_gate` expectation for `.github/workflows/repo-guard.yml`, with exact SHA pinning and disallow list:

```text
continue_on_error
manual_clone
direct_temp_cli_execution
```

- [ ] **Step 3: Remove bootstrap workflow in the same atomic cutover**

No two permanent repo-guard execution paths remain after merge.

- [ ] **Step 4: Verify permanent gate on exact PR head**

`repo-guard` must read policy and trusted GovernanceGrant from base/issue #25 and pass without any policy-relaxation grant.

Before merge: fresh main, exact head, `behind_by=0`, fixed-head merge, post-merge reread.

---

### Task 3: Prove fail-closed behavior with disposable PRs

**Files:** Temporary probe branches only; none are merged.

**Interfaces:**
- Consumes: permanent repo-guard gate from Task 2.
- Produces: GitHub-run evidence linked back to issue #25.

- [ ] **Step 1: Positive product-only probe**

Create a harmless product-document change under `webassist/` with valid ChangeIntent and no GovernanceGrant. Expected: repo-guard SUCCESS. Close probe without merge.

- [ ] **Step 2: Negative contract weakening probe**

On a separate branch change current contract `accepted` from `true` to `false` (with contract/conformance cochange as required so failure is specifically accepted-state weakening). Expected: repo-guard FAIL. Close without merge.

- [ ] **Step 3: Negative product-root leakage probe**

On a separate branch add a parent-repository reference such as `repo-policy.json` to `webassist/README.md`. Expected: repo-guard FAIL specifically on product-root content rule. Close without merge.

- [ ] **Step 4: Record exact run/head evidence in issue #25**

Include probe PR numbers, head SHAs, workflow run conclusions and the diagnostic that caused each negative failure.

---

### Task 4: Close governance bootstrap and hand off merge protection

**Files:**
- Issue #25 status/comment
- Issue #3 comment/dependency only; no code required here.

**Interfaces:**
- Consumes: GREEN permanent gate + positive/negative probe evidence.
- Produces: completed repo-guard bootstrap and explicit dependency for future `ci-required`/branch protection.

- [ ] **Step 1: Re-read `main` and permanent workflow/policy**

Confirm exact pin, blocking mode, accepted pair paths, empty relaxation list and no bootstrap workflow.

- [ ] **Step 2: Comment on #3**

State that branch protection/`ci-required` must preserve repo-guard failure/cancellation as blocking evidence and may not convert it into an aggregator success.

- [ ] **Step 3: Close #25 only after all acceptance bullets have fresh evidence**

Do not claim branch protection itself is complete; that remains issue #3.
