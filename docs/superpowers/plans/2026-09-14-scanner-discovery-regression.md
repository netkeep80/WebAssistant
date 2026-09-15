# Windows Scanner Discovery Regression Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make scanner listing fast and independent from slow/offline scanner capability probes, while increasing Windows scanner diagnostics enough to localize WIA/TWAIN delays.

**Architecture:** `GET /v1/scanners` enumerates registered endpoints only (`scannerId`, `name`, `backend`) and never calls `GetCaps` for every device. `GET /v1/scanners/{scannerId}/settings` resolves exactly the selected endpoint and performs the deep capability probe only for that scanner. `POST /v1/scan` likewise resolves capabilities only for the selected scanner before source/settings validation. Registered-but-physically-offline devices may remain in the list. WIA and TWAIN remain independent endpoints. Stage-level logs cover enumeration, selected-device capability probing, exceptions and shutdown duration.

**Tech Stack:** .NET 10, ASP.NET Core, NAPS2 SDK/Win32 worker, xUnit, Microsoft.Extensions.Logging.

**Spec:** `docs/superpowers/specs/2026-09-10-scanner-api-stable-id-auto-design.md`, candidate contract v0.3 scanner requirements, and reopened Issue #163 physical Windows evidence.

## Global Constraints

- Base at start: `main=ee22fcee400e17bca222c5be3e157e3eccd2e3d1`, `webassist/VERSION=0.3.30`.
- Accepted contract/conformance v0.2 are immutable; candidate v0.3 may be synchronized only if required.
- `/v1/scanners` means registered endpoints known to the backend, not a liveness/health check.
- One offline endpoint must not force capability probing of unrelated endpoints.
- No arbitrary timeout before the exact blocking stage is proven from physical logs.
- Do not log raw backend-native IDs. Public opaque `scannerId`, backend, scanner name, stage, duration, exception type/HRESULT/message are allowed.
- Every production behavior change follows RED -> observed failure -> minimal GREEN.
- Filesystem work remains out of scope except for rebase/conflict handling before merge.

---

### Task 1: RED — prove the list/capability boundary

**Files:**
- `tests/core/WindowsScannerDiscoveryRegressionTests.cs`
- `tests/core/ScannerCapabilityBoundaryTests.cs`

- [x] Add regression asserting Windows listing does not invoke capability probes.
- [x] Add HTTP contract regression asserting `/v1/scanners` returns identity fields without `sources`.
- [x] Add HTTP regression asserting `/v1/scanners/{scannerId}/settings` probes exactly the selected scanner.
- [ ] Observe CI RED on test-only head.

### Task 2: Split registered-endpoint enumeration from selected capabilities

**Files:**
- `webassist/src/WebAssistant/Scanning/IScanAdapter.cs`
- `webassist/src/WebAssistant/Scanning/WindowsScanAdapter.cs`
- `webassist/src/WebAssistant/Scanning/LinuxScanAdapter.cs`
- `webassist/src/WebAssistant/Http/ScannerEndpointHandlers.cs`
- `webassist/src/WebAssistant/Http/ScannerSettingsEndpointHandlers.cs`
- `webassist/src/WebAssistant/Http/ScanCoordinator.cs`
- affected scanner tests

- [ ] Add `GetScannerCapabilitiesAsync(scannerId, cancellationToken)` to the adapter boundary with a compatibility default for test/fake adapters.
- [ ] Make Windows `GetScannersAsync` perform only WIA/TWAIN `GetDeviceList` enumeration and deterministic identity normalization.
- [ ] Make Linux `GetScannersAsync` perform only SANE `GetDeviceList` enumeration.
- [ ] Implement selected-endpoint capability resolution on Windows/Linux: enumerate the requested backend, resolve exact persistent ID, call `GetCaps` only for the selected device, return the enriched `ScannerDevice`.
- [ ] Remove `sources` from `/v1/scanners`; capabilities are represented only by the selected settings endpoint.
- [ ] Update settings handler and scan coordinator to use selected capabilities.
- [ ] Run focused scanner tests and obtain GREEN.

### Task 3: Add high-information scanner diagnostics

**Files:**
- `webassist/src/WebAssistant/Scanning/WindowsScanAdapter.cs`
- `webassist/src/WebAssistant/Scanning/WindowsScanAdapterHolder.cs` or DI factory in `Program.cs`
- diagnostics tests

- [ ] RED tests for log events.
- [ ] Log per backend `getDeviceList.start/success/failure` with duration and device count.
- [ ] Log selected `getCaps.start/success/failure` with public scannerId, scanner name, duration, exception type, HRESULT and message.
- [ ] Log adapter/scanning-context shutdown start/end/duration.
- [ ] Keep adapter creation lazy under Windows Service lifetime ownership.
- [ ] Run focused logging/lifetime tests GREEN.

### Task 4: Documentation, version and physical acceptance

**Files:**
- `webassist/docs/api.md`
- `webassist/VERSION`
- candidate contract/conformance only if synchronization is required by repository policy

- [ ] Document `/v1/scanners` as registered identity enumeration and `/settings` as selected capability probe.
- [ ] Run full core suite.
- [ ] Advance VERSION exactly once after implementation is green.
- [ ] Make repo-guard and exact-head CI green.
- [ ] Produce canonical Windows installer through the existing producer.
- [ ] On Vadim's machine verify `/v1/scanners` returns promptly even with broken/offline MF4500 registered and still returns MF220.
- [ ] Select MF220 and verify its settings return promptly.
- [ ] Select MF4500 separately and collect stage logs; if it blocks, the delay is now isolated to that selected operation and the log identifies `GetDeviceList` vs `GetCaps`.
- [ ] Decide any timeout/worker/driver follow-up only from this evidence.
