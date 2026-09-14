# Windows Scanner Discovery Regression Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve usable scanner endpoints when one Windows scanner capability probe fails and make WIA/TWAIN discovery hangs diagnosable from the normal WebAssistant log.

**Architecture:** Keep the existing independent WIA + TWAIN discovery contract and public warning shape. Split backend enumeration failure from per-device capability failure, so a failed `GetCaps` skips only that endpoint and records the existing backend `enumerationFailed` warning without discarding already valid endpoints. Inject `ILogger<WindowsScanAdapter>` lazily through `WindowsScanAdapterHolder` and emit stage/duration/outcome diagnostics for `GetDeviceList`, per-device `GetCaps`, total backend discovery, and adapter shutdown; do not invent a timeout until real stage evidence identifies the blocking call.

**Tech Stack:** .NET 10, ASP.NET Core, NAPS2 SDK/Win32 worker, xUnit, Microsoft.Extensions.Logging.

**Spec:** `docs/superpowers/specs/2026-09-10-scanner-api-stable-id-auto-design.md` plus reopened GitHub Issue #163 real Windows pilot evidence.

## Global Constraints

- Current base is exact `main=ee22fcee400e17bca222c5be3e157e3eccd2e3d1`, `webassist/VERSION=0.3.30`.
- Accepted contract/conformance v0.2 remain immutable; this regression fix does not promote candidate v0.3.
- WIA and TWAIN remain independently enumerated; one backend failure must not hide the other backend.
- No arbitrary discovery timeout in this transaction before stage-level physical evidence identifies the blocking call.
- Do not log raw backend-native scanner IDs; use backend, ordinal and public opaque `scannerId` where device correlation is required.
- Exception diagnostics must include type, HRESULT and message in the technical log.
- Every production behavior change follows RED -> observed failure -> minimal GREEN.
- Filesystem implementation is out of scope except for conflict/rebase handling before merge.

---

### Task 1: Isolate per-device capability failures

**Files:**
- Create: `tests/core/WindowsScannerDiscoveryRegressionTests.cs`
- Modify: `webassist/src/WebAssistant/Scanning/WindowsScanAdapter.cs`

**Interfaces:**
- Consumes: existing `WindowsScanAdapter.DiscoverAsync(...)` and `ScannerDiscoveryResult`.
- Produces: discovery behavior where a `GetCaps` exception skips only that device, preserves other endpoints from the same backend, marks the backend available after successful `GetDeviceList`, and records one `enumerationFailed` warning.

- [ ] **Step 1: Write the failing regression test**

Add a test with two TWAIN devices. Return valid caps for the first and throw `InvalidOperationException` for the second. Assert the first TWAIN endpoint survives, discovery remains available, and one TWAIN `enumerationFailed` warning is returned.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter FullyQualifiedName~WindowsScannerDiscoveryRegressionTests.Discovery_OneDeviceCapsFailurePreservesOtherDeviceFromSameBackend
```

Expected on the current implementation: FAIL because the backend-level catch discards the already-normalized TWAIN endpoint and does not count that backend as successful.

- [ ] **Step 3: Implement minimal per-device isolation**

Keep `GetDeviceList` inside a backend-level try/catch. After successful enumeration, process each non-ambiguous device in its own capability try/catch. On per-device failure, append/deduplicate the existing `enumerationFailed` warning for that backend and continue. Add successful normalized devices directly to the backend result and count the backend as successful once enumeration itself succeeded.

- [ ] **Step 4: Run focused + existing scanner adapter tests**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter "FullyQualifiedName~WindowsScannerDiscoveryRegressionTests|FullyQualifiedName~WindowsScanAdapterTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

Commit message:

```text
#163: isolate scanner capability probe failures
```

### Task 2: Add stage-level scanner discovery diagnostics

**Files:**
- Modify: `tests/core/WindowsScannerDiscoveryRegressionTests.cs`
- Modify: `webassist/src/WebAssistant/Scanning/WindowsScanAdapter.cs`
- Modify: `webassist/src/WebAssistant/Scanning/WindowsScanAdapterHolder.cs`
- Modify: `webassist/src/WebAssistant/Program.cs`

**Interfaces:**
- Consumes: `ILogger<WindowsScanAdapter>` from the ASP.NET Core logging pipeline.
- Produces: technical log entries for `GetDeviceList` start/success/failure, `GetCaps` start/success/failure per endpoint, backend completion, discovery completion, and scanner-context disposal duration.

- [ ] **Step 1: Write failing diagnostics tests**

Add a small in-memory `ILogger<WindowsScanAdapter>` test sink. Assert that a simulated TWAIN capability failure produces log messages containing `backend=twain`, `stage=getCaps`, public opaque scanner ID, duration, exception type, HRESULT and message, while no explicitly logged field contains the raw native ID. Add a successful enumeration assertion containing `stage=getDeviceList`, device count and duration.

- [ ] **Step 2: Run focused diagnostics tests and verify RED**

Run the regression test class only. Expected: compile/test failure because `DiscoverAsync` does not yet accept a logger and emits no stage diagnostics.

- [ ] **Step 3: Implement minimal structured diagnostics**

Add optional logger plumbing without changing the public HTTP contract. Use `Stopwatch` around backend enumeration, capability probes and total backend work. Emit Information for start/success, Warning for failures, and include exception `GetType().FullName`, `HResult` formatted as hex, and `Message`. Use only the public `scannerId` for device correlation.

- [ ] **Step 4: Wire the production logger lazily**

Construct `WindowsScanAdapter` from `WindowsScanAdapterHolder` through the existing DI registration using `ILogger<WindowsScanAdapter>`, preserving lazy scanner-context creation and current shutdown ownership.

- [ ] **Step 5: Verify scanner and logging tests**

Run focused regression, existing `WindowsScanAdapterTests`, `WindowsScannerLifetimeTests`, and `RequestLoggingMiddlewareTests`. Expected: PASS.

- [ ] **Step 6: Commit**

Commit message:

```text
#163: add Windows scanner discovery stage diagnostics
```

### Task 3: Version, documentation and CI/physical handoff

**Files:**
- Modify: `webassist/VERSION`
- Modify: `webassist/docs/api.md` only if operational warning/log wording is documented there and requires synchronization.
- Modify: candidate conformance only if existing repository policy requires evidence-path synchronization; accepted v0.2 files remain untouched.

**Interfaces:**
- Produces: one testable Windows artifact whose log can localize the real ~50 second TWAIN stall to `GetDeviceList` or one endpoint's `GetCaps`.

- [ ] **Step 1: Run the full core test suite before version transition**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj
```

Expected: all tests PASS.

- [ ] **Step 2: Apply the repository-required single VERSION transition**

Advance `webassist/VERSION` exactly once according to the current product version policy after implementation is green.

- [ ] **Step 3: Run version/policy-sensitive tests**

Run the full core suite again and repository governance/CI through the PR.

- [ ] **Step 4: Build canonical Windows installer through normal CI**

Use the repository's canonical Windows producer; do not create a parallel packaging path.

- [ ] **Step 5: Repeat physical evidence on Vadim's machine**

On a fresh service process invoke `/v1/scanners` once and collect the normal `/v1/diag/logs` excerpt. The log must identify exactly which TWAIN stage consumes ~40–50 seconds and whether one specific endpoint capability probe fails.

- [ ] **Step 6: Decide the root-cause follow-up from evidence**

If `GetDeviceList(Twain)` is the blocker, investigate/fix the NAPS2 worker/DSM enumeration boundary. If only one `GetCaps` is the blocker, isolate/fix that data-source capability path. Do not infer either before the physical log proves it.

- [ ] **Step 7: Commit and Ready review**

Commit the version/evidence synchronization, move the PR Ready only after exact-head CI/repo-guard is green, then perform the normal merge gate.
