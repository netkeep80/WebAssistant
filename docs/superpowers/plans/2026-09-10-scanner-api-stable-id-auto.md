# Scanner API stable identity and automatic source selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the current WIA-first/source-specific scanner API with one JSON `POST /v1/scan`, deterministic persistent scanner identities, independent WIA+TWAIN discovery, and WebAssistant-owned automatic source selection.

**Architecture:** Public orchestration, stable identity, partial-failure semantics, and source policy live in WebAssistant. The repository-owned NAPS2 SDK is extended only with nullable feeder-paper state. Accepted v0.2 remains immutable; the existing unaccepted v0.3 candidate is updated to authorize the semantic delta.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, NAPS2 SDK 1.3.0 repository-owned package, NAPS2.Wia, NTwain, xUnit, GitHub Actions, repo-guard.

**Spec:** `docs/superpowers/specs/2026-09-10-scanner-api-stable-id-auto-design.md`

## Global Constraints

- Re-check live `main` before execution. Planned base is `77a5c66c431c746d2be2f283640c7951730911eb`, `webassist/VERSION = 0.3.21`; if it moved, stop and reconcile the plan first.
- GitHub is source of truth. `netkeep80/ScannerAgent` remains read-only.
- Preserve `webassistant-contract/v0.2`, `webassistant-conformance/v0.2`, and `repo-policy.json.current = v0.2` byte-for-byte.
- Modify only candidate `webassistant-contract/v0.3` / `webassistant-conformance/v0.3`; keep `status=candidate`, `accepted=false`.
- Every behavior block starts with a tests-only RED commit and exact-head failure evidence before production changes.
- Only one acquisition route remains: `POST /v1/scan` with JSON body. `scannerId` required; `source` default `auto`; `settings.duplex` default `false`.
- Public source strings are exactly lowercase `auto`, `flatbed`, `feeder`. Duplex is a boolean, not a source.
- `/v1/scan/feeder` and `/v1/scan/duplex` are removed, not aliases.
- Windows enumerates WIA and TWAIN independently and returns the union of usable endpoints. One failed backend cannot hide the other.
- Stable ID is `wa1-<backend>-<43-char full unpadded base64url SHA-256>` over UTF-8 `backend + "\0" + nativeId`; nativeId is byte-exact as received, with no trim/case-fold/Unicode normalization.
- Native IDs: WIA=`WIA_DIP_DEV_ID`; TWAIN=exact NAPS2-addressable source/ProductName; SANE=exact NAPS2 SANE device ID.
- Duplicate exact native IDs within one backend are fail-closed and never numbered by enumeration order.
- Auto on dual-source: `PRESENT->feeder`, `ABSENT->flatbed`, `UNKNOWN->flatbed`; feeder-only->feeder; flatbed-only->flatbed.
- `auto+duplex` and `flatbed+duplex` -> HTTP 400 before acquisition. Unsupported feeder/duplex -> HTTP 422 before acquisition.
- A syntactically valid scannerId whose indicated backend cannot currently be enumerated -> HTTP 503, not 404. 404 means the indicated backend was successfully enumerated and the endpoint is absent.
- Preserve raw `application/pdf`, multipage one-PDF semantics, single physical acquisition, cancellation, safe logging, loopback-only listener, and no long-term scan storage.
- WebAssistant stores no scanner/profile/default preferences.
- New immutable repository-owned SDK version is `WebAssistant.NAPS2.Sdk 1.3.0-webassistant.2.450cba65`; retain `.1.450cba65` unchanged.
- #163 bumps product VERSION exactly once, expected `0.3.21 -> 0.3.22` if base is unchanged. No Release publication in #163.
- #191 upgrade UX, #164 DPI/color/paper size, #192 installer localization, Linux GitLab CI/CD, final PDF/release remain out of scope.

---

## Responsibility Map

Create under `webassist/src/WebAssistant/Scanning/`:

```text
ScannerBackend.cs              enum Wia/Twain/Sane
PaperPresence.cs               Present/Absent/Unknown
ScannerSourceCapabilities.cs   flatbed/feeder/duplex booleans
ScannerEndpoint.cs             stable ID + name + backend + internal native ID + caps + paper state
ScannerDiscoveryWarning.cs     backend + machine-readable warning code
ScannerDiscoveryResult.cs      scanners + warnings + Unavailable
ScannerIdentity.cs             stable ID create/parse only
ScanSourcePolicy.cs             pure request validation/auto resolution
Naps2ScanSession.cs             injectable ScanController/PDF wrapper for Windows tests
```

Create `webassist/src/WebAssistant/Http/ScanRequest.cs` for wire DTOs. Modify `ScanCoordinator.cs`, `ScannerEndpointHandlers.cs`, `Program.cs` only for HTTP orchestration.

Vendor change is auditable through `webassist/vendor/naps2/patches/0001-feeder-paper-presence.patch`, `rebuild-fixed-sdk.sh`, README provenance, and the new `.nupkg`.

---

### Task 1: Authorize #163 in candidate v0.3

**Files:**
- Modify: `tests/core/DistributionContractCandidateTests.cs`
- Modify: `contracts/webassistant-contract-v0.3.json`
- Modify: `contracts/webassistant-conformance-v0.3.json`

**Produces:** candidate scanner requirements `WA-SCAN-001..007` and executable vectors while accepted v0.2 stays unchanged.

- [ ] **Step 1: Add tests-only RED for candidate semantics**

Add a test that requires candidate statements containing all of:

```text
WIA + TWAIN + scannerId
POST /v1/scan + source + duplex
PRESENT + ABSENT + UNKNOWN
caller-owned/stateless preferences
```

and rejects any candidate requirement statement containing `/v1/scan/feeder` or `/v1/scan/duplex`.

Use actual code:

```csharp
[Fact]
public void CandidateScannerContract_AuthorizesUnifiedStableAutoModel()
{
    using var document = JsonDocument.Parse(File.ReadAllText(GetContractPath()));
    var statements = document.RootElement.GetProperty("requirements")
        .EnumerateArray()
        .Select(x => x.GetProperty("statement").GetString() ?? string.Empty)
        .ToArray();

    Assert.Contains(statements, x => x.Contains("WIA", StringComparison.Ordinal) &&
                                      x.Contains("TWAIN", StringComparison.Ordinal) &&
                                      x.Contains("scannerId", StringComparison.Ordinal));
    Assert.Contains(statements, x => x.Contains("POST /v1/scan", StringComparison.Ordinal) &&
                                      x.Contains("source", StringComparison.Ordinal) &&
                                      x.Contains("duplex", StringComparison.Ordinal));
    Assert.Contains(statements, x => x.Contains("PRESENT", StringComparison.Ordinal) &&
                                      x.Contains("ABSENT", StringComparison.Ordinal) &&
                                      x.Contains("UNKNOWN", StringComparison.Ordinal));
    Assert.DoesNotContain(statements, x =>
        x.Contains("/v1/scan/feeder", StringComparison.Ordinal) ||
        x.Contains("/v1/scan/duplex", StringComparison.Ordinal));
}
```

- [ ] **Step 2: Run RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter 'FullyQualifiedName~CandidateScannerContract_AuthorizesUnifiedStableAutoModel'
```

Expected: FAIL because current v0.3 still specifies three source routes.

- [ ] **Step 3: Update only scanner requirements in `webassistant-contract-v0.3.json`**

Use these semantics:

```text
WA-SCAN-001  GET /v1/scanners returns deterministic persistent scannerId endpoints; Windows WIA and TWAIN are independently enumerated and not physically deduplicated.
WA-SCAN-002  POST /v1/scan is the only acquisition route; JSON requires scannerId; source defaults auto; settings.duplex defaults false; source-specific routes do not exist.
WA-SCAN-003  one physical acquisition at a time; competing request -> conflict. (retain)
WA-SCAN-004  one acquisition -> one multipage PDF; no long-term scan storage. (retain)
WA-SCAN-005  scanner operation exclusions. (retain)
WA-SCAN-006  auto uses PRESENT/ABSENT/UNKNOWN, never promotes UNKNOWN to PRESENT; explicit source has no fallback.
WA-SCAN-007  caller owns persistence of scannerId/source/settings; WebAssistant stores no mutable scanner profile/defaults.
```

Keep candidate status false.

- [ ] **Step 4: Update v0.3 conformance using only existing evidence paths at this stage**

Create/update vectors:

```text
WA-C-SCANNER-DISCOVERY-001 -> WA-SCAN-001 -> WindowsScanAdapterTests.cs + HttpScanContractTests.cs
WA-C-SCANNER-HTTP-001      -> WA-SCAN-002,003 -> HttpScanContractTests.cs + ScanAdapterContractTests.cs + PlatformEndToEndTests.cs
WA-C-SCANNER-AUTO-001      -> WA-SCAN-006 -> WindowsScanAdapterTests.cs + LinuxScanAdapterTests.cs
WA-C-SCANNER-STATELESS-001 -> WA-SCAN-007 -> HttpScanContractTests.cs + webassist/docs/api.md
```

Do not reference not-yet-created files in `requiredRepositoryPaths`; later tasks add them when they exist.

- [ ] **Step 5: Run candidate/conformance GREEN**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter 'FullyQualifiedName~DistributionContractCandidateTests|FullyQualifiedName~ConformanceGraphValidatorTests'
```

- [ ] **Step 6: Commit**

```bash
git add contracts/webassistant-contract-v0.3.json contracts/webassistant-conformance-v0.3.json tests/core/DistributionContractCandidateTests.cs
git commit -m "contracts: authorize unified scanner API candidate"
```

---

### Task 2: Add stable identity and normalized scanner domain

**Files:**
- Create: `ScannerBackend.cs`, `PaperPresence.cs`, `ScannerSourceCapabilities.cs`, `ScannerEndpoint.cs`, `ScannerDiscoveryWarning.cs`, `ScannerDiscoveryResult.cs`, `ScannerIdentity.cs` under `webassist/src/WebAssistant/Scanning/`
- Create: `tests/core/ScannerIdentityTests.cs`
- Modify: `contracts/webassistant-conformance-v0.3.json`

**Exact interfaces:**

```csharp
internal enum ScannerBackend { Wia, Twain, Sane }
internal enum PaperPresence { Present, Absent, Unknown }
internal sealed record ScannerSourceCapabilities(bool Flatbed, bool Feeder, bool Duplex);
internal sealed record ScannerEndpoint(string ScannerId, string Name, ScannerBackend Backend, string NativeId,
    ScannerSourceCapabilities Sources, PaperPresence FeederPaper);
internal sealed record ScannerDiscoveryWarning(ScannerBackend Backend, string Code);
internal sealed record ScannerDiscoveryResult(IReadOnlyList<ScannerEndpoint> Scanners,
    IReadOnlyList<ScannerDiscoveryWarning> Warnings, bool Unavailable);
```

`ScannerIdentity` exposes:

```csharp
internal static string Create(ScannerBackend backend, string nativeId);
internal static bool TryParse(string value, out ScannerBackend backend);
```

- [ ] **Step 1: Write tests-only RED**

Test deterministic equality, WIA/TWAIN namespace separation, exact zero separator, malformed IDs, and non-normalization:

```csharp
[Theory]
[InlineData(" native")]
[InlineData("native ")]
[InlineData("Native")]
public void Create_DoesNotNormalizeNativeIdentity(string altered)
{
    Assert.NotEqual(
        ScannerIdentity.Create(ScannerBackend.Wia, "native"),
        ScannerIdentity.Create(ScannerBackend.Wia, altered));
}
```

Also compute an expected digest inside the test from:

```csharp
SHA256.HashData(Encoding.UTF8.GetBytes("wia\0native-42"))
```

and assert the result is exactly `wa1-wia-` + full unpadded base64url digest. Require 51 chars for WIA IDs (`8+43`).

Malformed cases:

```text
empty
wa1-wia-
wa1-wia-short
wa1-unknown-<43 valid chars>
wa1-wia-<42 chars>
wa1-wia-<44 chars>
wa1-wia-<invalid '=' or '+' char>
```

- [ ] **Step 2: Run RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter 'FullyQualifiedName~ScannerIdentityTests'
```

Expected: compile failure because types do not exist.

- [ ] **Step 3: Implement identity exactly**

```csharp
var token = backend switch
{
    ScannerBackend.Wia => "wia",
    ScannerBackend.Twain => "twain",
    ScannerBackend.Sane => "sane",
    _ => throw new ArgumentOutOfRangeException(nameof(backend))
};
var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token + "\0" + nativeId));
var encoded = Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
return $"wa1-{token}-{encoded}";
```

`TryParse` must validate exact prefix + backend token + exactly 43 `[A-Za-z0-9_-]` characters. It validates syntax only; it cannot reverse SHA-256.

- [ ] **Step 4: Run focused + full core GREEN**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter 'FullyQualifiedName~ScannerIdentityTests'
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
```

- [ ] **Step 5: Add new paths to v0.3 conformance and commit**

```bash
git add webassist/src/WebAssistant/Scanning tests/core/ScannerIdentityTests.cs contracts/webassistant-conformance-v0.3.json
git commit -m "feat: add stable scanner identity model"
```

---

### Task 3: Produce immutable NAPS2 `.2` package with feeder-paper state

**Files:**
- Create: `webassist/vendor/naps2/patches/0001-feeder-paper-presence.patch`
- Modify: `webassist/vendor/naps2/rebuild-fixed-sdk.sh`
- Modify: `webassist/vendor/naps2/README.md`
- Add: `webassist/vendor/nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.2.450cba65.nupkg`
- Modify: `webassist/src/WebAssistant/WebAssistant.csproj`
- Modify: `tests/core/DependencyOwnershipTests.cs`
- Modify: `contracts/webassistant-conformance-v0.3.json`

**Produces:** `PaperSourceCaps.FeederHasPaper : bool?` with `true=PRESENT`, `false=ABSENT`, `null=UNKNOWN`.

- [ ] **Step 1: Write tests-only RED without weakening integrity checks**

Change `PackageVersion` and `PackageFile` in `DependencyOwnershipTests` to `.2.450cba65`, keep the existing hard-coded SHA assertion in place, and add:

```csharp
[Fact]
public void FixedSdk_ExposesNullableFeederPaperState()
{
    var property = typeof(NAPS2.Scan.PaperSourceCaps).GetProperty("FeederHasPaper");
    Assert.NotNull(property);
    Assert.Equal(typeof(bool?), property.PropertyType);
}
```

Do not remove or runtime-derive `ExpectedPackageSha256`.

- [ ] **Step 2: Run RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter 'FullyQualifiedName~DependencyOwnershipTests'
```

Expected: FAIL because `.2` package is absent and referenced SDK still lacks the property.

- [ ] **Step 3: Create an auditable patch against exact upstream `450cba65aaffe6387041050a573051a64cd80fe9`**

Patch only:

```text
NAPS2.Sdk/Scan/PaperSourceCaps.cs
NAPS2.Sdk/Scan/Internal/Wia/WiaScanDriver.cs
NAPS2.Sdk/Scan/Internal/Twain/LocalTwainController.cs
```

`PaperSourceCaps` gets:

```csharp
public bool? FeederHasPaper { get; init; }
```

WIA population:

```csharp
bool? feederHasPaper = null;
if (device.SupportsFeeder())
{
    var handlingStatus = device.Properties.GetOrNull(WiaPropertyId.DPS_DOCUMENT_HANDLING_STATUS);
    if (handlingStatus?.Value is int status)
    {
        feederHasPaper = (status & WiaPropertyValue.FEED_READY) != 0;
    }
}
```

TWAIN population only if `CapFeederLoaded.IsSupported`; readable True/False maps to bool, read failure/unsupported -> null. `CapAutomaticSenseMedium` alone never supplies PRESENT/ABSENT. SANE leaves null.

- [ ] **Step 4: Update rebuild script**

Set package version to `1.3.0-webassistant.2.450cba65`, then after exact upstream checkout run:

```bash
git -C "$work_dir" apply --check "$script_dir/patches/0001-feeder-paper-presence.patch"
git -C "$work_dir" apply "$script_dir/patches/0001-feeder-paper-presence.patch"
```

Keep exact upstream commit verification.

- [ ] **Step 5: Rebuild bytes, compute and pin exact SHA, switch product reference**

```bash
cd webassist
./vendor/naps2/rebuild-fixed-sdk.sh
PACKAGE='vendor/nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.2.450cba65.nupkg'
sha256sum "$PACKAGE"
```

Copy the printed lowercase digest into `ExpectedPackageSha256` as a literal constant. Update `WebAssistant.csproj` to `.2.450cba65`. README records exact upstream commit, patch path, package identity/version, and keeps the old `.1` package untouched.

- [ ] **Step 6: Run GREEN**

```bash
dotnet restore tests/core/WebAssistant.CoreTests.csproj
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter 'FullyQualifiedName~DependencyOwnershipTests|FullyQualifiedName~LinuxScanAdapterTests'
dotnet build webassist/src/WebAssistant/WebAssistant.csproj --configuration Release
```

- [ ] **Step 7: Commit**

```bash
git add webassist/vendor/naps2 webassist/vendor/nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.2.450cba65.nupkg webassist/src/WebAssistant/WebAssistant.csproj tests/core/DependencyOwnershipTests.cs contracts/webassistant-conformance-v0.3.json
git commit -m "deps: expose feeder paper presence from fixed NAPS2 SDK"
```

---

### Task 4: Add pure source policy and migrate shared adapter contract

**Files:**
- Modify: `webassist/src/WebAssistant/Scanning/ScanSource.cs`
- Create: `webassist/src/WebAssistant/Scanning/ScanSourcePolicy.cs`
- Modify: `webassist/src/WebAssistant/Scanning/IScanAdapter.cs`
- Modify: `webassist/src/WebAssistant/Scanning/LinuxScanAdapter.cs`
- Delete after migration: `webassist/src/WebAssistant/Scanning/ScannerDevice.cs`
- Create: `tests/core/ScanSourcePolicyTests.cs`
- Modify: `tests/core/ScanAdapterContractTests.cs`
- Modify: `tests/core/LinuxScanAdapterTests.cs`
- Modify: `tests/core/LinuxVirtualScanAdapterTests.cs`
- Modify: `contracts/webassistant-conformance-v0.3.json`

**Exact adapter contract:**

```csharp
internal interface IScanAdapter
{
    Task<ScannerDiscoveryResult> GetScannersAsync(CancellationToken cancellationToken = default);
    Task<Stream> ScanAsync(ScannerEndpoint scanner, ScanSource source, bool duplex,
        CancellationToken cancellationToken = default);
}
```

`ScanSource` becomes exactly `Auto, Flatbed, Feeder`.

- [ ] **Step 1: Write tests-only RED for full source table**

Create `ScanSourcePolicyTests` proving:

```text
dual + Present + Auto -> Feeder,false
dual + Absent  + Auto -> Flatbed,false
dual + Unknown + Auto -> Flatbed,false
feeder-only + Auto -> Feeder,false
flatbed-only + Auto -> Flatbed,false
no source + Auto -> UnsupportedScanCapabilityException
Auto + duplex -> ScanRequestValidationException
Flatbed + duplex -> ScanRequestValidationException
Feeder + duplex supported -> Feeder,true
Feeder + duplex unsupported -> UnsupportedScanCapabilityException
explicit unsupported Flatbed/Feeder -> UnsupportedScanCapabilityException
```

Define in `ScanSourcePolicy.cs`:

```csharp
internal sealed class ScanRequestValidationException(string message) : Exception(message);
internal sealed class UnsupportedScanCapabilityException(string message) : Exception(message);
internal sealed record ResolvedScanSource(ScanSource Source, bool Duplex);
```

- [ ] **Step 2: Run RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter 'FullyQualifiedName~ScanSourcePolicyTests'
```

- [ ] **Step 3: Implement pure policy with no NAPS2 references**

Implement exactly the table above. `UNKNOWN` cannot select feeder on a dual-source endpoint.

- [ ] **Step 4: Migrate Linux adapter**

Successful SANE enumeration maps every unambiguous native device to:

```text
Backend=Sane
NativeId=device.ID exact
ScannerId=ScannerIdentity.Create(Sane, device.ID)
Sources from ScanCaps.PaperSourceCaps
FeederPaper=Unknown
```

Duplicate exact SANE IDs are dropped with `ambiguousNativeIdentity` warning. SANE enumeration exception -> empty scanners + `enumerationFailed` warning + `Unavailable=true`. Successful empty enumeration -> `Unavailable=false`. Per-device caps failure -> omit that device + `capabilitiesUnavailable` warning, but enumeration remains available.

Linux `ScanAsync` receives only resolved `Flatbed`/`Feeder` and maps `(Feeder,true)` to NAPS2 `PaperSource.Duplex`; receiving `Auto` is an internal programming error and throws before acquisition.

- [ ] **Step 5: Run shared/Linux GREEN**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter 'FullyQualifiedName~ScanSourcePolicyTests|FullyQualifiedName~ScanAdapterContractTests|FullyQualifiedName~LinuxScanAdapterTests|FullyQualifiedName~LinuxVirtualScanAdapterTests'
```

- [ ] **Step 6: Commit**

```bash
git add webassist/src/WebAssistant/Scanning tests/core/ScanSourcePolicyTests.cs tests/core/ScanAdapterContractTests.cs tests/core/LinuxScanAdapterTests.cs tests/core/LinuxVirtualScanAdapterTests.cs contracts/webassistant-conformance-v0.3.json
git commit -m "feat: add deterministic scanner source policy"
```

---

### Task 5: Implement independently testable WIA+TWAIN Windows discovery

**Files:**
- Create: `webassist/src/WebAssistant/Scanning/Naps2ScanSession.cs`
- Modify: `webassist/src/WebAssistant/Scanning/WindowsScanAdapter.cs`
- Modify: `tests/core/WindowsScanAdapterTests.cs`
- Modify: `contracts/webassistant-conformance-v0.3.json`

**Injectable seam:**

```csharp
internal interface INaps2ScanSession : IDisposable
{
    Task<IReadOnlyList<ScanDevice>> GetDevicesAsync(Driver driver, CancellationToken cancellationToken);
    Task<ScanCaps> GetCapsAsync(ScanDevice device, CancellationToken cancellationToken);
    Task<Stream> ScanPdfAsync(Driver driver, ScanDevice device, PaperSource paperSource,
        CancellationToken cancellationToken);
}
```

Production `Naps2ScanSession` owns the existing GDI `ScanningContext`, `ScanController`, image disposal, and `PdfExporter`. `WindowsScanAdapter()` keeps the real Windows platform guard; `WindowsScanAdapter(INaps2ScanSession session)` is internal for off-Windows unit tests.

- [ ] **Step 1: Replace old preferred-driver tests with tests-only RED**

Use a fake session to prove:

```text
WIA one + TWAIN one -> two endpoints
same display name across WIA/TWAIN -> still distinct endpoints/IDs
reordered raw devices -> identical stable ID set
WIA throws + TWAIN succeeds -> usable result + warning(wia, enumerationFailed)
TWAIN throws + WIA succeeds -> usable result + warning(twain, enumerationFailed)
both throw -> Unavailable=true
duplicate exact native ID in a backend -> drop ambiguous group + ambiguousNativeIdentity
one device caps fails -> omit only that device + capabilitiesUnavailable
```

- [ ] **Step 2: Run RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter 'FullyQualifiedName~WindowsScanAdapterTests'
```

Expected: FAIL because current code uses `SelectPreferredDriver` and WIA suppresses TWAIN.

- [ ] **Step 3: Extract NAPS2 scan/PDF mechanics without semantic change**

Move current Windows image collection/PDF export into `Naps2ScanSession.ScanPdfAsync` so orchestration can be tested with a fake session.

- [ ] **Step 4: Implement per-backend independent discovery**

For WIA and TWAIN independently:

```text
GetDevicesAsync inside its own try/catch
enumeration throw -> enumerationFailed + backendFailed
success -> group by exact device.ID using StringComparer.Ordinal
count>1 group -> drop group + ambiguousNativeIdentity
single device -> GetCapsAsync
caps throw -> omit endpoint + capabilitiesUnavailable
caps success -> stable ID + source booleans + nullable paper map
```

Map SDK `bool? FeederHasPaper` to `Present/Absent/Unknown`. `Unavailable=true` only if both WIA and TWAIN enumeration calls failed; successful empty backend is success.

- [ ] **Step 5: Implement exact endpoint acquisition**

Use `scanner.Backend` to enumerate only WIA or only TWAIN. Find `device.ID == scanner.NativeId` ordinal, recompute stable ID and require equality, then map:

```text
Flatbed,false -> PaperSource.Flatbed
Feeder,false  -> PaperSource.Feeder
Feeder,true   -> PaperSource.Duplex
```

Never fallback across backends.

- [ ] **Step 6: Run unit + Windows virtual GREEN**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter 'FullyQualifiedName~WindowsScanAdapterTests'
```

On the Windows virtual scanner workflow also run `Category=WindowsVirtualScanner` and require PDF output from a returned `wa1-twain-*` endpoint.

- [ ] **Step 7: Commit**

```bash
git add webassist/src/WebAssistant/Scanning/Naps2ScanSession.cs webassist/src/WebAssistant/Scanning/WindowsScanAdapter.cs tests/core/WindowsScanAdapterTests.cs contracts/webassistant-conformance-v0.3.json
git commit -m "feat: enumerate Windows WIA and TWAIN independently"
```

---

### Task 6: Replace old HTTP scan surface with strict JSON contract

**Files:**
- Create: `webassist/src/WebAssistant/Http/ScanRequest.cs`
- Modify: `webassist/src/WebAssistant/Http/ScanCoordinator.cs`
- Modify: `webassist/src/WebAssistant/Http/ScannerEndpointHandlers.cs`
- Modify: `webassist/src/WebAssistant/Program.cs`
- Modify: `tests/core/HttpScanContractTests.cs`
- Modify: `tests/core/PlatformEndToEndTests.cs`
- Modify: `contracts/webassistant-conformance-v0.3.json`

**Wire DTO:**

```csharp
internal sealed record ScanRequest(string? ScannerId, string? Source, ScanSettingsRequest? Settings);
internal sealed record ScanSettingsRequest(bool? Duplex);
```

- [ ] **Step 1: Write tests-only RED for acquisition**

Require:

```text
{scannerId} -> Auto,false
{scannerId,source:flatbed} -> Flatbed,false
{scannerId,source:feeder,settings:{duplex:false}} -> Feeder,false
{scannerId,source:feeder,settings:{duplex:true}} -> Feeder,true
missing/blank scannerId -> 400
malformed wa1 ID -> 400
well-formed absent ID after successful target backend enumeration -> 404
target backend enumerationFailed -> 503 even if another backend succeeded
source=AUTO/glass/duplex/unknown -> 400
auto+duplex / flatbed+duplex -> 400
unsupported source/duplex -> 422
second concurrent acquisition -> 409
adapter acquisition failure -> 502
success -> raw application/pdf
/v1/scan/feeder -> 404
/v1/scan/duplex -> 404
query-only scannerId with no JSON scannerId -> 400
```

- [ ] **Step 2: Write tests-only RED for GET `/v1/scanners`**

Require envelope:

```json
{
  "scanners": [{
    "scannerId": "wa1-wia-...",
    "name": "Scanner",
    "backend": "wia",
    "sources": {"flatbed":true,"feeder":true,"duplex":true}
  }],
  "warnings": []
}
```

`NativeId` and `FeederPaper` must never appear. Partial warning + usable result -> 200. Successful empty -> 200. `Unavailable=true` -> 503.

- [ ] **Step 3: Run RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter 'FullyQualifiedName~HttpScanContractTests'
```

- [ ] **Step 4: Implement strict parsing before scanner resolution**

```csharp
var source = request.Source switch
{
    null => ScanSource.Auto,
    "auto" => ScanSource.Auto,
    "flatbed" => ScanSource.Flatbed,
    "feeder" => ScanSource.Feeder,
    _ => throw new ScanRequestValidationException("Некорректный source.")
};
var duplex = request.Settings?.Duplex ?? false;
```

Missing/blank ID -> 400. Call `ScannerIdentity.TryParse` and retain the parsed `targetBackend`; malformed -> 400.

- [ ] **Step 5: Resolve discovery failures correctly**

Coordinator sequence after acquiring the existing concurrency gate:

```text
validate request/scannerId
GetScannersAsync
Unavailable -> 503
lookup exact scannerId
if absent AND warnings contain (targetBackend, enumerationFailed) -> 503
if absent otherwise -> 404
ScanSourcePolicy.Resolve
ScanRequestValidationException -> 400
UnsupportedScanCapabilityException -> 422
adapter.ScanAsync
non-empty PDF -> 200 application/pdf
actual acquisition exception -> 502
```

This rule prevents a partial WIA failure from falsely turning a persisted `wa1-wia-*` ID into 404 while TWAIN still works.

- [ ] **Step 6: Serialize scanner list with explicit lowercase backend mapping**

Use explicit switch `Wia->"wia"`, `Twain->"twain"`, `Sane->"sane"`; warnings expose only `backend` and `code`.

- [ ] **Step 7: Replace `Program.cs` routes**

Keep only JSON `POST /v1/scan`; delete `/scan/feeder` and `/scan/duplex`; do not read scannerId from query string.

- [ ] **Step 8: Run HTTP/E2E GREEN and commit**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter 'FullyQualifiedName~HttpScanContractTests|FullyQualifiedName~PlatformEndToEndTests'
```

```bash
git add webassist/src/WebAssistant/Http webassist/src/WebAssistant/Program.cs tests/core/HttpScanContractTests.cs tests/core/PlatformEndToEndTests.cs contracts/webassistant-conformance-v0.3.json
git commit -m "feat: unify scanner acquisition HTTP API"
```

---

### Task 7: Synchronize docs and executable virtual-scanner consumers

**Files:**
- Modify: `webassist/docs/api.md`
- Modify: `webassist/README.md`
- Modify: `tests/core/HttpScanContractTests.cs`
- Modify: `tests/core/DistributionContractCandidateTests.cs`
- Modify: `tests/core/VirtualScannerWorkflowTests.cs`
- Modify only if actual old calls exist: `.github/workflows/virtual-scanner.yml`, `tests/core/PlatformEndToEndTests.cs`
- Modify: `contracts/webassistant-conformance-v0.3.json`

- [ ] **Step 1: Add docs/consumer RED assertions**

Require docs to contain `GET /v1/scanners`, `POST /v1/scan`, `scannerId`, `auto`, `flatbed`, `feeder`, `duplex`, `backend`, `sources`, and status codes `400/404/409/422/502/503`; reject old source routes.

Require virtual scanner consumers to GET scannerId then POST JSON to `/v1/scan`, never old routes.

- [ ] **Step 2: Run RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter 'FullyQualifiedName~HttpScanContractTests|FullyQualifiedName~DistributionContractCandidateTests|FullyQualifiedName~VirtualScannerWorkflowTests'
```

- [ ] **Step 3: Rewrite `api.md` with copy/pasteable examples**

Minimal:

```json
{"scannerId":"wa1-wia-..."}
```

Full:

```json
{"scannerId":"wa1-wia-...","source":"feeder","settings":{"duplex":true}}
```

Document UI mapping:

```text
Авто               -> auto,false
Стекло             -> flatbed,false
Лоток              -> feeder,false
Лоток двусторонний -> feeder,true
```

Document WIA/TWAIN as distinct endpoints even for one physical MFP, scannerId persistence semantics, legitimate ID change after native driver re-enumeration, partial-backend warning behavior, and exact status-code meanings.

- [ ] **Step 4: Update README and actual virtual scanner HTTP calls**

Remove WIA-first/fallback and old-route descriptions. Change only consumers proven to use the old API.

- [ ] **Step 5: Run full core + Linux/Windows virtual scanner GREEN**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
```

Require Linux SANE PDF and Windows TWAIN virtual PDF acceptance GREEN on exact head.

- [ ] **Step 6: Commit**

```bash
git add webassist/docs/api.md webassist/README.md tests/core/HttpScanContractTests.cs tests/core/DistributionContractCandidateTests.cs tests/core/VirtualScannerWorkflowTests.cs contracts/webassistant-conformance-v0.3.json .github/workflows/virtual-scanner.yml tests/core/PlatformEndToEndTests.cs
git commit -m "docs: document unified scanner API"
```

Omit unchanged optional paths from `git add`.

---

### Task 8: Single VERSION transition, Ready acceptance, real reboot evidence, exact-head merge

**Files:**
- Modify: `webassist/VERSION`
- Modify only if proven version-sensitive by failing tests: `tests/core/ReleaseInfrastructureTests.cs`
- No product source changes after Ready head freeze.

- [ ] **Step 1: Require pre-bump functional GREEN**

Run full core and inspect fresh Draft PR CI. Before VERSION change, repo-guard may fail only on monotonicity; read the actual log and stop if any other policy fails.

- [ ] **Step 2: Bump exactly once**

If base is still `0.3.21`, set `webassist/VERSION` exactly to:

```text
0.3.22
```

Run core; update only fixtures whose failing assertions explicitly model current `Version/BaseVersion` to `0.3.22/0.3.21`. Never bulk-replace historical evidence.

- [ ] **Step 3: Commit version transition and reach Draft GREEN**

```bash
git add webassist/VERSION tests/core/ReleaseInfrastructureTests.cs
git commit -m "release: advance WebAssistant to 0.3.22"
```

Omit the fixture path if unchanged. Require exact-head core + repo-guard GREEN before Ready.

- [ ] **Step 4: Final diff review**

Verify accepted v0.2, `repo-policy.json`, #191 installer-upgrade implementation, and #192 localization are untouched. Check no unresolved review threads and no accidental unrelated paths.

- [ ] **Step 5: Mark the same head Ready and require full classifier matrix**

Require fresh Ready runs for core, repo-guard, Linux scanner smoke/final, Windows scanner smoke/final, `ci-required`, and canonical installer build/lifecycle jobs selected by the change classifier. No source changes after these checks begin.

- [ ] **Step 6: Use exact Ready Windows installer for real identity evidence**

Record exact PR/head/VERSION/artifact SHA/provenance. Install those bytes, then before reboot:

```powershell
$before = Invoke-RestMethod http://127.0.0.1:17654/v1/scanners
$before | ConvertTo-Json -Depth 8
$before | ConvertTo-Json -Depth 8 | Set-Content .\scanners-before-reboot.json -Encoding UTF8
```

Require at least one `wa1-*`. If hardware exposes both WIA and TWAIN, capture both distinct endpoints.

- [ ] **Step 7: Reboot Windows and compare IDs**

After actual OS reboot/service startup:

```powershell
$after = Invoke-RestMethod http://127.0.0.1:17654/v1/scanners
$beforeJson = Get-Content .\scanners-before-reboot.json -Raw | ConvertFrom-Json
$beforeIds = @($beforeJson.scanners | ForEach-Object scannerId | Sort-Object)
$afterIds = @($after.scanners | ForEach-Object scannerId | Sort-Object)
Compare-Object $beforeIds $afterIds
```

Expected for unchanged installed endpoints: no output. If IDs changed, do not merge; record raw before/after and investigate native identity.

- [ ] **Step 8: Prove unified acquisition with the returned ID**

```powershell
$scannerId = $after.scanners[0].scannerId
$body = @{ scannerId = $scannerId; source = 'flatbed'; settings = @{ duplex = $false } } | ConvertTo-Json -Depth 4
Invoke-WebRequest http://127.0.0.1:17654/v1/scan -Method Post -ContentType 'application/json' -Body $body -OutFile .\scan.pdf
(Get-Item .\scan.pdf).Length
```

On feeder-capable hardware also test the supported feeder mode. For auto on dual-source hardware, capture paper-present and paper-absent cases when feasible. If the driver cannot expose paper state, record `UNKNOWN -> flatbed`; never claim sensor support that was not observed.

- [ ] **Step 9: Record evidence in #163 and merge exact head only**

Comment exact installer identity, before/after IDs, reboot fact, observed WIA/TWAIN endpoints, source/acquisition results, and evidence hashes. Re-check PR head/base/mergeability and live main, then merge with expected head SHA. Require fresh post-merge core/scanner CI.

- [ ] **Step 10: Handoff to #191; do not publish Release**

Update #160/#191 with merged main SHA, VERSION, exact scanner API, and physical evidence status. The next delivery candidate is built only after #191 in-place upgrade work advances VERSION again. Do not finalize PDF or publish `v0.3.22` from #163.
