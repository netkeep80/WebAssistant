# Tiered CI Feedback / Exact-Head Acceptance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Разделить текущий PR CI на быстрый Tier A (`ci-fast`) и exact-head Tier B (`ci-required`), сохранив fail-closed selective routing из #6 и все существующие platform acceptance semantics.

**Architecture:** Остаётся один top-level `.github/workflows/ci.yml` и один repository-owned path classifier `ci/change-plan.sh`. Tier A запускается на каждом PR head и состоит из requirements + core + применимого platform adapter smoke; Tier B запускается только для ready PR или explicit manual full run и содержит применимые lifecycle/E2E evidence. Все child workflows получают explicit exact `source_ref`; `ci-required` остаётся stable final product gate, а `repo-guard` остаётся отдельным governance gate.

**Tech Stack:** GitHub Actions YAML, Bash, `gh api`, `jq`, .NET 10, xUnit, existing WebAssistant reusable workflows.

**Spec:** `docs/superpowers/specs/2026-09-07-tiered-ci-feedback-design.md`

## Global Constraints

- Canonical base at plan creation: `main = dc4875d4ac39218e12d062037ad3ec5259ed2328`.
- Accepted product version on that base: `webassist/VERSION = 0.3.6`; #5 transaction advances it to `0.3.7` exactly once relative to accepted `main`.
- Accepted contract/conformance pair remains `webassistant-contract/v0.2` + `webassistant-conformance/v0.2`; do not modify either file or `repo-policy.json`.
- Do not modify product/runtime source, installers, package contents, runtime API, scanner semantics, Windows Service semantics, ALT systemd semantics, or vendored SDK provenance.
- Stable final product gate name remains exactly `ci-required`.
- Permanent `repo-guard` remains a separate trust boundary; never aggregate or weaken it.
- Unknown production/CI paths and VERSION-only transitions remain fail-closed to full cross-platform classification.
- A required job may satisfy an aggregator only with `success`; required `skipped`, `failure`, `cancelled`, missing or unknown results are failures.
- `ci-fast` is development feedback only and must never become the branch-protection contract.
- Full closure of #5 remains externally blocked by #3/#25 until GitHub actually requires `ci-required` + `repo-guard` and forbids direct push to `main`.
- Before every GitHub write: reread fresh `main`, target Issue/PR/grant, policy, relevant branch head and competing open work.
- Before merge: exact head, `behind_by=0`, repo-guard GREEN, applicable product CI GREEN, intended diff, no review blockers, fixed-head merge, post-merge reread.

---

### Task 1: Add RED executable contracts for Tier A / Tier B

**Files:**
- Modify: `tests/core/ChangePlanClassifierTests.cs`
- Create: `tests/core/TieredCiWorkflowContractTests.cs`
- Read-only reference: `tests/core/CiWorkflowContractTests.cs`
- Existing version marker already prepared by approved spec branch: `webassist/VERSION`

**Interfaces:**
- Consumes: existing `ci/change-plan.sh --paths ...` key/value output.
- Produces: required new classifier keys `smoke_linux` and `smoke_windows`; structural contract for `workflow_dispatch`, `ci-fast`, final-only `ci-required`, `source_ref`, and `smoke|full` reusable scanner modes.

- [ ] **Step 1: Extend classifier test helper with smoke expectations**

Change `AssertPlan(...)` so every case explicitly checks both new outputs:

```csharp
private static void AssertPlan(
    string[] paths,
    bool core,
    bool linuxSystemd,
    bool windowsService,
    bool virtualLinux,
    bool virtualWindows,
    bool smokeLinux,
    bool smokeWindows,
    bool fullCrossPlatform)
{
    var plan = RunPlan(paths);

    Assert.Equal(core, Flag(plan, "core"));
    Assert.Equal(linuxSystemd, Flag(plan, "linux_systemd"));
    Assert.Equal(windowsService, Flag(plan, "windows_service"));
    Assert.Equal(virtualLinux, Flag(plan, "virtual_linux"));
    Assert.Equal(virtualWindows, Flag(plan, "virtual_windows"));
    Assert.Equal(smokeLinux, Flag(plan, "smoke_linux"));
    Assert.Equal(smokeWindows, Flag(plan, "smoke_windows"));
    Assert.Equal(virtualLinux || virtualWindows, Flag(plan, "virtual_scanner"));
    Assert.Equal(fullCrossPlatform, Flag(plan, "full_cross_platform"));
}
```

Required truth table:

```text
docs-only                  smoke_linux=false smoke_windows=false
Windows-only               false              true
Linux-only                 true               false
common/mixed/unknown       true               true
classifier/workflow change true               true
VERSION-only               true               true
```

- [ ] **Step 2: Add structural RED tests for the top-level workflow**

Create `TieredCiWorkflowContractTests.cs` and assert exact required tokens, including:

```csharp
Assert.Contains("workflow_dispatch:", ci, StringComparison.Ordinal);
Assert.Contains("pr_number:", ci, StringComparison.Ordinal);
Assert.Contains("pull-requests: read", ci, StringComparison.Ordinal);
Assert.Contains("final_required:", ci, StringComparison.Ordinal);
Assert.Contains("smoke_linux:", ci, StringComparison.Ordinal);
Assert.Contains("smoke_windows:", ci, StringComparison.Ordinal);
Assert.Contains("scanner-smoke:", ci, StringComparison.Ordinal);
Assert.Contains("mode: smoke", ci, StringComparison.Ordinal);
Assert.Contains("scanner-final:", ci, StringComparison.Ordinal);
Assert.Contains("mode: full", ci, StringComparison.Ordinal);
Assert.Contains("ci-fast:", ci, StringComparison.Ordinal);
Assert.Contains("name: ci-fast", ci, StringComparison.Ordinal);
Assert.Contains("GITHUB_SHA", ci, StringComparison.Ordinal);
Assert.Contains("gh api", ci, StringComparison.Ordinal);
Assert.Contains("pulls/$pr_number", ci, StringComparison.Ordinal);
```

Also assert that `ci-required` is guarded independently of `requirements` outputs so a broken requirements job on a ready/manual final run cannot make the final check disappear:

```csharp
Assert.Contains("github.event_name == 'workflow_dispatch'", ci, StringComparison.Ordinal);
Assert.Contains("github.event.pull_request.draft == false", ci, StringComparison.Ordinal);
```

- [ ] **Step 3: Add structural RED tests for child exact-head checkout and scanner mode**

For `core.yml`, `linux-systemd.yml`, `windows-service.yml`, assert `workflow_call.inputs.source_ref` exists and checkout contains an explicit `ref:` expression falling back to `github.sha` for standalone push behavior.

For `virtual-scanner.yml`, additionally assert:

```csharp
Assert.Contains("mode:", scanner, StringComparison.Ordinal);
Assert.Contains("smoke", scanner, StringComparison.Ordinal);
Assert.Contains("full", scanner, StringComparison.Ordinal);
Assert.Contains("source_ref:", scanner, StringComparison.Ordinal);
Assert.Contains("Category=LinuxVirtualScanner", scanner, StringComparison.Ordinal);
Assert.Contains("Category=WindowsVirtualScanner", scanner, StringComparison.Ordinal);
Assert.Contains("Category=PlatformVirtualEndToEnd", scanner, StringComparison.Ordinal);
```

The test must verify that HTTP E2E is conditional on effective mode `full` rather than unconditional.

- [ ] **Step 4: Run the focused RED suite**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter "FullyQualifiedName~ChangePlanClassifierTests|FullyQualifiedName~TieredCiWorkflowContractTests"
```

Expected: compile succeeds; failures are specifically missing `smoke_linux`/`smoke_windows`, missing `workflow_dispatch`/`ci-fast`, missing `source_ref`, and missing `smoke|full` scanner behavior. Existing unrelated tests remain PASS.

- [ ] **Step 5: Publish the RED commit and record evidence**

Commit message:

```text
test(ci): specify tiered fast and final acceptance
```

If using GitHub CI as the RED boundary, open a draft PR only after a fresh write-boundary reread. Record exact RED head/run IDs in the PR/Issue; do not weaken tests after RED is observed.

---

### Task 2: Extend the existing change classifier with smoke outputs

**Files:**
- Modify: `ci/change-plan.sh`
- Test: `tests/core/ChangePlanClassifierTests.cs`

**Interfaces:**
- Consumes: exact `base SHA -> head SHA` or `--paths` list.
- Produces: existing outputs unchanged plus `smoke_linux=true|false` and `smoke_windows=true|false`.

- [ ] **Step 1: Add smoke state without creating a second classifier**

Initialize:

```bash
smoke_linux=false
smoke_windows=false
```

Extend `require_full()`:

```bash
smoke_linux=true
smoke_windows=true
```

In Windows-only cases set `smoke_windows=true`; in Linux-only cases set `smoke_linux=true`. Docs-only keeps both false. Mixed/common/unknown/self-change and VERSION-only flow through `require_full()` and therefore set both true.

- [ ] **Step 2: Emit the new machine-readable outputs**

At the end of `ci/change-plan.sh` add:

```bash
printf 'smoke_linux=%s\n' "$smoke_linux"
printf 'smoke_windows=%s\n' "$smoke_windows"
```

Do not change the meaning of `linux_systemd`, `windows_service`, `virtual_linux`, `virtual_windows`, `virtual_scanner`, or `full_cross_platform`.

- [ ] **Step 3: Run classifier tests**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter "FullyQualifiedName~ChangePlanClassifierTests"
```

Expected: PASS for docs-only, Windows-only, Linux-only, common, mixed, unknown, self-change and VERSION-only cases.

- [ ] **Step 4: Commit the classifier change**

```text
feat(ci): classify platform smoke requirements
```

---

### Task 3: Make reusable workflows exact-head aware and add scanner smoke/full mode

**Files:**
- Modify: `.github/workflows/core.yml`
- Modify: `.github/workflows/linux-systemd.yml`
- Modify: `.github/workflows/windows-service.yml`
- Modify: `.github/workflows/virtual-scanner.yml`
- Test: `tests/core/TieredCiWorkflowContractTests.cs`

**Interfaces:**
- Produces on all child workflows: `workflow_call.inputs.source_ref` string, default `''`.
- Produces on scanner workflow: `workflow_call.inputs.mode` string, default `full`, accepted values exactly `smoke|full`.
- Preserves standalone `push: main` behavior by using `github.sha` and effective mode `full` on push.

- [ ] **Step 1: Add exact `source_ref` checkout to core/service workflows**

For each of `core.yml`, `linux-systemd.yml`, and `windows-service.yml` define:

```yaml
on:
  workflow_call:
    inputs:
      source_ref:
        description: Exact commit/ref to test when called by the top-level CI.
        required: false
        type: string
        default: ''
```

Change checkout to:

```yaml
- uses: actions/checkout@v6
  with:
    ref: ${{ inputs.source_ref || github.sha }}
```

Keep every existing platform test/lifecycle step unchanged.

- [ ] **Step 2: Add a fail-closed scanner input resolver**

In `virtual-scanner.yml` add `source_ref` and `mode` workflow_call inputs, then add a small `resolve-inputs` job. It must compute effective values:

```text
push event       -> mode=full, source_ref=github.sha, run_linux=true, run_windows=true
workflow_call    -> mode must be smoke|full; source_ref defaults to github.sha; run flags come from inputs
invalid mode     -> resolver job fails before platform jobs
```

Resolver output names:

```text
mode
source_ref
run_linux
run_windows
```

- [ ] **Step 3: Make Linux/Windows acquisition the smoke boundary**

Both platform jobs `need: resolve-inputs`, checkout `needs.resolve-inputs.outputs.source_ref`, and always run their acquisition/adaptor test when their platform is selected.

Linux smoke still performs SANE test-backend setup and:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter "Category=LinuxVirtualScanner"
```

Windows smoke still installs the official TWAIN sample source and runs:

```powershell
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter "Category=WindowsVirtualScanner"
```

- [ ] **Step 4: Gate HTTP E2E on mode=full**

Add an `if` to both `Category=PlatformVirtualEndToEnd` steps:

```yaml
if: ${{ needs.resolve-inputs.outputs.mode == 'full' }}
```

Do not remove the E2E commands. Repository-owned SDK feed assertions may remain in both modes because they are cheap dependency-integrity checks and do not create lifecycle/E2E work.

- [ ] **Step 5: Run workflow structural tests**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter "FullyQualifiedName~TieredCiWorkflowContractTests|FullyQualifiedName~VirtualScannerWorkflowTests"
```

Expected: PASS, including exact source ref, valid mode contract, smoke suppression of HTTP E2E, and preserved full E2E text.

- [ ] **Step 6: Commit reusable workflow changes**

```text
feat(ci): add exact-head reusable smoke and full modes
```

---

### Task 4: Orchestrate Tier A and Tier B in the single top-level CI

**Files:**
- Modify: `.github/workflows/ci.yml`
- Test: `tests/core/TieredCiWorkflowContractTests.cs`
- Test: `tests/core/CiWorkflowContractTests.cs` only if existing stable-gate assertions need naming updates; never weaken fail-closed assertions.

**Interfaces:**
- `requirements` outputs: `base_sha`, `head_sha`, `final_required`, existing plan outputs, `smoke_linux`, `smoke_windows`.
- Tier A aggregator: stable development check `ci-fast`.
- Tier B aggregator: existing final check `ci-required`.

- [ ] **Step 1: Add manual dispatch and permissions**

Top-level trigger becomes:

```yaml
on:
  pull_request:
    types: [opened, synchronize, reopened, ready_for_review]
  workflow_dispatch:
    inputs:
      pr_number:
        description: Open PR number whose current exact head must receive full acceptance.
        required: true
        type: string
```

Permissions:

```yaml
permissions:
  contents: read
  pull-requests: read
```

Concurrency remains PR-scoped for both event types:

```yaml
concurrency:
  group: webassistant-pr-${{ github.event.pull_request.number || inputs.pr_number }}
  cancel-in-progress: true
```

- [ ] **Step 2: Resolve event context before checkout**

Add `id: context` as the first requirements step. For `pull_request`, emit directly from event payload:

```text
base_sha = github.event.pull_request.base.sha
head_sha = github.event.pull_request.head.sha
final_required = !github.event.pull_request.draft
```

For `workflow_dispatch`, use only the workflow `GITHUB_TOKEN`:

```bash
export GH_TOKEN="$GH_TOKEN"
pr_json="$(gh api "repos/$GITHUB_REPOSITORY/pulls/$pr_number")"
state="$(jq -r '.state' <<<"$pr_json")"
base_sha="$(jq -r '.base.sha' <<<"$pr_json")"
head_sha="$(jq -r '.head.sha' <<<"$pr_json")"

[[ "$state" == open ]] || { echo 'manual full acceptance requires an open PR' >&2; exit 1; }
[[ "$GITHUB_SHA" == "$head_sha" ]] || {
  echo "manual full acceptance ref mismatch: selected=$GITHUB_SHA current_pr_head=$head_sha" >&2
  exit 1
}

printf 'base_sha=%s\n' "$base_sha" >> "$GITHUB_OUTPUT"
printf 'head_sha=%s\n' "$head_sha" >> "$GITHUB_OUTPUT"
printf 'final_required=true\n' >> "$GITHUB_OUTPUT"
```

Do not accept a free-form SHA input.

- [ ] **Step 3: Checkout and classify the resolved exact head**

Checkout:

```yaml
- uses: actions/checkout@v6
  with:
    ref: ${{ steps.context.outputs.head_sha }}
    fetch-depth: 0
```

Classifier env uses context outputs, not direct event-only expressions.

Export all classifier keys, including `smoke_linux` and `smoke_windows`, through `requirements.outputs`.

- [ ] **Step 4: Build Tier A jobs**

Keep core as a reusable child and pass:

```yaml
with:
  source_ref: ${{ needs.requirements.outputs.head_sha }}
```

Add `scanner-smoke`:

```yaml
if: ${{ needs.requirements.outputs.smoke_linux == 'true' || needs.requirements.outputs.smoke_windows == 'true' }}
uses: ./.github/workflows/virtual-scanner.yml
with:
  source_ref: ${{ needs.requirements.outputs.head_sha }}
  mode: smoke
  run_linux: ${{ needs.requirements.outputs.smoke_linux == 'true' }}
  run_windows: ${{ needs.requirements.outputs.smoke_windows == 'true' }}
```

- [ ] **Step 5: Add fail-closed `ci-fast`**

`ci-fast` must use `if: ${{ always() }}` and need `requirements`, `core`, and `scanner-smoke`.

Its shell verifier must enforce:

```text
requirements must be success
core required flag/result must be valid and success when required
scanner-smoke required=(smoke_linux || smoke_windows)
required success only; optional success/skipped only
failure/cancelled/unknown/missing -> fail
```

Reuse the existing `check_result` truth table rather than inventing weaker semantics.

- [ ] **Step 6: Gate every heavy Tier B job by `final_required`**

ALT systemd:

```yaml
if: ${{ needs.requirements.outputs.final_required == 'true' && needs.requirements.outputs.linux_systemd == 'true' }}
```

Windows Service uses the analogous condition. Pass exact `source_ref` to both.

Replace the current full virtual-scanner call with `scanner-final`, gated by `final_required && virtual_scanner`, with `mode: full` and exact `source_ref`.

- [ ] **Step 7: Make `ci-required` final-only but fail closed on broken requirements**

Do not gate `ci-required` only on `needs.requirements.outputs.final_required`, because a failed requirements job could erase the final check. Use event-level eligibility:

```yaml
if: ${{ always() && (github.event_name == 'workflow_dispatch' || github.event.pull_request.draft == false) }}
```

Needs:

```text
requirements
ci-fast
linux-systemd
windows-service
scanner-final
```

Verifier first requires:

```text
REQUIREMENTS_RESULT=success
FINAL_REQUIRED=true
CI_FAST_RESULT=success
```

Then checks each Tier B plan flag/result using the same fail-closed truth table. A draft PR without manual full run therefore has `ci-fast` but no successful `ci-required`; ready/manual final events always create a final verdict even when requirements fail.

- [ ] **Step 8: Run the full core repository test suite**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
```

Expected: all tests PASS. Do not update tests merely to accommodate an unexpected GREEN implementation behavior; fix workflow/classifier semantics instead.

- [ ] **Step 9: Commit top-level orchestration**

```text
feat(ci): split fast feedback from exact-head acceptance
```

---

### Task 5: Establish governance authorization and prove the implementation PR

**Files:**
- No additional repository semantics beyond Tasks 1-4.
- GitHub control plane: #5, new GovernanceGrant Issue, implementation PR.

**Interfaces:**
- GovernanceGrant authorizes only changed workflow governance paths.
- No policy relaxation.

- [ ] **Step 1: Fresh-read merge boundary inputs**

Read fresh `main`, #5, policy, implementation branch, open PRs, current VERSION and the exact changed-file set. If `main` moved, rebuild the #5 branch onto fresh main before any PR write and advance VERSION relative to that new accepted state.

- [ ] **Step 2: Create a narrow GovernanceGrant**

Authorize exactly:

```text
.github/workflows/ci.yml
.github/workflows/core.yml
.github/workflows/linux-systemd.yml
.github/workflows/windows-service.yml
.github/workflows/virtual-scanner.yml
```

Grant must include:

```yaml
allow_atomic_governance_cutover: true
allow_policy_relaxation: []
```

Atomic cutover is required because workflow governance changes are intentionally co-delivered with repository-owned classifier/tests/spec/plan and the mandatory VERSION marker.

- [ ] **Step 3: Open the implementation PR with trusted linkage**

PR body must contain `Fixes <GovernanceGrant issue>` so repo-guard recognizes the trusted grant, and `Refs #5` rather than auto-closing #5 because #3 still blocks final closure.

ChangeIntent scope is limited to:

```text
docs/superpowers/specs/2026-09-07-tiered-ci-feedback-design.md
docs/superpowers/plans/2026-09-07-tiered-ci-feedback.md
ci/change-plan.sh
tests/core/ChangePlanClassifierTests.cs
tests/core/TieredCiWorkflowContractTests.cs
.github/workflows/ci.yml
.github/workflows/core.yml
.github/workflows/linux-systemd.yml
.github/workflows/windows-service.yml
.github/workflows/virtual-scanner.yml
webassist/VERSION
```

Explicitly forbid contracts, policy, product source, installers, vendor, runtime docs and unrelated tests.

- [ ] **Step 4: Use the draft implementation PR as the first real Tier A acceptance**

Because the diff changes classifier/workflows, #6 must classify it full-cross-platform, so Tier A should run:

```text
requirements GREEN
core GREEN
Linux scanner smoke GREEN
Windows scanner smoke GREEN
ci-fast GREEN
ALT systemd NOT RUN/SKIPPED
Windows Service NOT RUN/SKIPPED
scanner-final NOT RUN/SKIPPED
ci-required not GREEN final verdict on draft
repo-guard GREEN separately
```

Record exact run/job IDs.

- [ ] **Step 5: Convert the same head draft -> ready**

No source commit. Verify a new ready-for-review run appears on the exact same head and now runs full Tier B:

```text
Tier A GREEN
ALT p11 systemd GREEN
Windows Service GREEN
Linux full virtual scanner GREEN
Windows full virtual scanner GREEN
ci-required GREEN
repo-guard GREEN
```

This is direct evidence that Ready transition starts final acceptance without a synthetic commit.

- [ ] **Step 6: Perform fixed-head merge**

Fresh-read `main`, PR, grant, #5, policy, compare, reviews and exact workflows. Require `behind_by=0`, exact-head repo-guard GREEN, exact-head Tier B/`ci-required` GREEN and no unresolved review blockers. Merge with `expected_head_sha`, then reread new `main`, VERSION and grant closure.

---

### Task 6: Run closed-unmerged behavioral probes and measure latency

**Files:**
- Probe-only marker files outside product source.
- Probe VERSION advances relative to newly accepted #5 main; probes are closed without merge.

**Interfaces:**
- Produces GitHub event-level evidence for draft, ready transition, synchronize invalidation and manual exact-head dispatch.

- [ ] **Step 1: Draft development probe**

Create a Linux-only draft probe using a harmless marker under `tests/linux-systemd/` plus mandatory VERSION change.

Expected:

```text
core GREEN
Linux scanner smoke GREEN
Windows smoke skipped
all Tier B heavy jobs skipped/not started
ci-fast GREEN
ci-required not a GREEN final verdict
```

- [ ] **Step 2: Draft -> ready exact-same-head probe**

Record probe SHA, convert to ready without commit, and prove new Tier B run uses exactly that SHA. Expect Linux-only final plan: ALT + Linux full scanner GREEN; Windows heavy jobs skipped; `ci-required` GREEN.

- [ ] **Step 3: Ready synchronize invalidation probe**

After final GREEN, add one new marker commit to the same probe branch while retaining a valid VERSION relation to main. Prove the old run becomes obsolete/cancelled and the new exact head receives fresh Tier A + Tier B + `ci-required`.

- [ ] **Step 4: Positive manual exact-head probe**

On an open probe PR branch, dispatch `ci.yml` with its `pr_number`. Verify selected workflow ref SHA equals current PR head and full applicable Tier B reaches `ci-required=SUCCESS`.

- [ ] **Step 5: Negative manual mismatch probe**

Dispatch the same `pr_number` while selecting `main` (or another known non-head ref). Expected: requirements fails at exact-head comparison before platform jobs; no accidental GREEN `ci-required` is possible.

- [ ] **Step 6: Close all probes without merge**

Fresh-read each probe before closing; assert `merged=false` afterward. No probe VERSION or marker enters `main`.

- [ ] **Step 7: Record initial latency metrics against #97 baseline**

For every completed probe capture event time, requirements completion, `ci-fast`, and `ci-required` when applicable. Compare to baseline:

```text
push -> first useful result p50 = 41 s
push -> ci-required p50 = 147.5 s
full-head runner wall-time p50 ≈ 7.31 min
```

Record observed Tier A savings without claiming statistical p50/p95 until at least 10 representative development heads exist.

---

### Task 7: Finish #5 only after the external GitHub merge boundary exists

**Files:**
- No repository code unless #3 implementation requires its own separately approved transaction.

**Interfaces:**
- Consumes actual GitHub ruleset/branch-protection state from #3/#25.
- Produces final #5 completion verdict.

- [ ] **Step 1: After workflow acceptance, update #5 with the implementation/probe evidence**

State clearly that workflow Tier A/Tier B mechanics are accepted but merge-boundary enforcement is still external if `main protected=false` / no ruleset exists.

- [ ] **Step 2: Do not close #5 while #3/#25 remain unenforced**

Required GitHub boundary:

```text
PR required for main
repo-guard required separately
ci-required required
up-to-date/exact-head merge policy enabled
ordinary direct push/bypass disabled
```

- [ ] **Step 3: Once #3/#25 are physically enforced, run one final ready-head verification**

Use a non-merge probe to confirm GitHub reports both required checks on the exact head and that the PR cannot become mergeable while `ci-required` is absent/failing.

- [ ] **Step 4: Close #5 as completed only after fresh final evidence**

Final comment must cite implementation merge SHA, accepted VERSION, Tier A/Tier B probe run IDs, manual mismatch RED evidence, latency observations, and actual branch/ruleset enforcement state.
