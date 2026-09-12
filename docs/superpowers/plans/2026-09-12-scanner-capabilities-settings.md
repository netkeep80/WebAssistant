# Scanner Capabilities Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Реализовать #164: capability-driven `dpi`, `colorMode`, `paperSize`, scanner-specific settings endpoint, canonical machine-readable schema и diagnostic panel, использующую тот же public API.

**Architecture:** Raw NAPS2 `ScanCaps` остаётся внутри adapters и преобразуется в WebAssistant-owned source-specific normalized capabilities. HTTP/API и diagnostic panel работают только с normalized model. `auto` projection использует intersection всех concrete sources, которые auto может выбрать, а effective defaults/validation вычисляются одной общей policy перед physical acquisition.

**Tech Stack:** .NET 10 / ASP.NET Core minimal API, C#, pinned `WebAssistant.NAPS2.Sdk`, xUnit, static HTML/JavaScript service panel, GitHub Actions virtual scanner acceptance.

**Spec:** `docs/superpowers/specs/2026-09-12-scanner-capabilities-settings-design.md`

## Global Constraints

- Base authority at start: `main=7f1ecac30d5b33bbc96d381df6e443e80a47a75f`, `VERSION=0.3.25`.
- Never modify accepted `contracts/webassistant-contract-v0.2.json` or `contracts/webassistant-conformance-v0.2.json`.
- Never modify `repo-policy.json`.
- Preserve #163 `source=auto` and stateless caller-owned preferences.
- No NAPS2 fork delta unless a new exact dependency gap is proven.
- No VERSION change until functional scanner/API/panel tests are GREEN; then exactly one transition.

---

### Task 1: RED — freeze public schema and normalized capability semantics

**Files:**
- Create: `tests/core/ScannerSettingsSchemaTests.cs`
- Create: `tests/core/ScannerCapabilityProjectionTests.cs`
- Modify: `tests/core/HttpScanContractTests.cs`
- Modify: `tests/core/ServicePanelScannerApiTests.cs`

**Interfaces:**
- Produces expected public vocabulary `dpi`, `colorMode`, `paperSize`, `duplex`.
- Produces expected mode vocabulary `auto`, `flatbed`, `feeder`, `feederDuplex`.
- Produces expected endpoint `GET /v1/scanners/{scannerId}/settings` and schema endpoint `GET /v1/scanner-settings/schema`.

- [ ] **Step 1: Add schema RED tests**

Tests must assert repository file `webassist/docs/scanner-settings.schema.json` exists, is valid JSON, exposes exact public field names, enum values `color|grayscale|blackAndWhite`, page sizes `letter|legal|a5|a4|a3|b5|b4`, preferred defaults `100/color/letter/false`, and contains no backend-specific `wia`, `twain`, `sane` fields.

- [ ] **Step 2: Add projection RED tests**

Create tests that construct source capabilities directly and assert:

```text
flatbed dpi [100,300,600]
feeder  dpi [200,300]
auto    dpi [300]
```

Also assert canonical ordering, default fallback to first supported value, color/page-size intersection and omission of unsupported modes.

- [ ] **Step 3: Add HTTP RED tests**

Extend `HttpScanContractTests` so fake adapter can expose normalized capabilities and capture effective settings. Assert:

```text
GET /v1/scanner-settings/schema -> 200 JSON
GET /v1/scanners/{scannerId}/settings -> 200 projection
malformed setting -> 400 before ScanAsync
valid-but-unsupported setting -> 422 before ScanAsync
omitted settings -> effective defaults
explicit settings -> exact effective settings passed to adapter
```

- [ ] **Step 4: Add panel RED tests**

Assert HTML contains one mode selector, one scan action, scanner information action, controls for dpi/colorMode/paperSize, settings/schema endpoint usage; assert legacy separate `scan-feeder` and `scan-duplex` buttons are absent.

- [ ] **Step 5: Run core tests and record RED**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --no-restore
```

Expected: new #164 tests fail because normalized settings model/schema/endpoints/panel do not yet exist, while pre-existing tests remain green.

---

### Task 2: GREEN — normalized model, projection and canonical schema

**Files:**
- Create: `webassist/src/WebAssistant/Scanning/ScannerSettings.cs`
- Create: `webassist/src/WebAssistant/Scanning/ScannerCapabilities.cs`
- Create: `webassist/src/WebAssistant/Scanning/ScannerCapabilityProjection.cs`
- Create: `webassist/docs/scanner-settings.schema.json`
- Modify: `webassist/src/WebAssistant/Scanning/ScannerDevice.cs`

**Interfaces:**
- `ScannerEffectiveSettings(int Dpi, ScannerColorMode ColorMode, ScannerPaperSize PaperSize)`.
- `ScannerSourceCapabilities(IReadOnlyList<int> DpiValues, IReadOnlyList<ScannerColorMode> ColorModes, IReadOnlyList<ScannerPaperSize> PaperSizes)`.
- `ScannerEndpointCapabilities(ScannerSourceCapabilities? Flatbed, ScannerSourceCapabilities? Feeder, ScannerSourceCapabilities? Duplex)`.
- `ScannerCapabilityProjection.BuildModes(ScannerDevice)` returns normalized request modes and effective defaults.
- `ScannerCapabilityProjection.ResolveEffectiveSettings(mode, requestedSettings)` validates and returns effective settings.

- [ ] **Step 1: Implement normalized enums/records**

Keep NAPS2 types out of these files. Public string mappings are exact lowercase/camelCase values from the spec.

- [ ] **Step 2: Implement deterministic projection**

For auto on dual-source endpoint intersect all setting sets. DPI sort ascending; color/page size follow canonical schema order. Unsupported/empty setting sets are represented explicitly and never fabricated.

- [ ] **Step 3: Implement default selection**

Preferred defaults are `100/color/letter`; if absent choose first supported value in canonical order. Same helper is later used by HTTP acquisition validation.

- [ ] **Step 4: Add canonical JSON schema**

Schema owns normalized field/type/enum/unit/default metadata and is repository-readable without runtime reflection.

- [ ] **Step 5: Run focused projection/schema tests**

Expected: Task 2 unit tests GREEN; HTTP/panel RED remains.

---

### Task 3: GREEN — adapter capability mapping and effective acquisition settings

**Files:**
- Modify: `webassist/src/WebAssistant/Scanning/IScanAdapter.cs`
- Modify: `webassist/src/WebAssistant/Scanning/WindowsScanAdapter.cs`
- Modify: `webassist/src/WebAssistant/Scanning/LinuxScanAdapter.cs`
- Modify: `tests/core/WindowsScanAdapterTests.cs`
- Modify: `tests/core/LinuxScanAdapterTests.cs`
- Modify: `tests/core/LinuxVirtualScanAdapterTests.cs`

**Interfaces:**
- New acquisition overload:

```csharp
Task<Stream> ScanAsync(
    string scannerId,
    ScanSource source,
    ScannerEffectiveSettings settings,
    CancellationToken cancellationToken = default);
```

- Existing overloads remain internal compatibility shims only if required by current tests/callers and must delegate to deterministic defaults.

- [ ] **Step 1: Map NAPS2 `PerSourceCaps` to normalized source capabilities**

`DpiCaps.Values` -> ordered integer values.
`BitDepthCaps` -> `color/grayscale/blackAndWhite`.
`PageSizeCaps.Fits(PageSize.<well-known>)` -> supported named page sizes.

- [ ] **Step 2: Store normalized endpoint capabilities in `ScannerDevice`**

No raw `ScanCaps` reference escapes adapter discovery.

- [ ] **Step 3: Apply effective settings to NAPS2 `ScanOptions`**

Set `Dpi`, `BitDepth`, `PageSize` together with current exact `Driver`, `Device`, `PaperSource`.

- [ ] **Step 4: Extend adapter tests**

Prove capability mapping for WIA/TWAIN/SANE test fixtures and exact mapping of effective settings into NAPS2 values using focused helper/reflection tests where physical scanner is not available.

- [ ] **Step 5: Run adapter/core tests**

Expected: adapter/projection GREEN; HTTP/panel may still be RED.

---

### Task 4: GREEN — public settings/schema endpoints and POST validation

**Files:**
- Create: `webassist/src/WebAssistant/Http/ScannerSettingsEndpointHandlers.cs`
- Modify: `webassist/src/WebAssistant/Http/ScanRequest.cs`
- Modify: `webassist/src/WebAssistant/Http/ScanCoordinator.cs`
- Modify: `webassist/src/WebAssistant/Http/ScannerEndpointHandlers.cs`
- Modify: `webassist/src/WebAssistant/Program.cs`
- Modify: `tests/core/HttpScanContractTests.cs`

**Interfaces:**
- `GET /v1/scanner-settings/schema`.
- `GET /v1/scanners/{scannerId}/settings`.
- `ScanSettings` adds nullable `int? Dpi`, `string? ColorMode`, `string? PaperSize`.

- [ ] **Step 1: Add schema endpoint**

Return canonical schema JSON with `application/json`, no duplicated in-code schema object.

- [ ] **Step 2: Add scanner settings endpoint**

Reuse discovery semantics from current `/v1/scanners`: malformed id 400, absent endpoint 404, requested backend unavailable 503, unexpected failure 502.

- [ ] **Step 3: Parse syntax before acquisition**

Unknown color/page-size names, non-positive DPI -> 400 and zero scan calls.

- [ ] **Step 4: Resolve selected request mode and effective settings**

Use #163 source policy for concrete source and `ScannerCapabilityProjection` for mode capability/default validation. Valid normalized but unsupported value -> 422.

- [ ] **Step 5: Call adapter with exact effective settings**

Auto feeder-empty fallback to flatbed must recompute/validate the same requested settings for flatbed before fallback acquisition; auto projection guarantees this remains valid.

- [ ] **Step 6: Run HTTP/core tests**

Expected: all #164 public API tests GREEN.

---

### Task 5: GREEN — diagnostic panel as generic scanner settings client

**Files:**
- Modify: `webassist/src/WebAssistant/wwwroot/index.html`
- Modify: `tests/core/ServicePanelScannerApiTests.cs`
- Modify: `tests/core/PlatformEndToEndTests.cs` and/or existing virtual scanner panel acceptance fixture where applicable.

**Interfaces:**
- Panel fetches schema once and scanner settings projection after scanner selection/refresh.
- One mode selector maps exactly:

```text
auto          -> source=auto, duplex=false
flatbed       -> source=flatbed, duplex=false
feeder        -> source=feeder, duplex=false
feederDuplex  -> source=feeder, duplex=true
```

- [ ] **Step 1: Replace mode-specific buttons**

Keep one `Сканировать` button and one mode select populated from endpoint `modes`.

- [ ] **Step 2: Render dpi/color/paper controls from schema + projection**

Do not branch on backend names or scanner model.

- [ ] **Step 3: Add `Информация о сканере`**

Show selected discovery data plus read-only settings projection; no state mutation.

- [ ] **Step 4: Submit selected values in unified POST body**

Exact request includes selected source, duplex and optional dpi/colorMode/paperSize.

- [ ] **Step 5: Extend panel acceptance tests**

Prove new endpoint strings, controls, absence of legacy buttons, and browser-level path for applicable virtual fixture.

- [ ] **Step 6: Run core + virtual scanner acceptance**

Expected: panel and scanner acceptance GREEN.

---

### Task 6: Candidate authority, docs, version and final verification

**Files:**
- Modify: `contracts/webassistant-contract-v0.3.json`
- Modify: `contracts/webassistant-conformance-v0.3.json`
- Modify: `webassist/docs/api.md`
- Modify: `tests/core/DistributionContractCandidateTests.cs`
- Modify: `tests/core/DependencyOwnershipTests.cs` only if exact required vendored path/version assertions need synchronization; no NAPS2 package change is expected.
- Modify: `webassist/VERSION` exactly once after functional GREEN.

**Interfaces:**
- Candidate authority adds normalized scanner settings/capability/schema requirements and conformance vectors.

- [ ] **Step 1: Update candidate contract/conformance and API docs**

Document request model, schema endpoint, scanner projection, source-specific semantics, defaults, 400/422 rules, examples and caller-owned persistence.

- [ ] **Step 2: Run functional suite before VERSION change**

Expected: scanner/API/panel tests GREEN while product version monotonic/release fixture checks may intentionally still require version transition.

- [ ] **Step 3: Advance VERSION exactly once**

`0.3.25 -> 0.3.26`, then synchronize only version-derived release fixtures required by existing tests; do not alter release semantics.

- [ ] **Step 4: Run full core suite**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --no-restore
```

Expected: all tests PASS.

- [ ] **Step 5: Run full PR CI/repo-guard**

Require exact-head GREEN for core, virtual Linux SANE scanner, Windows TWAIN scanner, installer/service suites selected by change-plan, `ci-fast`, `ci-required`, and repo-guard.

- [ ] **Step 6: Verify diff boundary**

Confirm accepted v0.2, `repo-policy.json`, installer/release implementation and unrelated filesystem code are unchanged.
