# Scanner API stable identity and automatic source selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the current WIA-first/source-specific scanner API with one JSON `POST /v1/scan`, deterministic persistent scanner identities, independent WIA+TWAIN discovery, and WebAssistant-owned automatic source selection.

**Architecture:** Keep public orchestration and stable identity in WebAssistant. Extend the repository-owned NAPS2 SDK only with a nullable feeder-paper fact, then normalize WIA/TWAIN/SANE endpoints into a small domain model consumed by a pure source-selection policy and the HTTP coordinator. The accepted v0.2 authority remains untouched; the existing unaccepted v0.3 candidate is updated to authorize this semantic delta.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, NAPS2 SDK 1.3.0 repository-owned fork/package, NAPS2.Wia, NTwain, xUnit, GitHub Actions, repo-guard.

**Spec:** `docs/superpowers/specs/2026-09-10-scanner-api-stable-id-auto-design.md`

## Global Constraints

- Implementation base is `main = 77a5c66c431c746d2be2f283640c7951730911eb`, `webassist/VERSION = 0.3.21`; re-check live `main` immediately before execution. If it moved, stop and reconcile this plan before writing code.
- GitHub is the source of truth. Legacy `netkeep80/ScannerAgent` remains read-only.
- Preserve accepted `webassistant-contract/v0.2` and `webassistant-conformance/v0.2` byte-for-byte.
- Update only the existing `webassistant-contract/v0.3` / `webassistant-conformance/v0.3` candidate; keep `status = candidate`, `accepted = false`, and do not move `repo-policy.json.current` from v0.2.
- TDD is mandatory: each behavior block begins with a tests-only RED commit and recorded exact-head failure before production implementation.
- Public acquisition is exactly one `POST /v1/scan` JSON route. `scannerId` is required; `source` defaults to `auto`; `settings.duplex` defaults to `false`.
- Public source values are exactly lowercase `auto`, `flatbed`, `feeder`. Duplex is a separate boolean and never a source.
- `POST /v1/scan/feeder` and `POST /v1/scan/duplex` must be removed, not retained as aliases.
- Windows discovery enumerates WIA and TWAIN independently and returns the union of usable endpoints; one backend failure must not suppress the other.
- Stable IDs are `wa1-<backend>-<full-unpadded-base64url-sha256>`, where the digest input is UTF-8 `backend + "\0" + nativeId` using the exact native ID string with no trimming/case folding/Unicode normalization.
- WIA native identity is `WIA_DIP_DEV_ID`; TWAIN native identity is the exact NAPS2-addressable source/ProductName; SANE native identity is the exact NAPS2 SANE device ID.
- Duplicate exact native IDs inside one backend are fail-closed and never disambiguated using enumeration order.
- Auto on a dual-source endpoint maps feeder paper `PRESENT -> feeder`, `ABSENT -> flatbed`, `UNKNOWN -> flatbed`; feeder-only always resolves feeder and flatbed-only always resolves flatbed.
- `source=auto + duplex=true` and `source=flatbed + duplex=true` are HTTP 400 before acquisition. Unsupported feeder/duplex capability is HTTP 422 before acquisition.
- Preserve raw `application/pdf`, multipage-in-one-PDF, one-physical-acquisition-at-a-time, cancellation, safe logging, loopback-only listener, and no long-term scan storage.
- WebAssistant stores no selected scanner/profile/defaults; caller persistence is `scannerId + source + settings`.
- The repository-owned NAPS2 package receives a new immutable version `1.3.0-webassistant.2.450cba65`; do not overwrite or delete `1.3.0-webassistant.1.450cba65`.
- The #163 transition bumps product VERSION exactly once, expected `0.3.21 -> 0.3.22` if the base remains unchanged. Do not create/publish a GitHub Release in #163.
- #191 Windows upgrade UX, #164 DPI/color/paper-size schema, #192 installer localization, Linux GitLab CI/CD, and final PDF/publication remain out of scope.

---

## File Structure / Responsibility Map

New focused domain files under `webassist/src/WebAssistant/Scanning/`:

- `ScannerBackend.cs` — `Wia | Twain | Sane` backend identity.
- `PaperPresence.cs` — `Present | Absent | Unknown` tri-state.
- `ScannerSourceCapabilities.cs` — flatbed/feeder/duplex booleans.
- `ScannerEndpoint.cs` — stable public ID + display name + backend + internal native ID + current source capabilities/paper state.
- `ScannerDiscoveryWarning.cs` — machine-readable backend warning.
- `ScannerDiscoveryResult.cs` — normalized scanner list, warnings, and discovery-unavailable state.
- `ScannerIdentity.cs` — only stable ID creation/parsing logic.
- `ScanSourcePolicy.cs` — pure explicit/auto/duplex validation and concrete source resolution.
- `Naps2ScanSession.cs` — narrow injectable wrapper around `ScanController`/PDF export so Windows WIA/TWAIN orchestration is unit-testable off Windows.

HTTP files:

- `webassist/src/WebAssistant/Http/ScanRequest.cs` — JSON request DTO only.
- `webassist/src/WebAssistant/Http/ScanCoordinator.cs` — parse/resolve scanner, validate policy, serialize acquisition, map domain failures to HTTP.
- `webassist/src/WebAssistant/Http/ScannerEndpointHandlers.cs` — scanner-list envelope/warnings.
- `webassist/src/WebAssistant/Program.cs` — route surface only.

Vendor files:

- `webassist/vendor/naps2/patches/0001-feeder-paper-presence.patch` — auditable three-file NAPS2 source patch.
- `webassist/vendor/naps2/rebuild-fixed-sdk.sh` — apply patch and build new immutable package identity.
- `webassist/vendor/naps2/README.md` — exact upstream + patch + package provenance.
- `webassist/vendor/nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.2.450cba65.nupkg` — new package bytes; old package retained.

Primary tests:

- `tests/core/DistributionContractCandidateTests.cs`
- `tests/core/ScannerIdentityTests.cs` (new)
- `tests/core/ScanSourcePolicyTests.cs` (new)
- `tests/core/DependencyOwnershipTests.cs`
- `tests/core/ScanAdapterContractTests.cs`
- `tests/core/LinuxScanAdapterTests.cs`
- `tests/core/WindowsScanAdapterTests.cs`
- `tests/core/HttpScanContractTests.cs`
- `tests/core/PlatformEndToEndTests.cs`
- `tests/core/VirtualScannerWorkflowTests.cs`

---

### Task 1: Authorize the scanner semantic delta in candidate v0.3

**Files:**
- Modify: `tests/core/DistributionContractCandidateTests.cs`
- Modify: `contracts/webassistant-contract-v0.3.json`
- Modify: `contracts/webassistant-conformance-v0.3.json`

**Interfaces:**
- Consumes: accepted v0.2 remains immutable.
- Produces: candidate requirements/vectors authorizing unified scan JSON, stable scanner identity, WIA+TWAIN union, tri-state auto source, and stateless caller-owned preferences.

- [ ] **Step 1: Write tests-only RED assertions against the candidate contract**

Add a focused test that loads `webassistant-contract-v0.3.json` and requires the new scanner semantics while rejecting the superseded source-specific contract text:

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

Keep the existing candidate-status assertions requiring `candidate/false`.

- [ ] **Step 2: Run the focused test and record RED**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter 'FullyQualifiedName~DistributionContractCandidateTests.CandidateScannerContract_AuthorizesUnifiedStableAutoModel'
```

Expected: FAIL because v0.3 still states `/v1/scan`, `/v1/scan/feeder`, `/v1/scan/duplex` map glass/feeder/duplex.

- [ ] **Step 3: Update only candidate v0.3 scanner requirements**

Keep all non-scanner requirements unchanged. Replace scanner candidate semantics so they express:

```text
WA-SCAN-001: GET /v1/scanners returns deterministic persistent scannerId values and, on Windows, independently enumerates WIA + TWAIN endpoints without merging backend identities.
WA-SCAN-002: POST /v1/scan is the only acquisition route; JSON requires scannerId, source defaults auto, settings.duplex defaults false, and feeder/duplex routes do not exist.
WA-SCAN-003: at most one physical acquisition; concurrent request -> conflict. (unchanged)
WA-SCAN-004: one acquisition -> one multipage PDF, no long-term scan storage. (unchanged)
WA-SCAN-005: scanner capability exclusions. (unchanged)
WA-SCAN-006: auto source uses PRESENT/ABSENT/UNKNOWN and never treats UNKNOWN as PRESENT; explicit source never silently falls back.
WA-SCAN-007: WebAssistant owns no mutable user/scanner profile; caller persists and resends scannerId/source/settings.
```

Keep:

```json
"schema": "webassistant-contract/v0.3",
"status": "candidate",
"accepted": false
```

- [ ] **Step 4: Update candidate conformance vectors**

Replace the old scanner vector with explicit evidence vectors:

```text
WA-C-SCANNER-DISCOVERY-001
  requirements: WA-SCAN-001
  evidence: ScannerIdentityTests, WindowsScanAdapterTests, HttpScanContractTests

WA-C-SCANNER-HTTP-001
  requirements: WA-SCAN-002, WA-SCAN-003
  evidence: HttpScanContractTests, ScanAdapterContractTests, PlatformEndToEndTests

WA-C-SCANNER-AUTO-SOURCE-001
  requirements: WA-SCAN-006
  evidence: ScanSourcePolicyTests, WindowsScanAdapterTests, LinuxScanAdapterTests

WA-C-SCANNER-STATELESS-001
  requirements: WA-SCAN-007
  evidence: HttpScanContractTests, api.md
```

Add the new test/source paths to `requiredRepositoryPaths` only when those files exist later; at this task, reference only paths already present or create the new test path in the same RED commit if the conformance validator requires existence.

- [ ] **Step 5: Run candidate/conformance tests GREEN**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter 'FullyQualifiedName~DistributionContractCandidateTests|FullyQualifiedName~ConformanceGraphValidatorTests'
```

Expected: PASS while accepted v0.2 immutability tests remain PASS.

- [ ] **Step 6: Commit the authority delta**

```bash
git add contracts/webassistant-contract-v0.3.json \
        contracts/webassistant-conformance-v0.3.json \
        tests/core/DistributionContractCandidateTests.cs
git commit -m "contracts: authorize unified scanner API candidate"
```

---

### Task 2: Add deterministic scanner identity and normalized discovery types

**Files:**
- Create: `webassist/src/WebAssistant/Scanning/ScannerBackend.cs`
- Create: `webassist/src/WebAssistant/Scanning/PaperPresence.cs`
- Create: `webassist/src/WebAssistant/Scanning/ScannerSourceCapabilities.cs`
- Create: `webassist/src/WebAssistant/Scanning/ScannerEndpoint.cs`
- Create: `webassist/src/WebAssistant/Scanning/ScannerDiscoveryWarning.cs`
- Create: `webassist/src/WebAssistant/Scanning/ScannerDiscoveryResult.cs`
- Create: `webassist/src/WebAssistant/Scanning/ScannerIdentity.cs`
- Create: `tests/core/ScannerIdentityTests.cs`
- Modify: `contracts/webassistant-conformance-v0.3.json`

**Interfaces:**
- Produces:

```csharp
internal enum ScannerBackend { Wia, Twain, Sane }
internal enum PaperPresence { Present, Absent, Unknown }
internal sealed record ScannerSourceCapabilities(bool Flatbed, bool Feeder, bool Duplex);
internal sealed record ScannerEndpoint(
    string ScannerId,
    string Name,
    ScannerBackend Backend,
    string NativeId,
    ScannerSourceCapabilities Sources,
    PaperPresence FeederPaper);
internal sealed record ScannerDiscoveryWarning(ScannerBackend Backend, string Code);
internal sealed record ScannerDiscoveryResult(
    IReadOnlyList<ScannerEndpoint> Scanners,
    IReadOnlyList<ScannerDiscoveryWarning> Warnings,
    bool Unavailable);
internal static class ScannerIdentity
{
    internal static string Create(ScannerBackend backend, string nativeId);
    internal static bool TryParse(string value, out ScannerBackend backend);
}
```

- [ ] **Step 1: Write tests-only RED for stable ID semantics**

Create tests covering exact deterministic identity, order independence, backend namespace separation, byte-exact native input, and malformed IDs:

```csharp
[Fact]
public void Create_IsDeterministicAndBackendNamespaced()
{
    var first = ScannerIdentity.Create(ScannerBackend.Wia, "native-42");
    var second = ScannerIdentity.Create(ScannerBackend.Wia, "native-42");
    var twain = ScannerIdentity.Create(ScannerBackend.Twain, "native-42");

    Assert.Equal(first, second);
    Assert.StartsWith("wa1-wia-", first, StringComparison.Ordinal);
    Assert.StartsWith("wa1-twain-", twain, StringComparison.Ordinal);
    Assert.NotEqual(first, twain);
    Assert.Equal(51, first.Length); // "wa1-wia-" (8) + 43-char SHA-256 base64url
}

[Theory]
[InlineData("native")]
[InlineData(" native")]
[InlineData("native ")]
[InlineData("Native")]
public void Create_DoesNotNormalizeNativeIdentity(string nativeId)
{
    Assert.NotEqual(
        ScannerIdentity.Create(ScannerBackend.Wia, "native"),
        ScannerIdentity.Create(ScannerBackend.Wia, nativeId + "#different"));
}

[Theory]
[InlineData("")]
[InlineData("wa1-wia-")]
[InlineData("wa1-unknown-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
[InlineData("wa1-wia-short")]
public void TryParse_RejectsMalformedIdentity(string value)
{
    Assert.False(ScannerIdentity.TryParse(value, out _));
}
```

Also add an exact vector test using a known digest calculated in the test itself from `SHA256.HashData(Encoding.UTF8.GetBytes("wia\0native-42"))` so the implementation cannot use display name or omit the zero separator.

- [ ] **Step 2: Run and record RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter 'FullyQualifiedName~ScannerIdentityTests'
```

Expected: compile FAIL because the new domain/identity types do not yet exist.

- [ ] **Step 3: Implement the domain types and identity function**

Implement `ScannerIdentity.Create` exactly:

```csharp
internal static string Create(ScannerBackend backend, string nativeId)
{
    ArgumentException.ThrowIfNullOrEmpty(nativeId);
    var token = backend switch
    {
        ScannerBackend.Wia => "wia",
        ScannerBackend.Twain => "twain",
        ScannerBackend.Sane => "sane",
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
    };

    var bytes = Encoding.UTF8.GetBytes(token + "\0" + nativeId);
    var digest = SHA256.HashData(bytes);
    var encoded = Convert.ToBase64String(digest)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
    return $"wa1-{token}-{encoded}";
}
```

`TryParse` must require the exact `wa1-(wia|twain|sane)-` prefix plus exactly 43 base64url characters `[A-Za-z0-9_-]`; it validates syntax only and does not reverse the digest.

- [ ] **Step 4: Run identity tests GREEN and full core regression**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter 'FullyQualifiedName~ScannerIdentityTests'
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
```

Expected: all PASS.

- [ ] **Step 5: Add new paths to candidate conformance and commit**

Add all created domain/test paths to v0.3 `requiredRepositoryPaths`, then:

```bash
git add webassist/src/WebAssistant/Scanning \
        tests/core/ScannerIdentityTests.cs \
        contracts/webassistant-conformance-v0.3.json
git commit -m "feat: add stable scanner identity model"
```

---

### Task 3: Extend the repository-owned NAPS2 SDK with tri-state feeder presence

**Files:**
- Create: `webassist/vendor/naps2/patches/0001-feeder-paper-presence.patch`
- Modify: `webassist/vendor/naps2/rebuild-fixed-sdk.sh`
- Modify: `webassist/vendor/naps2/README.md`
- Add binary: `webassist/vendor/nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.2.450cba65.nupkg`
- Modify: `webassist/src/WebAssistant/WebAssistant.csproj`
- Modify: `tests/core/DependencyOwnershipTests.cs`
- Modify: `contracts/webassistant-conformance-v0.3.json`

**Interfaces:**
- Produces NAPS2 API:

```csharp
public bool? PaperSourceCaps.FeederHasPaper { get; init; }
```

Exact semantics: `true=PRESENT`, `false=ABSENT`, `null=UNKNOWN`.

- [ ] **Step 1: Write tests-only RED for the new immutable package**

Change `DependencyOwnershipTests` constants to:

```csharp
private const string PackageVersion = "1.3.0-webassistant.2.450cba65";
private const string PackageFile =
    "WebAssistant.NAPS2.Sdk.1.3.0-webassistant.2.450cba65.nupkg";
```

Add a reflection assertion after loading the SDK assembly from the package extraction/build output:

```csharp
var property = typeof(NAPS2.Scan.PaperSourceCaps).GetProperty("FeederHasPaper");
Assert.NotNull(property);
Assert.Equal(typeof(bool?), property.PropertyType);
```

Temporarily remove the old hard-coded SHA expectation only in this RED commit by changing the integrity assertion to require the new file path; restore a pinned exact SHA after building the package in Step 5.

- [ ] **Step 2: Run and record RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter 'FullyQualifiedName~DependencyOwnershipTests'
```

Expected: FAIL because the `.2` package does not exist and current referenced `PaperSourceCaps` lacks `FeederHasPaper`.

- [ ] **Step 3: Create an auditable upstream patch**

Create `0001-feeder-paper-presence.patch` against exact upstream `450cba65aaffe6387041050a573051a64cd80fe9` with only these semantic changes:

```diff
--- a/NAPS2.Sdk/Scan/PaperSourceCaps.cs
+++ b/NAPS2.Sdk/Scan/PaperSourceCaps.cs
@@
 public class PaperSourceCaps
 {
+    /// <summary>Current feeder paper state when the driver can report it.</summary>
+    public bool? FeederHasPaper { get; init; }
 }
```

In `NAPS2.Sdk/Scan/Internal/Wia/WiaScanDriver.cs`, compute nullable paper state only when feeder is supported and the document-handling status property is readable:

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

and assign it to `PaperSourceCaps.FeederHasPaper`.

In `NAPS2.Sdk/Scan/Internal/Twain/LocalTwainController.cs`, read only `CAP_FEEDERLOADED` when supported:

```csharp
bool? feederHasPaper = null;
if (supportsFeeder && ds.Capabilities.CapFeederLoaded.IsSupported)
{
    try
    {
        feederHasPaper = ds.Capabilities.CapFeederLoaded.GetCurrent() == BoolType.True;
    }
    catch
    {
        feederHasPaper = null;
    }
}
```

Assign the value to `PaperSourceCaps.FeederHasPaper`. Do not infer paper state from `CAP_AUTOMATICSENSEMEDIUM` alone. SANE leaves the nullable property unset (`null`).

- [ ] **Step 4: Update the reproducible rebuild script**

Set:

```bash
PACKAGE_VERSION="1.3.0-webassistant.2.450cba65"
```

After checking out the exact upstream commit and before changing package identity, run:

```bash
git -C "$work_dir" apply --check "$script_dir/patches/0001-feeder-paper-presence.patch"
git -C "$work_dir" apply "$script_dir/patches/0001-feeder-paper-presence.patch"
```

Update the embedded package-version replacement to `.2.450cba65`.

- [ ] **Step 5: Rebuild new package bytes, pin SHA, and update product reference**

Run from `webassist/` on a host with Git, Python 3, .NET SDK 10, and access to the public NAPS2 repository:

```bash
./vendor/naps2/rebuild-fixed-sdk.sh
PACKAGE="vendor/nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.2.450cba65.nupkg"
PACKAGE_SHA="$(sha256sum "$PACKAGE" | awk '{print $1}')"
echo "$PACKAGE_SHA"
```

Update `WebAssistant.csproj` to reference exactly `.2.450cba65`. Restore `ExpectedPackageSha256` in `DependencyOwnershipTests` to the exact printed lowercase digest by an explicit edit in the same commit; do not derive expected SHA at test runtime.

Update README provenance with:

```text
upstream commit = 450cba65aaffe6387041050a573051a64cd80fe9
repository patch = vendor/naps2/patches/0001-feeder-paper-presence.patch
package = WebAssistant.NAPS2.Sdk 1.3.0-webassistant.2.450cba65
```

Keep the `.1` nupkg in the repository unchanged.

- [ ] **Step 6: Run dependency and Linux build regressions GREEN**

```bash
dotnet restore tests/core/WebAssistant.CoreTests.csproj
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter 'FullyQualifiedName~DependencyOwnershipTests|FullyQualifiedName~LinuxScanAdapterTests'
dotnet build webassist/src/WebAssistant/WebAssistant.csproj --configuration Release
```

Expected: PASS; Linux still compiles against the nullable shared property.

- [ ] **Step 7: Commit package/provenance as one immutable dependency transition**

```bash
git add webassist/vendor/naps2 \
        webassist/vendor/nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.2.450cba65.nupkg \
        webassist/src/WebAssistant/WebAssistant.csproj \
        tests/core/DependencyOwnershipTests.cs \
        contracts/webassistant-conformance-v0.3.json
git commit -m "deps: expose feeder paper presence from fixed NAPS2 SDK"
```

---

### Task 4: Introduce pure source selection and migrate the adapter contract

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

**Interfaces:**
- `ScanSource` becomes exactly `Auto, Flatbed, Feeder`.
- `IScanAdapter` becomes:

```csharp
internal interface IScanAdapter
{
    Task<ScannerDiscoveryResult> GetScannersAsync(CancellationToken cancellationToken = default);
    Task<Stream> ScanAsync(
        ScannerEndpoint scanner,
        ScanSource source,
        bool duplex,
        CancellationToken cancellationToken = default);
}
```

- `ScanSourcePolicy.Resolve` produces a concrete `ScanSource` (`Flatbed` or `Feeder`) plus duplex flag or a typed validation/capability failure.

- [ ] **Step 1: Write tests-only RED for the complete decision table**

Create `ScanSourcePolicyTests` covering:

```csharp
[Theory]
[InlineData("Present", "Feeder")]
[InlineData("Absent", "Flatbed")]
[InlineData("Unknown", "Flatbed")]
public void Auto_DualSource_UsesTriStateRule(string paperName, string expectedName)
{
    var endpoint = Endpoint(flatbed: true, feeder: true, duplex: true,
        Enum.Parse<PaperPresence>(paperName));
    var resolved = ScanSourcePolicy.Resolve(endpoint, ScanSource.Auto, duplex: false);
    Assert.Equal(Enum.Parse<ScanSource>(expectedName), resolved.Source);
    Assert.False(resolved.Duplex);
}
```

Also require:

```text
Auto + duplex -> ScanRequestValidationException
Flatbed + duplex -> ScanRequestValidationException
Feeder + duplex when supported -> Feeder + true
Feeder + duplex unsupported -> UnsupportedScanCapabilityException
Explicit Flatbed unsupported -> UnsupportedScanCapabilityException
Explicit Feeder unsupported -> UnsupportedScanCapabilityException
Feeder-only Auto -> Feeder even with Unknown
Flatbed-only Auto -> Flatbed
No source -> UnsupportedScanCapabilityException
```

- [ ] **Step 2: Run and record RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter 'FullyQualifiedName~ScanSourcePolicyTests'
```

Expected: compile FAIL because new source model/policy do not exist.

- [ ] **Step 3: Implement pure policy types**

Use two small domain exceptions:

```csharp
internal sealed class ScanRequestValidationException(string message) : Exception(message);
internal sealed class UnsupportedScanCapabilityException(string message) : Exception(message);
internal sealed record ResolvedScanSource(ScanSource Source, bool Duplex);
```

`Resolve` contains no NAPS2 references and exactly implements the spec decision table.

- [ ] **Step 4: Migrate `IScanAdapter` and Linux adapter**

Linux discovery must:

```text
Driver.Sane device -> ScannerBackend.Sane
NativeId = exact device.ID
ScannerId = ScannerIdentity.Create(Sane, device.ID)
GetCaps(device) -> flatbed/feeder/duplex booleans
FeederPaper = Unknown for P0
```

Linux acquisition receives the resolved concrete source and maps:

```csharp
(ScanSource.Flatbed, false) => PaperSource.Flatbed
(ScanSource.Feeder, false) => PaperSource.Feeder
(ScanSource.Feeder, true) => PaperSource.Duplex
```

`ScanSource.Auto` must never reach the adapter; guard it with `ArgumentOutOfRangeException` because policy resolution belongs above the adapter.

- [ ] **Step 5: Rewrite adapter contract tests and run GREEN**

Tests must assert stable SANE ID derivation, source capabilities, no `ScannerDevice`, and exact source+duplex propagation.

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter 'FullyQualifiedName~ScanSourcePolicyTests|FullyQualifiedName~ScanAdapterContractTests|FullyQualifiedName~LinuxScanAdapterTests|FullyQualifiedName~LinuxVirtualScanAdapterTests'
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add webassist/src/WebAssistant/Scanning \
        tests/core/ScanSourcePolicyTests.cs \
        tests/core/ScanAdapterContractTests.cs \
        tests/core/LinuxScanAdapterTests.cs \
        tests/core/LinuxVirtualScanAdapterTests.cs \
        contracts/webassistant-conformance-v0.3.json
git commit -m "feat: add deterministic scanner source policy"
```

---

### Task 5: Replace WIA-first fallback with independent Windows WIA+TWAIN discovery

**Files:**
- Create: `webassist/src/WebAssistant/Scanning/Naps2ScanSession.cs`
- Modify: `webassist/src/WebAssistant/Scanning/WindowsScanAdapter.cs`
- Modify: `tests/core/WindowsScanAdapterTests.cs`
- Modify: `contracts/webassistant-conformance-v0.3.json`

**Interfaces:**
- New injectable seam:

```csharp
internal interface INaps2ScanSession : IDisposable
{
    Task<IReadOnlyList<ScanDevice>> GetDevicesAsync(Driver driver, CancellationToken cancellationToken);
    Task<ScanCaps> GetCapsAsync(ScanDevice device, CancellationToken cancellationToken);
    Task<Stream> ScanPdfAsync(
        Driver driver,
        ScanDevice device,
        PaperSource paperSource,
        CancellationToken cancellationToken);
}
```

Production `Naps2ScanSession` owns `ScanningContext`, `ScanController`, image disposal, and `PdfExporter` logic currently embedded in `WindowsScanAdapter`.

- [ ] **Step 1: Write tests-only RED for union discovery and stable IDs**

Replace the old `SelectPreferredDriver` tests with a fake `INaps2ScanSession` and require:

```text
WIA [wia-1] + TWAIN [twain-1] -> two endpoints
both same display name -> still two endpoints with distinct scannerId/backend
reversed enumeration order -> same scannerId values
WIA enumeration throws + TWAIN succeeds -> HTTP/domain result usable with warning(wia, enumerationFailed)
TWAIN throws + WIA succeeds -> usable with warning(twain, enumerationFailed)
both throw -> ScannerDiscoveryResult.Unavailable == true
duplicate exact TWAIN native ID -> no ambiguous endpoints + warning(twain, ambiguousNativeIdentity)
capability read failure for one endpoint -> exclude that endpoint + warning(backend, capabilitiesUnavailable)
```

A representative test:

```csharp
[Fact]
public async Task Discovery_ReturnsWiaAndTwainUnionWithoutPhysicalDeduplication()
{
    using var session = FakeNaps2ScanSession.Create(
        wia: [Device(Driver.Wia, "wia-native", "Same MFP")],
        twain: [Device(Driver.Twain, "twain-native", "Same MFP")]);
    using var adapter = new WindowsScanAdapter(session);

    var result = await adapter.GetScannersAsync();

    Assert.False(result.Unavailable);
    Assert.Equal(2, result.Scanners.Count);
    Assert.Contains(result.Scanners, x => x.Backend == ScannerBackend.Wia);
    Assert.Contains(result.Scanners, x => x.Backend == ScannerBackend.Twain);
    Assert.Equal(2, result.Scanners.Select(x => x.ScannerId).Distinct().Count());
}
```

- [ ] **Step 2: Run and record RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter 'FullyQualifiedName~WindowsScanAdapterTests'
```

Expected: FAIL because current code uses `SelectPreferredDriver` and suppresses TWAIN after non-empty WIA.

- [ ] **Step 3: Extract `Naps2ScanSession` without changing behavior**

Move the current `ScanningContext`/`ScanController`/PDF export mechanics into `Naps2ScanSession`. Keep the public production constructor:

```csharp
internal WindowsScanAdapter()
```

with Windows platform guard and GDI/Win32 worker setup, plus an internal test constructor:

```csharp
internal WindowsScanAdapter(INaps2ScanSession session)
```

that does not require Windows and owns/disposes the injected session exactly once.

- [ ] **Step 4: Implement independent backend enumeration**

For each of `Driver.Wia` and `Driver.Twain`:

1. call `GetDevicesAsync` independently inside its own `try/catch`;
2. if enumeration throws, add one `enumerationFailed` warning and mark that backend failed;
3. group successful raw devices by exact `device.ID`; for groups with count > 1, drop the entire group and add `ambiguousNativeIdentity`;
4. for each unambiguous device, call `GetCapsAsync`; if it fails, omit that endpoint and add `capabilitiesUnavailable`;
5. map nullable `caps.PaperSourceCaps?.FeederHasPaper` to `Present/Absent/Unknown`;
6. derive `ScannerIdentity.Create(backend, device.ID)`.

Set `Unavailable=true` only when both WIA and TWAIN enumeration operations failed. Successful empty enumeration is not failure.

- [ ] **Step 5: Implement exact acquisition resolution by endpoint backend/native ID**

`ScanAsync(ScannerEndpoint scanner, ScanSource concreteSource, bool duplex, ...)` must:

- reject backend other than WIA/TWAIN;
- enumerate only `scanner.Backend`;
- locate exact `device.ID == scanner.NativeId` using ordinal comparison;
- recompute `ScannerIdentity.Create(scanner.Backend, device.ID)` and require exact equality with `scanner.ScannerId`;
- map concrete source + duplex to NAPS2 `PaperSource`;
- call `session.ScanPdfAsync`.

No WIA→TWAIN or TWAIN→WIA fallback is permitted during acquisition.

- [ ] **Step 6: Run Windows unit + virtual scanner tests GREEN**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter 'FullyQualifiedName~WindowsScanAdapterTests'
```

Then on the Windows virtual-scanner job/environment:

```powershell
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter "Category=WindowsVirtualScanner"
```

Expected: union/stable-ID unit tests PASS and real virtual TWAIN PDF acquisition PASS.

- [ ] **Step 7: Commit**

```bash
git add webassist/src/WebAssistant/Scanning/Naps2ScanSession.cs \
        webassist/src/WebAssistant/Scanning/WindowsScanAdapter.cs \
        tests/core/WindowsScanAdapterTests.cs \
        contracts/webassistant-conformance-v0.3.json
git commit -m "feat: enumerate Windows WIA and TWAIN independently"
```

---

### Task 6: Replace source-specific HTTP routes with one strict JSON acquisition contract

**Files:**
- Create: `webassist/src/WebAssistant/Http/ScanRequest.cs`
- Modify: `webassist/src/WebAssistant/Http/ScanCoordinator.cs`
- Modify: `webassist/src/WebAssistant/Http/ScannerEndpointHandlers.cs`
- Modify: `webassist/src/WebAssistant/Program.cs`
- Modify: `tests/core/HttpScanContractTests.cs`
- Modify: `tests/core/PlatformEndToEndTests.cs`
- Modify: `contracts/webassistant-conformance-v0.3.json`

**Interfaces:**

```csharp
internal sealed record ScanRequest(string? ScannerId, string? Source, ScanSettingsRequest? Settings);
internal sealed record ScanSettingsRequest(bool? Duplex);
```

Do not bind `scannerId` from query string in the new route.

- [ ] **Step 1: Write tests-only RED for the new wire contract**

Replace old query/source-route tests with JSON requests using `StringContent`/`JsonContent` and require:

```text
POST /v1/scan {scannerId} -> source auto, duplex false
POST /v1/scan {scannerId, source:flatbed} -> flatbed
POST /v1/scan {scannerId, source:feeder, settings:{duplex:false}} -> feeder simplex
POST /v1/scan {scannerId, source:feeder, settings:{duplex:true}} -> feeder duplex
missing/blank scannerId -> 400
unknown/malformed wa1 scannerId -> malformed 400 or well-formed unresolved 404
source=AUTO / glass / duplex / unknown -> 400
source=auto + duplex=true -> 400
source=flatbed + duplex=true -> 400
unsupported explicit feeder/duplex -> 422
concurrent acquisition -> 409
adapter acquisition failure -> 502
successful acquisition -> raw application/pdf
POST /v1/scan/feeder -> 404
POST /v1/scan/duplex -> 404
query-only scannerId with no JSON scannerId -> 400
```

Update fake adapters to return `ScannerDiscoveryResult` and accept `ScannerEndpoint`.

- [ ] **Step 2: Write tests-only RED for scanner-list envelope/partial failure**

Require exact shape:

```json
{
  "scanners": [
    {
      "scannerId": "wa1-wia-...",
      "name": "Scanner",
      "backend": "wia",
      "sources": { "flatbed": true, "feeder": true, "duplex": true }
    }
  ],
  "warnings": []
}
```

And:

```text
Unavailable=false, one backend warning -> 200 + warning
Unavailable=false, zero scanners -> 200 + empty scanners
Unavailable=true -> 503
```

- [ ] **Step 3: Run and record RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter 'FullyQualifiedName~HttpScanContractTests'
```

Expected: multiple focused failures because current API uses query `scannerId`, glass default, and three routes.

- [ ] **Step 4: Implement strict request parsing**

In `ScanCoordinator`, parse `request.Source` with exact ordinal values only:

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

Validate `ScannerIdentity.TryParse` before adapter discovery. Missing/blank/malformed IDs return 400.

- [ ] **Step 5: Resolve current endpoint and source before acquisition**

Coordinator sequence:

```text
acquisition gate
-> validate JSON/scannerId syntax
-> adapter.GetScannersAsync()
-> if discovery unavailable: 503
-> exact scannerId lookup
-> if absent: 404
-> ScanSourcePolicy.Resolve(endpoint, requestedSource, duplex)
-> validation exception: 400
-> unsupported capability: 422
-> adapter.ScanAsync(endpoint, resolved.Source, resolved.Duplex)
-> verify non-empty PDF
-> 200 application/pdf
```

Keep current cancellation behavior and safe scanner ID/name logging.

- [ ] **Step 6: Replace scanner-list handler**

Serialize lowercase backend tokens using an explicit mapping function, not `Enum.ToString()`:

```csharp
ScannerBackend.Wia => "wia"
ScannerBackend.Twain => "twain"
ScannerBackend.Sane => "sane"
```

Serialize warnings with lowercase backend + stable `Code`; never expose `NativeId` or `FeederPaper`.

- [ ] **Step 7: Replace routes in `Program.cs`**

Keep only:

```csharp
api.MapPost("/scan", async (
    ScanRequest request,
    ScanCoordinator coordinator,
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    return await coordinator.ExecuteAsync(
        services.GetService<IScanAdapter>(),
        request,
        cancellationToken);
});
```

Delete mappings for `/scan/feeder` and `/scan/duplex`. Do not add query compatibility aliases.

- [ ] **Step 8: Run HTTP/E2E tests GREEN**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter 'FullyQualifiedName~HttpScanContractTests|FullyQualifiedName~PlatformEndToEndTests'
```

Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add webassist/src/WebAssistant/Http \
        webassist/src/WebAssistant/Program.cs \
        tests/core/HttpScanContractTests.cs \
        tests/core/PlatformEndToEndTests.cs \
        contracts/webassistant-conformance-v0.3.json
git commit -m "feat: unify scanner acquisition HTTP API"
```

---

### Task 7: Make documentation and executable conformance match the exact P0 API

**Files:**
- Modify: `webassist/docs/api.md`
- Modify: `webassist/README.md`
- Modify: `contracts/webassistant-conformance-v0.3.json`
- Modify: `tests/core/HttpScanContractTests.cs`
- Modify: `tests/core/DistributionContractCandidateTests.cs`

**Interfaces:**
- Produces the caller-facing contract used by the Triumf UI team.

- [ ] **Step 1: Add structural documentation assertions before editing docs**

Require `api.md` to contain all of:

```text
POST /v1/scan
scannerId
source
"auto"
"flatbed"
"feeder"
duplex
GET /v1/scanners
backend
sources
400
404
409
422
502
503
```

and to contain neither `/v1/scan/feeder` nor `/v1/scan/duplex`.

- [ ] **Step 2: Run documentation assertion RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter 'FullyQualifiedName~HttpScanContractTests|FullyQualifiedName~DistributionContractCandidateTests'
```

Expected: FAIL against old API documentation.

- [ ] **Step 3: Rewrite `api.md` to the actual wire contract**

Document a copy/pasteable minimal request:

```json
{
  "scannerId": "wa1-wia-..."
}
```

and full request:

```json
{
  "scannerId": "wa1-wia-...",
  "source": "feeder",
  "settings": {
    "duplex": true
  }
}
```

Document UI mapping:

```text
Авто               -> source=auto,    duplex=false
Стекло             -> source=flatbed, duplex=false
Лоток              -> source=feeder,  duplex=false
Лоток двусторонний -> source=feeder,  duplex=true
```

Document that WIA and TWAIN endpoints may both represent the same physical MFP and must be treated as distinct persisted endpoints. Document that native IDs are private and scannerId may legitimately change after driver reinstallation/re-enumeration that changes the backend-native ID.

Document `GET /v1/scanners` envelope and partial backend warnings exactly.

- [ ] **Step 4: Align top-level product README**

Remove references implying WIA-first/TWAIN fallback or source-specific acquisition routes. Link to `docs/api.md` for the exact wire schema.

- [ ] **Step 5: Run docs/contract/full core GREEN**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
```

Expected: all core tests PASS.

- [ ] **Step 6: Commit**

```bash
git add webassist/docs/api.md webassist/README.md \
        contracts/webassistant-conformance-v0.3.json \
        tests/core/HttpScanContractTests.cs \
        tests/core/DistributionContractCandidateTests.cs
git commit -m "docs: document unified scanner API"
```

---

### Task 8: Update virtual scanner acceptance and prove no Linux/Windows regression

**Files:**
- Modify: `tests/core/VirtualScannerWorkflowTests.cs`
- Modify when required by changed CLI/API calls: `.github/workflows/virtual-scanner.yml`
- Modify when required: `tests/core/PlatformEndToEndTests.cs`
- Do not modify installer/release workflows unless a failing test proves they consume the old scanner routes.

**Interfaces:**
- Produces automated executable evidence for real process startup + scanner enumeration/acquisition using the new IDs/API.

- [ ] **Step 1: Add RED assertions to virtual scanner workflow tests**

Require the workflow/acceptance logic to:

```text
GET /v1/scanners
extract a returned scannerId
POST /v1/scan with JSON body containing that scannerId and explicit source
verify application/pdf
never call /v1/scan/feeder or /v1/scan/duplex
```

For Windows TWAIN virtual scanner, also require returned `scannerId` starts `wa1-twain-`.

- [ ] **Step 2: Run and record RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release \
  --filter 'FullyQualifiedName~VirtualScannerWorkflowTests'
```

Expected: FAIL if current workflow still uses old routes/query IDs.

- [ ] **Step 3: Update only actual consumers of the old API**

Change HTTP calls to JSON bodies. Do not introduce test-only product routes or fallback aliases.

- [ ] **Step 4: Run full core, Linux virtual, and Windows virtual suites**

Core:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
```

Linux virtual scanner workflow must prove SANE discovery + PDF remains GREEN. Windows virtual scanner workflow must prove TWAIN endpoint discovery + stable-format ID + PDF remains GREEN.

- [ ] **Step 5: Commit**

```bash
git add tests/core/VirtualScannerWorkflowTests.cs \
        tests/core/PlatformEndToEndTests.cs \
        .github/workflows/virtual-scanner.yml
git commit -m "test: exercise unified scanner API end to end"
```

If `.github/workflows/virtual-scanner.yml` or `PlatformEndToEndTests.cs` required no change, omit unchanged paths from the commit.

---

### Task 9: Perform the single product VERSION transition and final Draft verification

**Files:**
- Modify: `webassist/VERSION`
- Modify version-sensitive current-transition fixtures only if exact test failures prove they track current VERSION, especially `tests/core/ReleaseInfrastructureTests.cs`.
- Do not alter historical evidence or staged `v0.3.21` Draft bytes.

**Interfaces:**
- Produces the #163 accepted-transition version, expected `0.3.22`.

- [ ] **Step 1: Confirm pre-bump Draft head is functionally GREEN**

Before touching VERSION:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
```

Also require fresh Draft PR `ci` and scanner jobs GREEN on the exact source head. At this point repo-guard may fail only on product-version monotonicity; inspect the actual log rather than assuming.

- [ ] **Step 2: Change VERSION exactly once**

If live base still has `0.3.21`, replace the sole content of `webassist/VERSION` with:

```text
0.3.22
```

No other version bump is permitted in #163.

- [ ] **Step 3: Synchronize only proven current-version fixtures**

Run full core. If a fixture such as `ReleaseInfrastructureTests.cs` fails because it intentionally models the current candidate transition, update its current `Version` / `BaseVersion` values to `0.3.22 / 0.3.21`. Do not bulk-replace historical `0.3.21` references.

- [ ] **Step 4: Run full core and repo-guard on exact head**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
```

Expected: core GREEN and repo-guard GREEN after ChangeIntent scope is exact.

- [ ] **Step 5: Commit final version transition**

```bash
git add webassist/VERSION tests/core/ReleaseInfrastructureTests.cs
git commit -m "release: advance WebAssistant to 0.3.22"
```

Omit `ReleaseInfrastructureTests.cs` if it did not require an edit.

---

### Task 10: Ready acceptance, real Windows reboot evidence, exact-head merge, and handoff to #191

**Files:**
- No product source changes after the Ready candidate head is frozen.
- GitHub issue/PR evidence only.

**Interfaces:**
- Consumes: exact Ready PR head for #163.
- Produces: physical scanner evidence and an exact merge commit; does not publish a Release.

- [ ] **Step 1: Final code/spec review before Ready**

Compare `main...HEAD` and verify the diff contains only #163 scope. Explicitly verify no changes to:

```text
contracts/webassistant-contract-v0.2.json
contracts/webassistant-conformance-v0.2.json
repo-policy.json
Windows installer upgrade behavior (#191)
installer localization (#192)
```

Check no unresolved PR review threads.

- [ ] **Step 2: Mark the same head Ready and require the full matrix**

Require fresh `ready_for_review` runs for:

```text
core
repo-guard
Linux scanner smoke/final
Windows scanner smoke/final
canonical Windows installer build/lifecycle when classifier requires it
canonical Linux installer build/lifecycle when classifier requires it
ci-required
```

No source commit is allowed merely to rerun a stale event snapshot; if PR metadata changed and repo-guard needs a new event, use the repository's established same-tree governance-only event strategy and verify tree identity.

- [ ] **Step 3: Download the exact Ready Windows installer artifact for physical acceptance**

Record:

```text
PR number
exact head SHA
VERSION 0.3.22
artifact name
artifact SHA-256/provenance
```

Do not rebuild locally for physical evidence.

- [ ] **Step 4: Capture real Windows scanner IDs before reboot**

On the target Windows machine with the exact Ready installer installed:

```powershell
$before = Invoke-RestMethod http://127.0.0.1:17654/v1/scanners
$before | ConvertTo-Json -Depth 8
$before | ConvertTo-Json -Depth 8 | Set-Content .\scanners-before-reboot.json -Encoding UTF8
```

Evidence must show at least one stable-format `wa1-*` ID. Where the target exposes both WIA and TWAIN for the same physical MFP, capture both endpoint entries and their distinct IDs.

- [ ] **Step 5: Reboot Windows and prove identity stability**

After an actual OS reboot and service startup:

```powershell
$after = Invoke-RestMethod http://127.0.0.1:17654/v1/scanners
$after | ConvertTo-Json -Depth 8
$beforeJson = Get-Content .\scanners-before-reboot.json -Raw | ConvertFrom-Json
$beforeIds = @($beforeJson.scanners | ForEach-Object scannerId | Sort-Object)
$afterIds = @($after.scanners | ForEach-Object scannerId | Sort-Object)
Compare-Object $beforeIds $afterIds
```

Expected for unchanged installed backend endpoints: `Compare-Object` prints no differences.

If IDs change, do not merge #163. Record the raw before/after endpoint data in #163 and investigate the native identity source.

- [ ] **Step 6: Prove unified acquisition on the physical scanner**

Choose one returned scanner ID and run:

```powershell
$scannerId = $after.scanners[0].scannerId
$body = @{ scannerId = $scannerId; source = 'flatbed'; settings = @{ duplex = $false } } |
    ConvertTo-Json -Depth 4
Invoke-WebRequest http://127.0.0.1:17654/v1/scan \
    -Method Post -ContentType 'application/json' -Body $body -OutFile .\scan.pdf
(Get-Item .\scan.pdf).Length
```

On a feeder-capable MFP, also exercise the UI-relevant feeder simplex/duplex mode supported by the device. For auto on a dual-source device, capture one practical case with feeder paper present and one with no feeder paper when feasible; if the physical driver cannot report state, capture the returned capabilities/observed `UNKNOWN -> flatbed` behavior instead of claiming sensor support.

- [ ] **Step 7: Record physical evidence in #163**

Comment with exact installer identity, head SHA, before/after IDs, reboot fact, backend union observation, acquisition result, and screenshots/log hashes where available. Do not mark a property as proven if the test hardware did not expose it.

- [ ] **Step 8: Merge only exact verified head**

Immediately re-check PR head/base/mergeability and `main`. Merge with ordinary merge commit using expected head SHA. After merge, require fresh push CI/core/scanner checks on the merge commit.

- [ ] **Step 9: Close #163 only after all acceptance items are actually proven**

If real WIA+TWAIN union cannot be physically observed on available hardware, keep that acceptance item explicitly unproven even if automated fixtures are GREEN; do not fabricate evidence. The issue may remain open for that evidence while code is merged only if the project governance explicitly allows the outstanding physical-evidence gate.

- [ ] **Step 10: Handoff to #191 without publishing v0.3.22**

Update #160/#191 with:

```text
#163 merged main SHA
VERSION 0.3.22
scanner API exact shape
physical evidence status
next task = Windows in-place upgrade with active WebAssistant/NAPS2.Worker
```

Do not create a final PDF or publish a Release here. The next canonical Windows installer intended for delivery is produced only after #191 completes and advances VERSION again according to repository policy.
