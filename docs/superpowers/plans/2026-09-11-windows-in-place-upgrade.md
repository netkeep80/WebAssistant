# Windows In-Place Upgrade Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `WebAssistant-win-x64-<VERSION>.exe` upgrade a running older WebAssistant in place without manual uninstall/service stop or `Files In Use`, while making normal Windows service shutdown deterministically terminate every WebAssistant-owned `NAPS2.Worker`.

**Architecture:** Use a two-generation solution. A Burn-chained elevated `UpgradePreflight` helper safely cleans up the already-installed legacy runtime before MSI validation, while a new immutable repo-owned NAPS2 SDK package plus a pre-`ApplicationStopped` hosted shutdown participant fixes worker ownership for the candidate and future versions. Acceptance starts from the exact staged `v0.3.21` bytes and exercises upgrade, repair, downgrade rejection, restart and uninstall without rebuilding either accepted installer.

**Tech Stack:** .NET 10, ASP.NET Core Generic Host/Windows Service, WiX Toolset 7 Burn/MSI, C# Win32 P/Invoke, xUnit, PowerShell 7, GitHub Actions, repository-owned NAPS2 SDK fork pinned to `cyanfish/naps2@450cba65aaffe6387041050a573051a64cd80fe9`.

**Spec:** `docs/superpowers/specs/2026-09-11-windows-in-place-upgrade-design.md`

## Global Constraints

- GitHub is source of truth. Work through issue #191, branch, commits, PR and exact-head CI; no direct `main` commits.
- Accepted `webassistant-contract/v0.2` and `webassistant-conformance/v0.2` remain untouched. #191 is not an HTTP/scanner-semantic transaction.
- Do not change scanner IDs, source-selection policy, public routes, request/response shapes, #164 extended settings, localization, GitLab/Linux distribution redesign, PDF or Release publication.
- Do not mutate or rebuild staged `v0.3.21` installer bytes.
- Historical Windows anchor is fixed:
  - source SHA `77a5c66c431c746d2be2f283640c7951730911eb`
  - release id `386331973`
  - EXE asset id `555125536`
  - SHA sidecar asset id `555125537`
  - provenance asset id `555125538`
  - EXE SHA-256 `68fe8a0145721c13f14dad4a4d3fea333c9ccb240b036d8869aa2152af7b7271`
- Keep `webassist/VERSION = 0.3.22` throughout tests-only RED and functional GREEN. Bump exactly once, expected to `0.3.23`, only after the full functional transaction is GREEN. If another accepted transition consumes that version first, use the next monotonic version.
- Do not add legacy WiX `UpgradeCode`/`MajorUpgrade` authoring merely to satisfy an old hypothesis. Preserve stable `Package Id="netkeep80.WebAssistant"` and `Bundle Id="netkeep80.WebAssistant.Bundle"`.
- Never terminate workers by image name alone. No global `taskkill /IM NAPS2.Worker.exe`, no broad `CloseApplication` rule.
- Every production behavior block follows test-first RED -> GREEN. Capture exact commit SHA for meaningful RED and GREEN evidence.
- Keep the current isolated `webassist` export-root build architecture and build each candidate installer once per acceptance run.

---

## Task 1: Encode the real `v0.3.21 -> current candidate` failure as executable RED

**Files:**
- Create: `tests/windows-service/run-upgrade-acceptance.ps1`
- Modify: `.github/workflows/installer-acceptance.yml`
- Modify: `.github/workflows/windows-service.yml`
- Modify: `tests/core/InstallerArtifactContractTests.cs`
- Modify: `tests/core/IsolatedExportRootAcceptanceTests.cs`

- [ ] **1.1 Add tests for immutable historical-input wiring before production changes**

Extend `InstallerArtifactContractTests` to require the Windows acceptance workflow/harness to pin the exact historical identity above. The test must reject a harness that rebuilds `v0.3.21`, accepts an arbitrary old artifact, or omits SHA/provenance verification.

Add assertions equivalent to:

```csharp
Assert.Contains("555125536", workflow, StringComparison.Ordinal);
Assert.Contains("555125537", workflow, StringComparison.Ordinal);
Assert.Contains("555125538", workflow, StringComparison.Ordinal);
Assert.Contains("68fe8a0145721c13f14dad4a4d3fea333c9ccb240b036d8869aa2152af7b7271", upgradeHarness, StringComparison.Ordinal);
Assert.DoesNotContain("dotnet publish", upgradeHarness, StringComparison.OrdinalIgnoreCase);
Assert.DoesNotContain("package.bat", upgradeHarness, StringComparison.OrdinalIgnoreCase);
```

Also require `run-upgrade-acceptance.ps1` from `IsolatedExportRootAcceptanceTests` as a consumer of exact already-produced bytes, never a producer.

- [ ] **1.2 Create the tests-only upgrade harness**

`run-upgrade-acceptance.ps1` must accept candidate EXE/SHA/provenance plus historical EXE/SHA/provenance. Implement reusable verification that proves:

```text
historical filename = WebAssistant-win-x64-0.3.21.exe
historical SHA-256  = 68fe8a0145721c13f14dad4a4d3fea333c9ccb240b036d8869aa2152af7b7271
historical provenance.version = 0.3.21
historical provenance.sourceSha = 77a5c66c431c746d2be2f283640c7951730911eb
candidate filename/version/SHA/provenance are mutually consistent
```

The harness must then:

```text
install exact 0.3.21
assert WebAssistant service Running
GET /v1/scanners to materialize WindowsScanAdapter
capture service PID
capture at least one NAPS2.Worker whose ParentProcessId == service PID and path is under Program Files\WebAssistant
DO NOT Stop-Service
DO NOT uninstall 0.3.21
run candidate bundle directly with a Burn log
expect successful upgrade
assert captured old process identities are gone
assert exactly one WebAssistant Installed Apps entry at candidate version
assert installed WebAssistant.exe ProductVersion == candidate version
assert service Running
assert /v1/health and /v1/scanners respond
restart service and repeat checks
run candidate same-version repair/maintenance and repeat checks
run exact 0.3.21 as downgrade attempt; require rejection while candidate remains intact
uninstall candidate
assert service and Program Files payload removed
assert ProgramData\WebAssistant\logs and \data remain
assert no WebAssistant-owned NAPS2 worker remains
```

PowerShell/CIM process inspection is allowed in acceptance code. Product code must not depend on PowerShell.

- [ ] **1.3 Download historical draft-release assets read-only in both Windows acceptance workflows**

Use authenticated GitHub API asset download with `${{ github.token }}` and exact asset ids. Download into `$RUNNER_TEMP`, never into the source tree. Verify names and hashes again in the harness before execution.

Do not use the expiring Actions artifact as the canonical old-version source while the durable draft Release exists.

- [ ] **1.4 Run core tests locally/CI and commit tests-only RED**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
```

Then run the exact Windows acceptance path on the tests-only head. Expected result: the new old-to-new scenario is RED on the current implementation because there is no pre-MSI legacy ownership barrier and no deterministic worker cleanup.

Commit only tests/workflow/harness changes:

```text
test: reproduce live-worker Windows upgrade failure
```

Record exact RED head and the failing step/log in #191. Do not change product code or VERSION in this commit.

---

## Task 2: Build a testable fail-closed `UpgradePreflight` ownership engine

**Files:**
- Create: `webassist/build/windows/upgrade-preflight/WebAssistant.UpgradePreflight.csproj`
- Create: `webassist/build/windows/upgrade-preflight/Program.cs`
- Create: `webassist/build/windows/upgrade-preflight/UpgradePreflight.cs`
- Create: `webassist/build/windows/upgrade-preflight/UpgradeEnvironment.cs`
- Create: `webassist/build/windows/upgrade-preflight/WindowsUpgradeEnvironment.cs`
- Create: `webassist/build/windows/upgrade-preflight/ProcessIdentity.cs`
- Modify: `tests/core/WebAssistant.CoreTests.csproj`
- Create: `tests/core/WindowsUpgradePreflightTests.cs`

- [ ] **2.1 Add pure ownership/orchestration tests first**

Make the helper project target plain `net10.0` so its orchestration can be referenced by the cross-platform core test project; guard actual Win32 execution at runtime. Add `InternalsVisibleTo` for `WebAssistant.CoreTests`.

Define a narrow injectable environment boundary conceptually like:

```csharp
internal interface IUpgradeEnvironment
{
    ServiceSnapshot? TryGetWebAssistantService();
    IReadOnlyList<ProcessIdentity> SnapshotProcesses();
    Task RequestServiceStopAsync(CancellationToken cancellationToken);
    Task<bool> WaitForServiceStoppedAsync(TimeSpan timeout, CancellationToken cancellationToken);
    bool IsAlive(ProcessIdentity process);
    Task<bool> WaitForExitAsync(ProcessIdentity process, TimeSpan timeout, CancellationToken cancellationToken);
    void Terminate(ProcessIdentity process);
}
```

Tests must cover:

```text
service absent -> success/no-op
running canonical service -> orderly stop requested
worker exact allowed path + parent PID + compatible start time -> captured as owned
same filename outside install root -> untouched
expected-looking path + wrong parent PID -> untouched
PID reused after recorded parent exits -> never added to ownership set
worker started after first snapshot but before exact parent exit -> captured
already stopped service with unprovable orphan -> no blind kill; fail closed when blocking state is ambiguous
service stop timeout -> failure
owned worker exits during grace -> no Terminate
owned worker survives grace -> only exact captured identity Terminate
termination fails / worker remains -> failure
repeated execution when service absent/stopped cleanly -> idempotent success
```

- [ ] **2.2 Run the focused tests and confirm RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter WindowsUpgradePreflightTests
```

The tests must fail because the helper/orchestrator does not exist yet.

- [ ] **2.3 Implement the minimum orchestration to make the pure tests GREEN**

Use immutable process identity containing at least:

```csharp
internal sealed record ProcessIdentity(
    int ProcessId,
    int ParentProcessId,
    string ImagePath,
    DateTimeOffset StartTimeUtc);
```

Keep an open Windows process handle for the exact service process in `WindowsUpgradeEnvironment` during the observation window when practical; PID/path/start-time remain the logical identity exposed to policy tests.

Allowed worker paths are derived only from the canonical installed service executable directory:

```text
<installRoot>\NAPS2.Worker.exe
<installRoot>\lib\NAPS2.Worker.exe
```

Comparison is canonical full-path, Windows case-insensitive. Do not scan arbitrary roots.

- [ ] **2.4 Implement Win32 environment only after pure logic is GREEN**

Use narrow P/Invoke for:

```text
OpenSCManager / OpenService
QueryServiceConfig
QueryServiceStatusEx
ControlService
CreateToolhelp32Snapshot / Process32First / Process32Next
OpenProcess
GetProcessTimes
QueryFullProcessImageName
WaitForSingleObject
TerminateProcess
CloseHandle/SafeHandle ownership
```

`Program.cs` returns `0` only after old service-process identity and all captured proven-owned workers are gone. Non-zero means hard failure. Log only lifecycle facts; do not dump environment/command lines/scanner data.

Use bounded policy constants in one place, initially:

```text
service stop/process convergence: 15 s
worker graceful-exit grace:        3 s
post-terminate exit wait:          2 s
```

Tests must use fake time/environment; do not sleep for these durations in unit tests.

- [ ] **2.5 Verify and commit the helper engine**

Run focused tests and full core. Commit:

```text
feat: add fail-closed Windows upgrade preflight
```

Do not chain it into Burn yet; this task proves the engine independently.

---

## Task 3: Chain preflight before MSI and preserve canonical build isolation

**Files:**
- Modify: `webassist/build/windows/package.ps1`
- Modify: `webassist/build/windows/installer/WebAssistant.Bundle.wixproj`
- Modify: `webassist/build/windows/installer/Bundle.wxs`
- Modify: `tests/core/InstallerArtifactContractTests.cs`
- Modify: `tests/core/SystemServiceProductTests.cs`
- Modify: `tests/core/IsolatedExportRootAcceptanceTests.cs`
- Modify: `.github/workflows/build-installers.yml`
- Modify: `.github/workflows/windows-service.yml`

- [ ] **3.1 Add structural Burn/producer tests first**

Tests must require:

```text
UpgradePreflight ExePackage appears before WebAssistantMsi in Chain
PerMachine=yes
Permanent=yes
Vital=yes
SourceFile uses $(var.PreflightPath)
RepairArguments is present so repair invokes preflight
bundle project defines PreflightPath
package.ps1 publishes helper self-contained win-x64 and passes exact path to bundle build
stable Package/Bundle Id values remain unchanged
ServiceControl remains present
no CloseApplication
no taskkill
no second ARP product identity
```

The ExePackage should use a deterministic always-run detection strategy for install/upgrade/repair and be permanent so uninstall does not invoke cleanup. With WiX 7 authoring, use the smallest valid form proven by the build, expected to be conceptually:

```xml
<ExePackage
    Id="UpgradePreflight"
    SourceFile="$(var.PreflightPath)"
    DetectCondition="0"
    PerMachine="yes"
    Permanent="yes"
    Vital="yes"
    RepairArguments="" />
```

Place it immediately before `WebAssistantMsi`. If WiX 7 rejects an attribute combination, preserve the observable planning semantics from the spec rather than weakening the tests.

- [ ] **3.2 Confirm RED before authoring changes**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter "FullyQualifiedName~InstallerArtifactContractTests|FullyQualifiedName~SystemServiceProductTests|FullyQualifiedName~IsolatedExportRootAcceptanceTests"
```

- [ ] **3.3 Publish helper from the autonomous `webassist` root**

In `package.ps1`, before bundle build:

```text
dotnet publish build/windows/upgrade-preflight/WebAssistant.UpgradePreflight.csproj
  -c Release
  -r win-x64
  --self-contained true
  /p:PublishSingleFile=true
  /p:PublishTrimmed=true
```

Stage the resulting EXE under the package work directory, not Program Files payload. Pass `PreflightPath` to `WebAssistant.Bundle.wixproj`. The helper itself must not be included by the MSI recursive payload glob.

- [ ] **3.4 Update isolated-export workflow guards**

Both Windows build workflows must assert that the copied `webassist` export root contains the helper project before running `package.bat`. They must never reach back to `$GITHUB_WORKSPACE` for helper sources after the export copy.

- [ ] **3.5 Build the real bundle and make structural tests GREEN**

On Windows run the canonical producer from an isolated copy. Verify:

```text
one versioned canonical EXE produced
sidecars/provenance still match it
one WebAssistant ARP entry on install
fresh install succeeds although no old service exists
```

Commit:

```text
feat: run Windows upgrade preflight before MSI
```

At this point re-run the old-to-new acceptance. It may become GREEN for the legacy transition, but service-stop lifetime tests for the new runtime are not yet complete; do not declare #191 done.

---

## Task 4: Create immutable NAPS2 `.3` package with deterministic worker shutdown

**Files:**
- Modify first for RED: `tests/core/DependencyOwnershipTests.cs`
- Modify: `webassist/vendor/naps2/rebuild-fixed-sdk.sh`
- Modify: `webassist/vendor/naps2/README.md`
- Create: `webassist/vendor/nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.3.450cba65.nupkg`
- Modify: `webassist/src/WebAssistant/WebAssistant.csproj`
- Modify: `tests/core/WindowsScanAdapterTests.cs`

- [ ] **4.1 Change dependency tests to require `.3` before producing it**

Pin:

```text
WebAssistant.NAPS2.Sdk 1.3.0-webassistant.3.450cba65
upstream 450cba65aaffe6387041050a573051a64cd80fe9
```

Keep the nullable `PaperSourceCaps.FeederHasPaper` assertion so `.2` functionality cannot regress. Add package/source evidence assertions for worker lifecycle concepts (`WorkerContext`, `WorkerFactory`, `ScanningContext` patch lineage) without deleting the immutable `.2` history.

Run `DependencyOwnershipTests` and capture RED because `.3` is absent/product still references `.2`.

- [ ] **4.2 Patch upstream WorkerContext with one shared stop task**

Extend `rebuild-fixed-sdk.sh` using the existing fail-closed exact-layout `replace_exact` mechanism. `WorkerContext.Stop()` must become idempotent-awaitable:

```text
first caller creates one stop Task
all later callers receive/await that same Task
normal stop asks worker service to exit
wait bounded for Process exit
if still alive, kill this exact Process
wait for actual exit after kill
```

Do not leave the current `_stopped` shortcut that lets a later shutdown caller return while an earlier stop is still running.

- [ ] **4.3 Patch WorkerFactory into the complete ownership authority**

Add synchronized state for:

```text
registered WorkerContext instances
in-flight worker-start Tasks
shutdown started flag
```

Required behavior:

```text
every process/context is registered before it can escape to a queue/caller
shutdown atomically rejects new usable workers
starts already in flight either fail before publication or register and immediately join shutdown
shutdown awaits all start tasks
shutdown calls shared Stop() on all registered contexts in parallel
shutdown waits until every registered Process has exited
shutdown is idempotent
```

Retire registry entries only after exit has been observed.

- [ ] **4.4 Connect ScanningContext.Dispose to the factory barrier**

For contexts initialized with the worker factory, dispose must synchronously bridge to the deterministic factory shutdown before disposing normal context resources. Use a bounded total shutdown policy compatible with WebAssistant host timeout; worker stops run in parallel, not 60 seconds serially per worker.

Target internal bound for WebAssistant-owned package:

```text
graceful worker stop: <= 10 s total
post-kill confirmation: <= 2 s
```

- [ ] **4.5 Rebuild `.3`, record exact SHA, and update provenance/product reference**

From `webassist`:

```bash
./vendor/naps2/rebuild-fixed-sdk.sh
sha256sum vendor/nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.3.450cba65.nupkg
```

Put the exact resulting SHA into `DependencyOwnershipTests`. Update `README.md` to state that `.3` contains all `.2` paper-state behavior plus deterministic worker lifetime ownership. Change only the product package reference from `.2` to `.3`; keep `.2` immutable in repository history.

- [ ] **4.6 Add Windows virtual-worker lifecycle evidence**

Extend `WindowsScanAdapterTests` under `Category=WindowsVirtualScanner` to materialize workers, dispose the adapter, and prove package-owned worker processes spawned by that adapter are gone after disposal. Use process identity/path/parent evidence; do not kill test leftovers as the success mechanism.

Run:

```text
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
dotnet test ... --filter Category=WindowsVirtualScanner
```

Commit:

```text
fix: make repo-owned NAPS2 worker shutdown deterministic
```

---

## Task 5: Add an explicit pre-`ApplicationStopped` Windows scanner shutdown barrier without eager worker creation

**Files:**
- Create: `webassist/src/WebAssistant/Scanning/WindowsScanAdapterHolder.cs`
- Create: `webassist/src/WebAssistant/Scanning/WindowsScannerShutdownHostedService.cs`
- Modify: `webassist/src/WebAssistant/Scanning/WindowsScanAdapter.cs`
- Modify: `webassist/src/WebAssistant/Program.cs`
- Create: `tests/core/WindowsScannerLifetimeTests.cs`
- Modify: `tests/core/SystemServiceProductTests.cs`

- [ ] **5.1 Write lifetime tests first**

Tests must prove:

```text
holder starts with no adapter
hosted shutdown without prior scanner use does not construct adapter
first GetOrCreate constructs exactly one adapter
concurrent/repeated GetOrCreate returns the same adapter
ShutdownIfCreated is idempotent
ShutdownIfCreated disposes an already-created adapter exactly once
GetOrCreate after shutdown begins fails closed
Program registers holder + IScanAdapter factory + WindowsScannerShutdownHostedService only on Windows
HostOptions.ShutdownTimeout on Windows exceeds the internal NAPS2 shutdown bound
```

Use a factory delegate/fake disposable adapter in holder tests so Linux core tests do not create real Windows scanner workers.

- [ ] **5.2 Confirm focused RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter WindowsScannerLifetimeTests
```

- [ ] **5.3 Implement lazy holder**

`WindowsScanAdapterHolder` owns creation/shutdown state. It must not resolve/create `WindowsScanAdapter` in its constructor. Its public-to-DI behavior is conceptually:

```csharp
internal WindowsScanAdapter GetOrCreate();
internal void ShutdownIfCreated();
```

Use one lock/state transition. Once shutdown begins, no new adapter may be created.

- [ ] **5.4 Implement hosted shutdown participant**

Register on Windows:

```csharp
builder.Services.AddSingleton<WindowsScanAdapterHolder>();
builder.Services.AddSingleton<IScanAdapter>(sp =>
    sp.GetRequiredService<WindowsScanAdapterHolder>().GetOrCreate());
builder.Services.AddHostedService<WindowsScannerShutdownHostedService>();
builder.Services.Configure<HostOptions>(options =>
    options.ShutdownTimeout = TimeSpan.FromSeconds(20));
```

`WindowsScannerShutdownHostedService.StopAsync` calls `ShutdownIfCreated()` before Generic Host emits `ApplicationStopped`. `WindowsScanAdapter.Dispose()` remains idempotent so later DI disposal is harmless fallback.

Do not eagerly inject `IScanAdapter` into the hosted service; inject only the holder.

- [ ] **5.5 Verify GREEN and commit**

Run core plus Windows virtual scanner tests. Commit:

```text
fix: stop Windows scanner workers before service reports stopped
```

---

## Task 6: Strengthen Windows service acceptance to prove the new runtime invariant directly

**Files:**
- Modify: `tests/windows-service/run-service-acceptance.ps1`
- Modify: `.github/workflows/windows-service.yml`
- Modify: `tests/core/SystemServiceProductTests.cs`

- [ ] **6.1 Extend acceptance before modifying production further**

Before `Stop-Service`, call `GET /v1/scanners` so the lazy adapter is guaranteed to be materialized. Capture the current service PID and package-owned `NAPS2.Worker` identities using elevated CIM/process inspection.

Require at least one worker under the installed WebAssistant root with `ParentProcessId == WebAssistant service PID`.

- [ ] **6.2 After `Stop-Service`, assert the strong invariant**

After SCM reaches `Stopped`, require:

```text
old WebAssistant service process gone
all captured package-owned worker processes gone
no current worker under install root attributable to the stopped service
listener gone
```

Then start/restart as before and repeat health/scanner checks.

This is the direct regression test for:

```text
SCM says Stopped => WebAssistant-owned NAPS2 worker is gone
```

- [ ] **6.3 Run Windows Service acceptance and commit**

Expected result after Tasks 4-5: GREEN. If RED, use `superpowers:systematic-debugging`; do not loosen the assertion.

Commit:

```text
test: prove worker shutdown on Windows service stop
```

---

## Task 7: Make the complete installer acceptance matrix GREEN

**Files:**
- Modify as needed only within accepted design: `tests/windows-service/run-upgrade-acceptance.ps1`
- Modify: `tests/windows-service/run-installer-acceptance.ps1`
- Modify: `.github/workflows/installer-acceptance.yml`
- Modify: `.github/workflows/windows-service.yml`
- Modify: `tests/core/InstallerArtifactContractTests.cs`

- [ ] **7.1 Preserve clean-install acceptance and add upgrade acceptance beside it**

The Windows acceptance job should consume the single candidate artifact produced by the existing producer and run both:

```text
clean install -> lifecycle -> uninstall
exact v0.3.21 -> candidate live-worker upgrade -> repair -> downgrade rejection -> uninstall
```

Do not rebuild the candidate between these scenarios.

- [ ] **7.2 Prove preflight precedes MSI replacement**

Capture Burn package logs. Require explicit `UpgradePreflight` success evidence before the candidate MSI executes. Fail on unresolved `FilesInUse` / `MsiRMFilesInUse` / retry-ignore-required state. Do not merely infer success from quiet mode.

- [ ] **7.3 Prove version and ARP behavior**

After upgrade and after same-version repair:

```text
exactly one WebAssistant Installed Apps bundle entry
DisplayVersion == candidate version
installed WebAssistant.exe ProductVersion == candidate version
service Running
health OK
scanner endpoint reachable
```

- [ ] **7.4 Prove downgrade rejection is non-destructive**

Run exact historical `0.3.21` bundle over candidate. Require the older bundle to reject/non-successfully plan the downgrade. Immediately prove:

```text
candidate ARP entry remains
candidate executable version remains
service remains/rereturns Running
health remains OK
```

Never clean up the candidate by running the old uninstaller.

- [ ] **7.5 Prove state ownership after final uninstall**

Use sentinel files under `ProgramData\WebAssistant\logs` and `data` before upgrade/uninstall. Final candidate uninstall must remove service + Program Files but retain both state roots/sentinels.

- [ ] **7.6 Run the full Windows acceptance until GREEN and commit any harness-only corrections**

If product changes are required, return to the owning Task 2/3/4/5 tests first rather than patching around a failure in PowerShell.

Commit harness corrections separately:

```text
test: cover Windows upgrade repair and downgrade lifecycle
```

---

## Task 8: Functional GREEN gate while VERSION is still `0.3.22`

**Files:** no new feature scope; only fixes justified by failing accepted tests.

- [ ] **8.1 Verify VERSION has not moved**

```bash
cat webassist/VERSION
# must still be 0.3.22
```

- [ ] **8.2 Run full core**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
```

- [ ] **8.3 Run repo-guard on exact head**

Use the repository's canonical repo-guard invocation for the PR exact head. Any policy failure must be resolved without relaxing unrelated policy.

- [ ] **8.4 Run required Windows installer/service and scanner evidence**

Require GREEN for:

```text
canonical Windows build once
clean installer acceptance
v0.3.21 live-worker upgrade acceptance
same-version repair
old-version downgrade rejection
Windows service stop/restart lifecycle
Windows TWAIN virtual scanner
Linux SANE/core regressions selected by existing change classifier because vendor SDK changed
```

Because `webassist/vendor/**` is shared dependency scope, accept the current fail-closed classifier running cross-platform evidence; do not weaken it to save minutes.

- [ ] **8.5 Record the exact functional-GREEN SHA in #191**

At this point implementation is functionally complete but not yet final candidate identity. Only now is a VERSION transition allowed.

---

## Task 9: Bump VERSION exactly once and prove the final `0.3.23` candidate bytes

**Files:**
- Modify: `webassist/VERSION`
- Modify version-coupled fixtures only if an existing repository invariant requires them; do not rewrite historical evidence.

- [ ] **9.1 Bump `0.3.22 -> 0.3.23` in one dedicated transition**

If `main` advanced before implementation branch finalization, rebase/update first and choose the next free monotonic version. Never reuse a consumed version.

Commit:

```text
chore: bump WebAssistant to 0.3.23
```

- [ ] **9.2 Re-run full exact-head CI against final versioned bytes**

Build canonical Windows installer once and require all final checks on the same exact head/artifact identity:

```text
core GREEN
repo-guard GREEN
Windows installer acceptance GREEN
Windows service acceptance GREEN where selected
virtual scanner GREEN
cross-platform regressions GREEN where selected
```

The final upgrade test still starts from immutable `0.3.21`; only the candidate side changes to the exact final versioned EXE.

- [ ] **9.3 Verify artifact immutability**

Candidate EXE SHA before acceptance must equal SHA sidecar, provenance SHA and SHA after acceptance. Consumers must not modify/repack/rebuild it.

---

## Task 10: Completion evidence, issue update and PR handoff

**Files:** no production changes unless verification exposes a real defect.

- [ ] **10.1 Use `superpowers:verification-before-completion`**

Before claiming success, verify the exact final head and collect concrete command/workflow results. Do not summarize from stale earlier runs.

- [ ] **10.2 Update #191 with concise evidence**

Record:

```text
final source SHA
final VERSION
candidate EXE SHA
exact v0.3.21 source/SHA used
legacy live worker reproduced before upgrade
preflight ownership cleanup proof
no Files In Use decision required
service/health/scanner post-upgrade proof
same-version repair proof
downgrade rejection proof
ProgramData preservation proof
Windows service stop => no owned worker proof
required CI run ids/results
```

Do not close #191 until the acceptance checklist is actually met.

- [ ] **10.3 Open/update the implementation PR and use `superpowers:requesting-code-review`**

PR scope must contain only #191 design/plan/implementation/evidence changes. Explicitly state that accepted v0.2 and HTTP scanner semantics are unchanged.

- [ ] **10.4 Use `superpowers:finishing-a-development-branch` only after all exact-head checks and review are complete**

Do not merge merely because individual tests passed. Preserve normal user authorization/review expectations for the final merge.

## Final Acceptance Summary

The implementation is complete only when all of these are simultaneously true on the exact final head:

```text
real staged v0.3.21 + running WebAssistant + owned NAPS2.Worker
    -> run newer canonical EXE directly
    -> no manual stop/uninstall/Retry/Ignore
    -> old service/worker gone before payload replacement
    -> one newer WebAssistant installation
    -> service Running
    -> health/scanners GREEN
    -> restart GREEN
    -> same-version repair GREEN
    -> v0.3.21 downgrade rejected without damaging newer install
    -> uninstall removes package/service but preserves ProgramData state

and independently:

Stop-Service WebAssistant on the new runtime
    -> SCM reaches Stopped only after all WebAssistant-owned NAPS2 workers are gone
```
