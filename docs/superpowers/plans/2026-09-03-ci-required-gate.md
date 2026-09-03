# Stable `ci-required` Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce one stable fail-closed product CI check named `ci-required` while preserving permanent `repo-guard` as a separate governance merge boundary.

**Architecture:** A new PR caller workflow invokes existing product workflows through `workflow_call`, then a normal `ci-required` job evaluates explicit requirement flags and reusable-job results with `if: always()`. A repository-owned shell evaluator centralizes the result truth table; core tests verify the workflow structure. Component workflows retain `push: main` until #9.

**Tech Stack:** GitHub Actions reusable workflows, Bash, xUnit/.NET 10, repo-guard policy format 0.3.0.

**Spec:** `docs/superpowers/specs/2026-09-03-ci-required-gate-design.md`

## Global Constraints

- Product root `webassist/**` must not change.
- Contract/conformance files must not change.
- Permanent `.github/workflows/repo-guard.yml` stays separate from `ci-required`.
- No policy relaxation is permitted.
- Current component `push: main` execution is preserved until #9.
- All product suites are required in this slice; #6 owns selective classification.
- `failure` or `cancelled` must never become a successful `ci-required` verdict.
- `skipped` is accepted only for an explicitly non-required suite.
- GitHub settings-level protection remains a separate completion step because the current connector cannot write branch protection/rulesets.

---

### Task 1: Add failing repository CI contract tests

**Files:**
- Create: `tests/core/CiWorkflowContractTests.cs`

**Interfaces:**
- Consumes: repository files under `.github/workflows` and `.github/scripts`.
- Produces: xUnit evidence for the stable CI workflow contract.

- [ ] **Step 1: Write tests that require the future orchestration contract**

Create tests that locate repository root and assert:

```csharp
Assert.True(File.Exists(Path.Combine(root, ".github", "workflows", "ci.yml")));
Assert.Contains("ci-required:", ciText, StringComparison.Ordinal);
Assert.Contains("if: ${{ always() }}", ciText, StringComparison.Ordinal);
Assert.Contains(".github/scripts/ci-required.sh", ciText, StringComparison.Ordinal);
Assert.DoesNotContain("repo-guard.yml", ciText, StringComparison.Ordinal);
```

For each of `core.yml`, `linux-systemd.yml`, `windows-service.yml`, `virtual-scanner.yml`, assert `workflow_call:` is present and `pull_request:` is absent. Also assert their existing `push:` / `main` behavior remains present.

- [ ] **Step 2: Run core tests and verify RED**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter CiWorkflowContractTests
```

Expected: FAIL because `ci.yml` and `.github/scripts/ci-required.sh` do not exist and component workflows still have direct `pull_request` triggers.

- [ ] **Step 3: Commit the RED test**

```bash
git add tests/core/CiWorkflowContractTests.cs
git commit -m "test: define stable ci-required workflow contract"
```

### Task 2: Implement and self-test the fail-closed result evaluator

**Files:**
- Create: `.github/scripts/ci-required.sh`

**Interfaces:**
- Consumes environment variables `CORE_REQUIRED`, `CORE_RESULT`, `LINUX_SYSTEMD_REQUIRED`, `LINUX_SYSTEMD_RESULT`, `WINDOWS_SERVICE_REQUIRED`, `WINDOWS_SERVICE_RESULT`, `VIRTUAL_SCANNER_REQUIRED`, `VIRTUAL_SCANNER_RESULT`.
- Produces exit code 0 only when every `(required,result)` pair satisfies the design truth table.
- CLI: `.github/scripts/ci-required.sh --self-test` runs table-driven internal tests; no argument evaluates real environment variables.

- [ ] **Step 1: Implement `check_result`**

Use exact semantics:

```bash
check_result() {
  local name="$1" required="$2" result="$3"
  case "$required:$result" in
    true:success|false:success|false:skipped) return 0 ;;
    *) echo "ci-required: $name required=$required result=$result" >&2; return 1 ;;
  esac
}
```

Validate `required` is exactly `true|false` before the case so malformed classifier output fails closed.

- [ ] **Step 2: Add table-driven `--self-test`**

Cover at minimum:

```text
true/success -> pass
true/skipped -> fail
true/failure -> fail
true/cancelled -> fail
false/success -> pass
false/skipped -> pass
false/failure -> fail
false/cancelled -> fail
false/unknown -> fail
malformed-required/success -> fail
```

The self-test must exit non-zero on any mismatch.

- [ ] **Step 3: Evaluate the four actual suites**

Call `check_result` for core, Linux systemd, Windows Service, and virtual scanner; aggregate failures and exit 1 if any pair fails.

- [ ] **Step 4: Run self-test**

```bash
bash .github/scripts/ci-required.sh --self-test
```

Expected: PASS.

- [ ] **Step 5: Commit evaluator**

```bash
git add .github/scripts/ci-required.sh
git commit -m "ci: add fail-closed required gate evaluator"
```

### Task 3: Convert product workflows to reusable PR components

**Files:**
- Modify: `.github/workflows/core.yml`
- Modify: `.github/workflows/linux-systemd.yml`
- Modify: `.github/workflows/windows-service.yml`
- Modify: `.github/workflows/virtual-scanner.yml`

**Interfaces:**
- Consumes: `workflow_call` from the future `ci.yml` caller.
- Produces: existing jobs and product evidence unchanged; reusable-workflow call result reflects internal job success/failure/cancellation.

- [ ] **Step 1: Add `workflow_call` to every component workflow**

Each component must contain:

```yaml
on:
  workflow_call:
```

- [ ] **Step 2: Remove direct `pull_request` triggers**

PR execution must flow only through `ci.yml` so the component suites are not duplicated.

- [ ] **Step 3: Preserve existing `push: main` behavior**

Keep the current push trigger and current path filters exactly where they exist. For core, keep the current unrestricted `push` to `main`.

- [ ] **Step 4: Do not change job bodies**

No test command, runner, package/install command, timeout, source mapping, or product behavior is changed.

- [ ] **Step 5: Commit reusable conversion**

```bash
git add .github/workflows/core.yml .github/workflows/linux-systemd.yml .github/workflows/windows-service.yml .github/workflows/virtual-scanner.yml
git commit -m "ci: expose product suites as reusable workflows"
```

### Task 4: Add the top-level PR caller and stable `ci-required` job

**Files:**
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: the four reusable component workflows and `.github/scripts/ci-required.sh`.
- Produces: one normal check named `ci-required` for branch protection.

- [ ] **Step 1: Add PR trigger and read-only permissions**

Use:

```yaml
name: ci
on:
  pull_request:
    types: [opened, synchronize, reopened, ready_for_review]
permissions:
  contents: read
```

- [ ] **Step 2: Add explicit baseline requirement outputs**

Create a lightweight Ubuntu job `requirements` with outputs for `core`, `linux_systemd`, `windows_service`, and `virtual_scanner`. In this slice every output is the string `true`.

- [ ] **Step 3: Call the four reusable workflows**

Each caller job depends on `requirements`, uses the local `./.github/workflows/<component>.yml` file, and has an `if` expression keyed to its requirement output. Because all outputs are currently true, all product suites run.

- [ ] **Step 4: Add stable final job**

Use exact job id and display name:

```yaml
ci-required:
  name: ci-required
  if: ${{ always() }}
  needs:
    - requirements
    - core
    - linux-systemd
    - windows-service
    - virtual-scanner
```

The job runs on Ubuntu, checks `needs.requirements.result == 'success'`, runs `bash .github/scripts/ci-required.sh --self-test`, exports each requirement flag and `needs.<job>.result`, then runs `bash .github/scripts/ci-required.sh`.

- [ ] **Step 5: Explicitly keep repo-guard out of the graph**

`ci.yml` must not reference or call `.github/workflows/repo-guard.yml` and `ci-required.needs` must not contain repo-guard.

- [ ] **Step 6: Run structural core test**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter CiWorkflowContractTests
```

Expected: PASS.

- [ ] **Step 7: Commit caller workflow**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add stable ci-required PR gate"
```

### Task 5: Bind the new gate into repository policy and documentation

**Files:**
- Modify: `repo-policy.json`
- Modify: `README.md`

**Interfaces:**
- Consumes: accepted repo-guard integration schema 0.3.0.
- Produces: executable policy evidence for the CI gate and documentation of the external merge contract.

- [ ] **Step 1: Add `ci_gate` integration workflow**

Append an `integration.workflows` entry:

```json
{
  "id": "ci-required-pr-gate",
  "kind": "github_actions",
  "path": ".github/workflows/ci.yml",
  "role": "ci_gate",
  "expect": {
    "events": ["pull_request"],
    "event_types": ["opened", "synchronize", "reopened", "ready_for_review"],
    "permissions": {"contents": "read"},
    "disallow": ["continue_on_error"]
  }
}
```

- [ ] **Step 2: Protect the evaluator as governance**

Add `.github/scripts/ci-required.sh` to `paths.governance_paths`.

- [ ] **Step 3: Tighten README integration evidence**

Extend `integration.docs[readme-governance].must_mention` with `ci-required` and `must_reference_files` with `.github/workflows/ci.yml` and `.github/scripts/ci-required.sh`.

- [ ] **Step 4: Document merge boundary in README**

State that product merge readiness is `ci-required`, governance is separately `repo-guard`, and branch protection must require both. State that current repository settings still need to be enabled before #3 closes.

- [ ] **Step 5: Run full core tests**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
```

Expected: all tests PASS.

- [ ] **Step 6: Commit policy/docs**

```bash
git add repo-policy.json README.md
git commit -m "governance: bind stable ci-required merge contract"
```

### Task 6: PR verification and fixed-head merge

**Files:**
- No new repository files.

**Interfaces:**
- Consumes: GitHub Actions evidence and repo-guard ChangeIntent/GovernanceGrant from #88.
- Produces: accepted repository-side CI gate baseline.

- [ ] **Step 1: Open PR linked to #88**

Use the exact #88 ChangeIntent scope and trusted GovernanceGrant.

- [ ] **Step 2: Verify repo-guard diagnostics**

Expected:

```text
change-intent PASS
governance-grant PASS
governance-change-authorization PASS
policy-relaxation PASS with no relaxation grant
proposed-policy veto PASS
```

- [ ] **Step 3: Verify new CI workflow**

Expected on exact PR head:

```text
core reusable suite PASS
ALT Linux systemd reusable suite PASS
Windows Service reusable suite PASS
virtual scanner reusable suite PASS
ci-required PASS
repo-guard PASS separately
```

- [ ] **Step 4: Verify no duplicate direct PR component workflows**

The PR should have the caller graph plus separate repo-guard, not separate second copies launched by component `pull_request` triggers.

- [ ] **Step 5: Fixed-head gate**

Fresh-read `main`, refresh PR head, require `behind_by=0`, then merge only with `expected_head_sha` equal to the verified head.

- [ ] **Step 6: Post-merge verification**

Fresh-read `main`, `ci.yml`, component workflows, `repo-policy.json`, and README. Confirm #88 closed by merge. Keep #3 open until branch protection/ruleset requires `ci-required` and `repo-guard` separately and direct push is blocked.
