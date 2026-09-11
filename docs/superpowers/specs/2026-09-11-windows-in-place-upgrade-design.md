# Deterministic Windows in-place upgrade design

Issue: #191  
Parent roadmap: #160  
Windows installer baseline: #155  
Scanner/API companion P0: #163  
Historical upgrade anchor: #157 / v0.3.21

Design date: 2026-09-11

Base source reviewed:

```text
main = 8ed075f78693c7914356af3f25e11bcc8c5fa05c
VERSION = 0.3.22
```

Historical exact installer used as the mandatory old-version anchor:

```text
version = 0.3.21
sourceSha = 77a5c66c431c746d2be2f283640c7951730911eb
artifact = WebAssistant-win-x64-0.3.21.exe
artifactSha256 = 68fe8a0145721c13f14dad4a4d3fea333c9ccb240b036d8869aa2152af7b7271
```

The v0.3.21 bytes are immutable historical release-candidate evidence and must not be rebuilt, repacked, or replaced for this work.

## 1. Goal

Make the normal Windows user path a deterministic in-place upgrade even when the previous WebAssistant service is running and has already materialized one or more NAPS2 worker processes.

Required user flow:

```text
older WebAssistant installed
+ WebAssistant service Running
+ package-owned NAPS2.Worker may be alive

run newer canonical WebAssistant-win-x64-<VERSION>.exe

-> prior WebAssistant runtime is stopped safely
-> package-owned workers are gone before MSI validates/replaces payload
-> newer version upgrades in place
-> service starts again
-> health/scanner paths remain usable
```

No manual uninstall, manual service stop, Task Manager cleanup, `Retry`, or `Ignore` decision is acceptable in the supported upgrade path.

The design must also make runtime shutdown correct independently of the installer so that future versions do not leave workers behind during normal service stop/restart/uninstall.

## 2. Scope and semantic boundary

This is a Windows installation/runtime-lifecycle transaction, not a scanner API transaction.

It may change:

- Windows Burn bundle composition;
- Windows installer acceptance;
- WebAssistant-owned process-shutdown infrastructure;
- the repository-owned `WebAssistant.NAPS2.Sdk` package implementation and version;
- Windows scanner adapter shutdown plumbing;
- product VERSION once, after functional acceptance is GREEN.

It must not change:

- HTTP scanner request/response semantics accepted by #163;
- scannerId identity semantics;
- source-selection policy;
- Linux installer/runtime behavior except unavoidable package-graph verification;
- accepted historical contract/conformance v0.2;
- staged v0.3.21 bytes;
- unrelated installer localization, release publication, or metadata-renaming work.

#191 does not itself change accepted WebAssistant HTTP contract authority. If implementation unexpectedly creates an observable API-semantic delta, stop and classify that delta separately rather than hiding it inside the upgrade transaction.

## 3. Current-state findings

### 3.1 WiX upgrade family already exists

The current WiX 7 sources use stable identities:

```xml
<Package Id="netkeep80.WebAssistant" ... />
<Bundle Id="netkeep80.WebAssistant.Bundle" ... />
```

With WiX v6+ / v7, stable `Package/@Id` and `Bundle/@Id` are the upgrade-family identities. Higher versions with the same IDs participate in the normal upgrade relationship, and `Package` has the default major-upgrade strategy.

Therefore #191 must **not** invent a second `UpgradeCode`/`MajorUpgrade` mechanism merely to satisfy an earlier hypothesis. A RED test asserting that no upgrade relationship exists would test a falsified premise.

The problem to solve is deterministic payload ownership and process shutdown before replacement.

### 3.2 Current MSI service authoring is necessary but insufficient

Current package authoring includes:

```xml
<ServiceControl
    Name="WebAssistant"
    Start="install"
    Stop="both"
    Remove="uninstall"
    Wait="yes" />
```

This correctly asks Windows Installer to stop/start the service and wait for the service operation. It should remain the normal MSI service lifecycle mechanism.

However, service state `Stopped` is not a proof that every child process holding files in `Program Files\WebAssistant` has exited.

### 3.3 Current Windows scanner initialization creates workers eagerly

`WindowsScanAdapter` creates one long-lived `ScanningContext` and calls:

```text
SetUpWin32Worker()
```

The pinned NAPS2 `WorkerFactory.Init` defaults `StartSpareWorkers = true`, so worker creation can occur as soon as the Windows adapter is first materialized. The adapter is a lazy DI singleton, therefore current Windows service acceptance can remain GREEN without ever constructing it if scanner endpoints are not exercised.

This is why existing lifecycle acceptance does not prove the real failure mode.

### 3.4 Current NAPS2 shutdown is not a lifetime barrier

Pinned upstream commit:

```text
450cba65aaffe6387041050a573051a64cd80fe9
```

Current repo-owned package:

```text
WebAssistant.NAPS2.Sdk 1.3.0-webassistant.2.450cba65
```

At that baseline:

- `ScanningContext.Dispose()` does not stop `WorkerFactory` workers;
- `WorkerContext.Dispose()` calls `Stop().AssertNoAwait()`;
- `WorkerContext.Stop()` returns immediately on a second call once `_stopped` is set, even if the first asynchronous stop has not completed;
- `WorkerFactory.StopSpareWorkers()` can wait for workers still present in spare queues, but the factory does not own a complete registry of every created/live worker;
- worker startup is asynchronous, which creates a race where shutdown can begin while a worker is still being created;
- on Windows NAPS2 places workers in a kill-on-job-close Job object, but the Job lifetime is ultimately tied to parent-process teardown, not to a deterministic `ScanningContext` shutdown completion barrier.

This is sufficient to explain a service being considered stopped while a package-owned worker is still alive long enough for MSI `Files In Use` detection.

### 3.5 DI disposal alone is too late for the service-stop invariant

.NET Generic Host shutdown ordering is:

```text
IHostedLifecycleService.StoppingAsync
IHostApplicationLifetime.ApplicationStopping
IHostedService.StopAsync
IHostedLifecycleService.StoppedAsync
IHostApplicationLifetime.ApplicationStopped
IHostLifetime.StopAsync
```

`WindowsServiceLifetime.OnStop()` waits for `ApplicationStopped` before returning to SCM. Ordinary service-provider disposal of singleton `IDisposable` instances is not part of the pre-`ApplicationStopped` sequence.

Therefore relying only on eventual DI disposal of `WindowsScanAdapter` would not prove that workers are gone when SCM observes the service as stopped. #191 requires an explicit pre-`ApplicationStopped` shutdown participant.

### 3.6 A new runtime fix alone cannot repair the first legacy upgrade

This is the critical compatibility fact.

During `v0.3.21 -> candidate`, the process being stopped is still **v0.3.21**. Its worker lifecycle code is already frozen in the old installed bytes. A lifecycle correction shipped only inside the candidate cannot execute until after the candidate has replaced those bytes.

Therefore a complete solution requires two layers:

```text
legacy transition bridge in the new installer
+
permanent runtime lifecycle correction in the new version
```

The bridge protects the first transition from known old versions. The runtime fix removes the root cause for all subsequent service stops/upgrades.

## 4. Architecture

The accepted architecture has three cooperating boundaries.

```text
new Burn bundle
  |
  +-- pre-MSI UpgradePreflight helper
  |     -> stop previous WebAssistant service
  |     -> prove ownership of lingering package worker(s)
  |     -> wait / bounded force termination only for proven WebAssistant-owned workers
  |     -> return only when payload is no longer owned by old runtime
  |
  +-- canonical WebAssistant MSI
        -> normal WiX upgrade
        -> ServiceControl stop/start/remove remains authoritative

new WebAssistant runtime
  |
  +-- WindowsScannerShutdownHostedService
        -> runs before ApplicationStopped
        -> does not force scanner adapter materialization
        -> shuts down already-materialized WindowsScanAdapter
              |
              +-- repo-owned NAPS2 WorkerFactory lifetime barrier
                    -> own every worker it creates
                    -> stop every owned worker
                    -> wait until each process exits
                    -> bounded force-kill only its own unresponsive worker
```

The installer bridge and runtime fix deliberately overlap. The installer bridge is needed because old versions cannot be retroactively fixed. The runtime fix is needed so the installer is not permanently used to hide a runtime resource leak.

## 5. Layer A — Burn pre-MSI upgrade preflight

### 5.1 Form

Add one small repository-owned Windows helper project, conceptually:

```text
webassist/build/windows/upgrade-preflight/
  WebAssistant.UpgradePreflight.csproj
  Program.cs
  focused Win32/service/process helpers
```

The helper is:

- `net10.0`;
- `win-x64`;
- self-contained;
- non-interactive;
- console/log oriented;
- built only for the Windows installer;
- not installed as product payload;
- embedded/chained by Burn before the MSI package.

Use platform APIs / narrow Win32 P/Invoke rather than PowerShell or an external runtime dependency. The project should stay intentionally small and have no scanner/NAPS2 application logic.

Burn authoring must schedule the helper for install/upgrade/repair preparation **before the MSI package executes**. It must not uninstall or persist the helper as a separate product. The exact `ExePackage` detect/permanent/arguments authoring is an implementation detail, but executable tests must prove the observable planning rules:

```text
fresh install       -> helper executes, sees no prior service, success/no-op
upgrade             -> helper executes before MSI
same-version repair -> helper executes before repair payload validation
uninstall only      -> helper does not perform upgrade preparation
```

The helper is `Vital`: if it detects an existing WebAssistant runtime but cannot establish the required clean ownership state, the bundle fails closed before MSI payload replacement.

### 5.2 Service identity and expected product root

The helper owns only the canonical service:

```text
serviceName = WebAssistant
```

If no such service exists, preflight returns success without touching arbitrary processes.

If the service exists, read its configured binary path through Windows service APIs and resolve the actual service executable path. The expected executable is the currently installed WebAssistant service binary.

The helper must reject an ambiguous or unsafe service configuration instead of guessing. It must not terminate a process merely because its image name is `WebAssistant.exe` or `NAPS2.Worker.exe`.

### 5.3 Service-process and worker ownership proof

When the service is running, identify the exact old service-process instance as:

```text
service instance =
    configured executable path
  + PID reported by SCM
  + process creation time
```

A worker is eligible for preflight cleanup only if ownership can be proven from that exact old service-process instance.

Required proof is the conjunction of:

```text
process executable path
  == one of the NAPS2 worker paths belonging to the resolved WebAssistant install root

AND

process parent PID
  == recorded WebAssistant service PID

AND

worker process creation time
  falls within the recorded lifetime of that service-process instance
```

The supported worker path set must match the pinned NAPS2 `CreateDefault()` search layout relative to the WebAssistant entry folder:

```text
<NAPS2 entry root>\NAPS2.Worker.exe
<NAPS2 entry root>\lib\NAPS2.Worker.exe
```

The implementation must derive the concrete allowed paths from the resolved installed WebAssistant root rather than searching the entire machine by process name.

Unrelated `NAPS2.Worker.exe` processes with a different executable path, different parent PID, or incompatible creation time are out of scope and must remain untouched.

If the service is already stopped and no exact parent service-process instance can be established, the helper must not guess worker ancestry. If package-owned worker locks remain but ownership cannot be proven safely, fail closed rather than kill by name or path alone.

### 5.4 Preflight algorithm

Conceptual algorithm:

```text
open WebAssistant service
if absent:
    return SUCCESS

resolve configured service executable path
query current service state

if service has a running process:
    record service process path + PID + creation time
    begin capturing workers satisfying the ownership proof

if service is Running/StartPending/StopPending:
    request SERVICE_CONTROL_STOP when needed

while the recorded service process is alive:
    continue capturing newly-created workers satisfying the same ownership proof
    wait for SCM/service-process convergence within bounded policy

record service-process exit time
freeze the captured owned-worker identity set

for each captured owned worker still alive:
    wait short graceful-exit window
    if still alive:
        terminate that exact captured process only
        wait for its exit, bounded

verify:
    old service-process instance is gone
    every captured owned-worker instance is gone

return SUCCESS
```

After the recorded service process exits, new processes that merely reuse its numeric PID are never added to the ownership set. This prevents PID-reuse races from widening cleanup authority.

The helper does not attempt to stop arbitrary active scans gracefully through HTTP. Its job is the compatibility bridge for a version whose shutdown semantics are already frozen. The first action remains an orderly SCM stop; force termination is only a bounded fallback for a worker whose ownership has already been proven.

### 5.5 Race handling

The design must cover worker startup racing with service stop.

Preflight continuously closes the observation window until the recorded service-process instance exits. A worker created after the first snapshot but before parent exit can therefore enter the captured ownership set; a process created after parent exit cannot.

Success is based on final absence of the captured owned-worker set, not merely on having issued a stop request.

If final ownership cannot be determined safely, fail closed rather than kill by name.

### 5.6 Logging and exit semantics

Preflight logs concise machine-action evidence suitable for bundle/CI diagnostics:

```text
service-found / service-absent
service-pid
service-stop-requested
service-stopped
service-process-exited
owned-worker-found pid/path
owned-worker-exited-gracefully
owned-worker-force-terminated
preflight-pass / preflight-fail
```

Do not log secrets, environment dumps, scanner identifiers, or unrelated process command lines.

Exit `0` means the old runtime no longer owns replaceable payload according to the required proof. Any non-zero exit is a hard bundle failure.

## 6. Layer B — permanent NAPS2 worker lifetime correction

### 6.1 Package lineage

Do not mutate `.2` in place.

Create the next immutable repository-owned SDK package from the same exact upstream baseline:

```text
source = cyanfish/naps2@450cba65aaffe6387041050a573051a64cd80fe9
packageId = WebAssistant.NAPS2.Sdk
packageVersion = 1.3.0-webassistant.3.450cba65
```

The `.3` delta contains all `.2` feeder-paper-state behavior unchanged plus the minimum worker-ownership/shutdown fix described here.

Update `webassist/vendor/naps2/README.md`, the fail-closed rebuild script, package integrity evidence, and `WebAssistant.csproj` to the exact new package identity.

### 6.2 WorkerFactory becomes the ownership authority

`WorkerFactory` must maintain a thread-safe registry of every worker process/context it creates, not merely workers currently waiting in spare queues.

Ownership begins as part of successful worker-process creation and ends only after process exit has been observed and the context is retired.

This registry must cover:

- initial spare workers;
- replacement spare workers;
- workers leased to active scanner operations;
- workers whose normal per-operation `Dispose()` already initiated asynchronous shutdown;
- workers created concurrently with shutdown initiation.

Once factory shutdown begins:

```text
no new worker may escape untracked
no new worker may be handed to callers
all startup races must converge into shutdown
```

A newly starting worker that crosses the shutdown boundary must either be prevented from becoming usable or be registered and immediately included in the shutdown barrier.

### 6.3 Idempotent awaitable WorkerContext stop

Current behavior:

```text
first Stop(): sets _stopped and awaits exit
second Stop(): returns immediately
```

is not a valid lifetime barrier because a second caller cannot wait for the already-running first stop.

Replace the boolean-only behavior with one shared stop task/state transition:

```text
first StopAsync()
    creates the single stop operation
    requests graceful worker shutdown
    waits for process exit up to bounded timeout
    force-kills the same owned process if necessary
    waits for actual exit observation

concurrent/subsequent StopAsync()
    awaits the same stop operation
```

Normal per-operation disposal may continue to initiate asynchronous cleanup without blocking every scanner HTTP request for the full worker timeout. What changes is that factory/context shutdown can always await the same in-flight stop to completion.

### 6.4 Factory shutdown barrier

Add one deterministic factory shutdown operation, conceptually:

```text
ShutdownAsync / StopAllWorkersAsync
```

with these postconditions:

```text
factory no longer accepts/creates usable workers
all startup tasks are converged
all registered workers have completed StopAsync
all registered worker processes are exited
registry is empty/retired
```

The barrier must be idempotent and safe when called while scanner operations are completing.

The existing 60-second per-worker upstream fallback is too large to become an unconstrained aggregate service-stop delay. Implementation should use a bounded shutdown policy compatible with the host `ShutdownTimeout`, and tests must prove the total service stop path is bounded. Exact timeout constants belong in the implementation plan/tests, not in public API semantics.

### 6.5 ScanningContext exposes the same deterministic barrier to both async and sync shutdown

For a context configured with this worker factory, WebAssistant needs an awaitable shutdown path that does not complete while its owned worker processes are still alive.

The `.3` package must therefore expose one awaitable context-shutdown operation backed by the factory barrier. Synchronous `ScanningContext.Dispose()` must join the same idempotent barrier for fallback/ordinary ownership safety rather than start an independent cleanup path.

Conceptually:

```text
await ScanningContext shutdown
    -> await WorkerFactory shutdown barrier
    -> dispose remaining context resources

ScanningContext.Dispose()
    -> synchronously join that same already-idempotent shutdown operation
```

The exact public/internal method name is an implementation detail, but there must be one shared state machine, not separate async and sync shutdown semantics.

This is an internal repository-owned SDK lifecycle correction. It does not change WebAssistant HTTP semantics.

## 7. Layer C — explicit WebAssistant host shutdown barrier

### 7.1 Required service-stop ordering

For candidate and later versions, Windows service stop must satisfy:

```text
SCM stop request
  -> ASP.NET host begins shutdown
  -> ApplicationStopping
  -> WindowsScannerShutdownHostedService.StopAsync
       -> stop accepting scanner lifetime work
       -> if WindowsScanAdapter was materialized, await its deterministic shutdown
       -> ScanningContext awaits every owned NAPS2 worker exit
  -> remaining hosted-service shutdown completes
  -> ApplicationStopped
  -> WindowsServiceLifetime returns from OnStop
  -> SCM may report service Stopped
```

The invariant is:

```text
SCM reports WebAssistant stopped
=> no NAPS2 worker owned by that WebAssistant process remains alive
```

### 7.2 A dedicated hosted shutdown participant is required

Do not rely only on DI container disposal of `WindowsScanAdapter`: Generic Host calls `ApplicationStopped` before service-provider disposal, while Windows SCM waits on `ApplicationStopped`.

Introduce a small Windows-only lifetime boundary, conceptually:

```text
WindowsScannerShutdownCoordinator
WindowsScannerShutdownHostedService : IHostedService
```

Responsibilities:

- `WindowsScanAdapter` registers itself with the coordinator when and only when the lazy singleton is actually constructed;
- the hosted service depends on the coordinator, not directly on `IScanAdapter`;
- `StartAsync` is a no-op;
- `StopAsync` tells the coordinator to prevent further scanner-lifetime acquisition and, if an adapter was registered, awaits that adapter's deterministic shutdown;
- if no scanner endpoint was ever used, shutdown does **not** construct `WindowsScanAdapter` merely to dispose it;
- later DI disposal is an idempotent fallback that joins the already-completed barrier.

This boundary exists to establish host ordering; it must not contain scanner selection/business policy.

### 7.3 Adapter shutdown contract

`WindowsScanAdapter` gains one idempotent awaitable shutdown operation used by the hosted service. It delegates to the `.3` `ScanningContext` barrier.

After shutdown begins, attempts to start new adapter operations must fail/cancel rather than create fresh workers behind the barrier.

The existing `IDisposable` path remains as a safe fallback and joins the same shutdown operation.

### 7.4 In-flight HTTP work

WebApplication shutdown stops accepting new requests and cancels request tokens. `ScanCoordinator` already passes request cancellation into discovery/scan work and enforces a single acquisition gate.

#191 does not add a separate long-running job subsystem. The implementation must verify through tests that service shutdown during materialized scanner state converges within the host timeout and does not deadlock the coordinator, adapter, hosted shutdown service, or NAPS2 worker barrier.

## 8. Version behavior

Keep the stable WiX identities unchanged:

```text
Package Id = netkeep80.WebAssistant
Bundle Id = netkeep80.WebAssistant.Bundle
```

Required observable behavior:

### 8.1 Older installed version

```text
installed < candidate
-> preflight runs
-> old runtime is cleanly released
-> normal WiX major upgrade proceeds
-> exactly one current WebAssistant Installed Apps entry remains
```

### 8.2 Same exact version

Same-version behavior is explicit maintenance, not a second side-by-side install.

Executable acceptance must invoke the canonical same-version bundle repair/maintenance path and prove:

```text
preflight executes safely
repair/maintenance succeeds
service returns Running
installed VERSION is unchanged
one Installed Apps entry remains
health/scanner enumeration remain GREEN
```

The implementation may use the standard Burn maintenance command/surface; it must not create a custom second product identity to simulate repair.

### 8.3 Downgrade

```text
installed > older installer
-> downgrade is rejected
-> newer service/install remains intact and healthy
```

The historical `v0.3.21` installer is the canonical negative input once a newer candidate is installed.

The downgrade test must not uninstall or corrupt the newer installation as a side effect.

## 9. Package/state ownership

Upgrade preserves the accepted ownership model:

```text
C:\Program Files\WebAssistant
    package-owned binaries/config

C:\ProgramData\WebAssistant\logs
    preserved across upgrade/uninstall

C:\ProgramData\WebAssistant\data
    preserved across upgrade/uninstall
```

Package-owned `appsettings.json` follows the new package deterministically; user scanner preferences are not stored inside WebAssistant by #191.

The preflight helper may inspect installed runtime identity but must not modify ProgramData or product configuration.

## 10. Acceptance architecture

### 10.1 Current acceptance blind spot must be removed

Current `run-installer-acceptance.ps1` starts from a clean machine, installs one version, invokes general service acceptance, then uninstalls. General service acceptance does not require `/v1/scanners`, so the lazy Windows scanner adapter can remain unmaterialized and no NAPS2 worker is proven alive.

#191 adds an explicit old-to-new executable acceptance path instead of weakening the existing clean-install coverage.

### 10.2 Historical input integrity

The upgrade test must consume the exact staged historical artifact:

```text
WebAssistant-win-x64-0.3.21.exe
SHA-256 68fe8a0145721c13f14dad4a4d3fea333c9ccb240b036d8869aa2152af7b7271
source 77a5c66c431c746d2be2f283640c7951730911eb
```

CI may download it from the durable draft-release custody already established by #157, but must verify the pinned digest before execution.

If exact historical bytes are unavailable or the digest mismatches, acceptance fails. It must **not** rebuild v0.3.21 from source as a substitute.

### 10.3 Mandatory automated upgrade scenario

On GitHub-hosted Windows acceptance:

```text
1. assert machine starts without WebAssistant installed
2. obtain + SHA-verify exact v0.3.21 canonical EXE
3. install v0.3.21 quietly
4. assert service Running
5. call GET /v1/scanners
6. prove at least one NAPS2.Worker process owned by installed v0.3.21 WebAssistant is alive
7. DO NOT Stop-Service
8. DO NOT uninstall old version
9. run exact candidate canonical EXE quietly
10. assert bundle exits success without interactive Files In Use handling
11. assert old service/worker process identities are gone
12. assert one Installed Apps entry at candidate VERSION
13. assert installed WebAssistant.exe ProductVersion == candidate VERSION
14. assert service Running
15. assert /v1/health healthy
16. assert /v1/scanners responds through the candidate runtime
17. exercise service restart and repeat health/scanner checks
18. prove candidate runtime service stop/restart leaves no owned worker after each stop
19. invoke explicit same-version candidate maintenance/repair
20. repeat version/entry/service/health/scanner assertions
21. run exact v0.3.21 installer as downgrade attempt
22. assert downgrade rejected and candidate remains intact/healthy
23. uninstall candidate through canonical bundle
24. assert service + Program Files payload removed
25. assert ProgramData logs/data preservation semantics
26. assert no WebAssistant-owned NAPS2 worker remains
```

The scanner endpoint is sufficient to materialize the NAPS2 worker path; automated CI does not require a physical scanner to prove process ownership. Existing virtual-scanner/direct-SDK tests continue to cover scanning mechanics separately.

### 10.4 Files In Use proof

Quiet execution alone is not enough if the installer silently tolerated locked files.

Acceptance must preserve Burn/MSI logs and fail on evidence of unresolved/persisting `FilesInUse` / `MsiRMFilesInUse` conditions or equivalent retry/ignore-required state. The strongest primary proof remains that the candidate bundle exits successfully after preflight and the old proven worker process is already gone before MSI payload replacement begins.

### 10.5 Runtime shutdown regression tests

Repository tests around the `.3` SDK patch must prove at minimum:

- every created worker is registered as factory-owned;
- repeated `StopAsync` calls await one shared stop operation;
- workers already stopping are still awaited by factory shutdown;
- spare workers are included;
- leased/active workers are included;
- worker startup racing with shutdown cannot survive the barrier;
- unresponsive owned worker fallback is bounded and terminates only that worker;
- shutdown is idempotent;
- after awaitable `ScanningContext` shutdown completes, no factory-owned worker process remains;
- synchronous `ScanningContext.Dispose()` joins the same barrier.

WebAssistant tests must prove:

- hosted shutdown does not materialize an unused Windows scanner adapter;
- a materialized adapter is shut down before `ApplicationStopped`;
- subsequent DI disposal is idempotent;
- adapter operations cannot create a new worker after shutdown begins.

### 10.6 Preflight ownership negative tests

The helper must have executable tests proving it refuses unsafe cleanup:

- no WebAssistant service -> success/no-op;
- unrelated process named `NAPS2.Worker` with different path -> untouched;
- NAPS2.Worker at expected-looking path but wrong parent PID -> untouched;
- PID-reuse process created after recorded service-process exit -> untouched;
- service already stopped + unprovable worker ancestry -> no kill / fail closed if replacement would be unsafe;
- ambiguous/unsafe WebAssistant service binary identity -> fail closed;
- service stop timeout -> fail closed;
- proven owned worker exits gracefully -> no force kill;
- proven owned worker remains -> only that exact captured worker is terminated;
- worker/path identity changes during race -> no broad kill;
- final captured owned worker remains alive -> preflight failure.

## 11. TDD transaction order

Implementation must follow RED -> GREEN at exact branch heads.

The first RED stage should encode actual missing behavior, not the falsified WiX-upgrade-family hypothesis.

Recommended RED evidence groups:

```text
A. preflight/helper contract tests
   -> helper/bundle pre-MSI ownership barrier absent

B. NAPS2 lifecycle tests
   -> ScanningContext shutdown does not guarantee worker exit

C. WebAssistant host-lifetime tests
   -> scanner cleanup is not currently guaranteed before ApplicationStopped

D. installer acceptance contract/tests
   -> no exact v0.3.21 -> candidate live-worker upgrade scenario
```

Then implement the minimum production behavior to make each group GREEN.

Do not bump product VERSION merely to create RED evidence. Keep:

```text
VERSION = 0.3.22
```

through functional RED/GREEN development and bump exactly once to:

```text
VERSION = 0.3.23
```

only after the complete functional transaction is GREEN and ready for final candidate identity. If repository policy requires a different monotonic next version because another accepted transition lands first, use the then-current next version rather than reusing a consumed number.

## 12. Failure semantics

Fail closed before payload replacement when:

- an existing WebAssistant service cannot be stopped within the bounded policy;
- the old service-process instance remains alive;
- a captured/proven old package worker remains alive after bounded cleanup;
- worker ownership is ambiguous and cleanup would require a process-name-wide/path-only kill;
- helper execution itself fails;
- historical old installer digest does not match pinned evidence in acceptance.

Do not silently continue and hope Windows Installer resolves the lock.

Runtime shutdown must remain bounded by host shutdown policy and must not report successful service stop while a tracked owned worker is knowingly still alive.

## 13. Explicit non-solutions

The following are rejected:

- global `taskkill /IM NAPS2.Worker.exe /F`;
- killing every process with a matching image name;
- killing by install-root path alone when ancestry cannot be proven;
- telling users to stop the service manually;
- telling users to uninstall before upgrading;
- relying only on WiX `ServiceControl Wait="yes"`;
- relying only on Job Object process death at eventual parent-process exit;
- relying only on DI singleton disposal after host shutdown;
- disabling spare workers as the complete fix;
- changing `WorkerContext.Dispose()` to synchronously block every normal scanner operation for the full stop timeout;
- editing immutable `.2` package bytes in place;
- rebuilding historical v0.3.21 to manufacture upgrade evidence;
- adding duplicate WiX upgrade-family metadata without evidence it is required.

## 14. Definition of done

#191 is ready to merge only when exact-head evidence proves all of the following:

- [ ] repo-owned upgrade-preflight helper exists and is isolated from product business logic;
- [ ] Burn executes preflight before MSI install/upgrade/repair payload validation;
- [ ] preflight never performs broad process-name or unproven path-only cleanup;
- [ ] exact v0.3.21 service + live package worker is reproduced automatically;
- [ ] v0.3.21 -> candidate upgrade succeeds without manual stop/uninstall/Ignore;
- [ ] old proven worker is gone before candidate payload replacement;
- [ ] repo-owned NAPS2 `.3` lifecycle package is immutable and provenance-pinned;
- [ ] NAPS2 factory owns and deterministically stops every worker it creates;
- [ ] awaitable/synchronous `ScanningContext` shutdown share one worker-exit barrier;
- [ ] explicit hosted shutdown runs before `ApplicationStopped`;
- [ ] hosted shutdown does not instantiate an otherwise unused scanner adapter;
- [ ] candidate Windows service stop implies no owned NAPS2 worker remains;
- [ ] service returns Running after upgrade;
- [ ] health and scanner enumeration are GREEN after upgrade and restart;
- [ ] same-version maintenance/repair is deterministic and GREEN;
- [ ] downgrade is rejected without damaging the newer installation;
- [ ] exactly one WebAssistant Installed Apps entry remains after upgrade/repair;
- [ ] installed executable version equals candidate VERSION;
- [ ] package-owned config semantics remain deterministic;
- [ ] ProgramData logs/data preservation remains GREEN;
- [ ] uninstall removes service + Program Files payload and leaves no owned worker;
- [ ] canonical Windows acceptance preserves exact candidate artifact SHA/provenance identity;
- [ ] core, repo-guard, Windows service/installer acceptance and relevant scanner tests are GREEN on the exact final head;
- [ ] VERSION advances once only after functional GREEN.

## 15. Follow-up boundary

After #191 is accepted, the next Windows canonical installer may be produced and exercised through the existing exact-byte release-candidate architecture.

Do not pull #164 extended scanner settings, installer localization, GitLab/Linux CI redesign, final PDF, or final Release publication into this transaction.
