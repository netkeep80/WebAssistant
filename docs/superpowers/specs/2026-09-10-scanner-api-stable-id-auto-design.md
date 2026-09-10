# Unified scanner API, stable identity, and automatic source selection design

Issue: #163  
Parent roadmap: #161  
Reprioritization authority: #160  
Companion Windows upgrade work: #191  
Extended settings/capabilities after P0: #164

Design date: 2026-09-10

Base source reviewed:

```text
main = 77a5c66c431c746d2be2f283640c7951730911eb
VERSION = 0.3.21
```

## 1. Goal

Deliver one P0 scanner API suitable for the calling application to persist scanner choice and source preferences across WebAssistant restarts and Windows reboots, while exposing all usable Windows WIA and TWAIN endpoints rather than the current WIA-first/TWAIN-fallback view.

The P0 transaction must provide:

- one canonical `POST /v1/scan` acquisition route;
- required stable `scannerId`;
- independent WIA + TWAIN enumeration on Windows;
- explicit backend identity per endpoint;
- top-level `source = auto | flatbed | feeder`, defaulting to `auto`;
- `duplex` as a separate setting, defaulting to `false`;
- deterministic `auto` selection based on real source capabilities and tri-state paper presence;
- no mutable scanner profile/configuration stored by WebAssistant;
- preservation of raw PDF, multipage, single-acquisition concurrency, cancellation, logging, and loopback-only runtime behavior.

The deadline driving this scope is a canonical Windows installer containing this behavior by Monday 2026-09-14.

## 2. Current implementation problem

The current Windows adapter does not expose the required model:

```text
Get WIA devices
if WIA count > 0:
    use WIA only
else:
    get TWAIN devices
```

Therefore a machine with both WIA and TWAIN endpoints for the same physical MFP exposes only WIA. The current `ScannerDevice` contains only `Id` and `Name`, so the caller cannot know which backend owns the endpoint.

The current HTTP API also has three acquisition routes:

```text
POST /v1/scan
POST /v1/scan/feeder
POST /v1/scan/duplex
```

and `/v1/scan` defaults to glass. This route model is superseded by #163.

## 3. Research findings from the exact dependency stack

### 3.1 Vendored NAPS2 authority

WebAssistant currently owns a fixed SDK package:

```text
WebAssistant.NAPS2.Sdk
1.3.0-webassistant.1.450cba65
upstream source commit = 450cba65aaffe6387041050a573051a64cd80fe9
```

The package exists because the Linux SANE path requires an upstream fixed-point `WordList` correction. It is already repository-owned and reproducibly rebuildable from the pinned upstream source.

### 3.2 WIA native identity

At the pinned NAPS2 source, WIA enumeration creates `ScanDevice(Driver.Wia, id, name)` where:

```text
id = WIA_DIP_DEV_ID
name = WIA_DIP_DEV_NAME
```

`WiaDeviceManager.FindDevice(deviceID)` resolves the same native ID later for acquisition. This makes `WIA_DIP_DEV_ID` the correct backend-native address key for WebAssistant stable identity.

### 3.3 TWAIN native identity

At the pinned NAPS2 source, TWAIN enumeration currently constructs:

```text
new ScanDevice(Driver.Twain, ds.Name, ds.Name)
```

and later resolves the source using:

```text
ds.Name == options.Device.ID
```

Thus the backend address key exposed by the current NAPS2 layer is the TWAIN `ProductName`/source name.

NTwain also exposes the numeric `TW_IDENTITY.Id`, manufacturer, product family, and product name. The numeric ID is session-scoped and is not a valid persistent scanner identifier. WebAssistant must not use it for reboot-stable identity.

A consequence is unavoidable: if a TWAIN DSM exposes two simultaneously installed sources with the same exact addressable product name, the pinned NAPS2 API itself cannot unambiguously address them. WebAssistant must not invent a false stable identity using enumeration order.

### 3.4 Source capabilities

The pinned NAPS2 SDK already exposes through `ScanCaps.PaperSourceCaps`:

```text
SupportsFlatbed
SupportsFeeder
SupportsDuplex
CanCheckIfFeederHasPaper
```

WIA capabilities derive from `DPS_DOCUMENT_HANDLING_CAPABILITIES`. TWAIN capabilities derive from `CAP_FEEDERENABLED`, `CAP_DUPLEX`, and paper sensing capability support.

### 3.5 Paper-presence gap

The current public `PaperSourceCaps` exposes only whether a backend can determine feeder state. It does not expose the current state itself.

This is insufficient for WebAssistant's accepted tri-state contract:

```text
PRESENT
ABSENT
UNKNOWN
```

and especially for the invariant:

```text
UNKNOWN must never be interpreted as PRESENT
```

The underlying stacks already contain the required mechanisms:

- WIA: `DPS_DOCUMENT_HANDLING_STATUS` / `FEED_READY`, exposed by `NAPS2.Wia.WiaExtensions.FeederReady()`;
- TWAIN: `CAP_FEEDERLOADED` when readable;
- TWAIN `CAP_AUTOMATICSENSEMEDIUM` can ask the datasource to make an automatic choice during scanning, but does not give WebAssistant an explicit readable PRESENT/ABSENT fact.

Therefore strict WebAssistant-owned source selection requires a minimal extension of the repository-owned NAPS2 SDK.

## 4. Chosen architecture

Use WebAssistant as the orchestration and public-contract layer. Extend the repository-owned NAPS2 SDK only with the missing feeder-presence fact.

Do not duplicate WIA/TWAIN session machinery inside WebAssistant and do not delegate P0 source semantics blindly to `PaperSource.Auto`.

The layering is:

```text
HTTP /v1 API
    ↓
Scanner discovery + scannerId resolver
    ↓
normalized backend endpoint
    ↓
source-selection policy
    ↓
IScanAdapter / WindowsScanAdapter
    ↓
NAPS2 ScanController
    ↓
WIA or TWAIN
```

The NAPS2 extension is deliberately narrow:

```text
PaperSourceCaps.FeederHasPaper : bool?
```

with exact meaning:

```text
true  = PRESENT
false = ABSENT
null  = UNKNOWN
```

No WebAssistant HTTP types or stable-ID rules are added to NAPS2.

## 5. Normalized scanner endpoint model

Internally WebAssistant must distinguish public identity from backend-native addressability.

Conceptual endpoint:

```text
ScannerEndpoint
    ScannerId       stable public opaque ID
    Name            human-readable display name
    Backend         wia | twain | sane
    NativeId        backend address key, internal only
    SourceCaps      flatbed / feeder / duplex booleans
    FeederHasPaper  PRESENT | ABSENT | UNKNOWN, internal policy input
```

`NativeId` must not be exposed as the public persistence contract. The public `scannerId` is derived deterministically from it.

## 6. Stable scannerId

### 6.1 Canonical algorithm

Use a versioned deterministic namespace:

```text
wa1-wia-<digest>
wa1-twain-<digest>
wa1-sane-<digest>
```

where `<digest>` is the full unpadded base64url encoding of the 32-byte result:

```text
SHA-256( UTF8( backend + "\0" + nativeId ) )
```

Equivalent canonical inputs are:

```text
wia\0<WIA_DIP_DEV_ID>
twain\0<TWAIN ProductName/source name>
sane\0<NAPS2 SANE device ID>
```

The backend token is exactly lowercase `wia`, `twain`, or `sane`.

`nativeId` is the exact .NET string returned by the backend address surface. It is encoded as UTF-8 exactly as received. Do not trim it, case-fold it, normalize whitespace, apply Unicode normalization, or substitute the display name. This preserves the exact address key that the backend itself resolves and avoids collapsing distinct native endpoints. A backend change to that exact address key is a legitimate scanner-identity change.

The backend is included both in the digest input and in the readable prefix. This intentionally keeps WIA and TWAIN endpoints distinct even when they refer to one physical device.

`wa1` is the scanner identity algorithm version. It is independent of WebAssistant product VERSION. A normal WebAssistant upgrade must not change a `wa1-*` scanner ID.

### 6.2 Stability guarantees

For a backend endpoint whose backend-native address key remains unchanged, the public `scannerId` must remain identical across:

- repeated enumeration;
- enumeration-order changes;
- WebAssistant process restart;
- Windows service restart;
- Windows reboot;
- WebAssistant product upgrade that preserves `wa1`.

Legitimate native driver/device changes may change the ID, including driver reinstall/re-enumeration that changes `WIA_DIP_DEV_ID`, or TWAIN driver changes that change its addressable source name.

### 6.3 Prohibited identity inputs

Do not use any of these to create persistence identity:

```text
enumeration index
session-local TW_IDENTITY.Id
process counter
startup random GUID
boot-local state
hash of display name without backend namespace
```

### 6.4 Native-ID ambiguity

Within each backend enumeration, group endpoints by the exact backend-native address key that the backend/NAPS2 can later resolve.

If more than one live endpoint in one backend has the same exact address key, WebAssistant must not manufacture separate IDs by index or enumeration order. Such endpoints are not returned as independently usable scanner endpoints. Enumeration produces an `ambiguousNativeIdentity` warning for that backend, and acquisition cannot target those ambiguous instances through a stable `scannerId`.

TWAIN is the known concrete risk because the pinned NAPS2 layer uses product/source name as its address key. The same fail-closed rule nevertheless applies to WIA or SANE if a backend unexpectedly returns duplicate address keys.

This is fail-closed behavior, not silent deduplication based on physical-device guesses.

## 7. Windows enumeration semantics

WIA and TWAIN are independent discovery surfaces.

Canonical flow:

```text
enumerate WIA
enumerate TWAIN
normalize each usable endpoint
apply stable scannerId derivation
return union
```

Do not perform WIA-first suppression and do not merge WIA/TWAIN endpoints that appear to describe the same physical scanner.

### 7.1 Partial backend failure

Each backend enumeration result is classified independently as:

```text
success with endpoints
success with zero endpoints
failure
```

Response behavior:

- both backends successfully enumerated: HTTP 200, including an empty scanner list when no devices exist;
- one backend fails and the other succeeds: HTTP 200 with the successful endpoints plus a warning for the failed backend;
- both WIA and TWAIN fail: HTTP 503 because Windows scanner discovery is unavailable.

The endpoint must not silently hide a backend failure.

## 8. GET /v1/scanners wire contract

The minimum P0 response shape is:

```json
{
  "scanners": [
    {
      "scannerId": "wa1-wia-...",
      "name": "Samsung Scanner Class Driver",
      "backend": "wia",
      "sources": {
        "flatbed": true,
        "feeder": true,
        "duplex": true
      }
    },
    {
      "scannerId": "wa1-twain-...",
      "name": "Samsung Scanner",
      "backend": "twain",
      "sources": {
        "flatbed": true,
        "feeder": true,
        "duplex": true
      }
    }
  ],
  "warnings": []
}
```

Warnings have a stable machine-readable code and backend, for example:

```json
{
  "backend": "wia",
  "code": "enumerationFailed"
}
```

or:

```json
{
  "backend": "twain",
  "code": "ambiguousNativeIdentity"
}
```

P0 does not expose native IDs, serial numbers, raw driver metadata, DPI lists, color modes, or paper sizes through this route. Those belong to #164 unless needed internally for identity/source operation.

## 9. POST /v1/scan wire contract

The only P0 acquisition route is:

```text
POST /v1/scan
Content-Type: application/json
```

Full minimum request:

```json
{
  "scannerId": "wa1-wia-...",
  "source": "auto",
  "settings": {
    "duplex": false
  }
}
```

Minimal legal request:

```json
{
  "scannerId": "wa1-wia-..."
}
```

Defaults are immutable built-in API semantics:

```text
source = auto
settings.duplex = false
```

`scannerId` is required and may not be null, empty, or whitespace.

The superseded routes:

```text
POST /v1/scan/feeder
POST /v1/scan/duplex
```

must be removed. They must not remain hidden compatibility aliases.

The successful response remains raw:

```text
Content-Type: application/pdf
```

with one acquisition represented as one possibly multipage PDF.

## 10. Source-selection semantics

Public source values are exactly:

```text
auto
flatbed
feeder
```

Duplex is not a source.

### 10.1 Explicit flatbed

```text
source=flatbed
```

requires `SupportsFlatbed=true` and maps to NAPS2 `PaperSource.Flatbed`.

No feeder fallback is permitted.

### 10.2 Explicit feeder

```text
source=feeder
settings.duplex=false
```

requires `SupportsFeeder=true` and maps to NAPS2 `PaperSource.Feeder`.

An empty feeder, unsupported feeder, or acquisition error is reported explicitly. No flatbed fallback is permitted.

### 10.3 Duplex feeder

```text
source=feeder
settings.duplex=true
```

requires both feeder and duplex support and maps to NAPS2 `PaperSource.Duplex`.

### 10.4 Invalid combinations

Reject before physical acquisition:

```text
source=auto    + duplex=true
source=flatbed + duplex=true
```

Auto never decides simplex versus duplex on behalf of the caller.

## 11. Automatic source algorithm

WebAssistant owns the auto decision. It resolves a concrete NAPS2 source before acquisition rather than passing `PaperSource.Auto` and treating driver-dependent behavior as the API contract.

For a dual-source endpoint:

```text
PRESENT -> feeder
ABSENT  -> flatbed
UNKNOWN -> flatbed
```

General decision table:

```text
supports feeder + supports flatbed + PRESENT -> feeder
supports feeder + supports flatbed + ABSENT  -> flatbed
supports feeder + supports flatbed + UNKNOWN -> flatbed
supports feeder only                         -> feeder
supports flatbed only                        -> flatbed
no usable source                             -> error
```

`UNKNOWN` is never promoted to `PRESENT`.

No device-name heuristics, previous-scan state, last-used source, or timing heuristics are permitted.

## 12. Repository-owned NAPS2 extension

Create a new repository-owned NAPS2 package version; do not overwrite the existing package bytes or reuse the same package identity/version for changed source.

The source patch remains reproducible from the pinned upstream provenance and adds only the paper-presence fact needed by #163.

### 12.1 API extension

`PaperSourceCaps` receives:

```csharp
public bool? FeederHasPaper { get; init; }
```

### 12.2 WIA population

When feeder state can be read reliably, populate from WIA document handling status using `FEED_READY`.

If the relevant status property cannot be read, return `null`; do not turn a capability/read error into `false`.

### 12.3 TWAIN population

When `CAP_FEEDERLOADED` is supported and readable, map its current value to `true`/`false`.

When it is unsupported or cannot be read reliably, return `null`.

Support for `CAP_AUTOMATICSENSEMEDIUM` alone is not sufficient to claim PRESENT or ABSENT because that capability can delegate automatic selection without exposing the current state to WebAssistant.

### 12.4 Linux/SANE boundary

P0 is driven by Windows delivery, but the shared SDK/API type must remain binary/source coherent for Linux builds. SANE may leave `FeederHasPaper=null` unless its existing public stack can prove a current feeder state without introducing a new Linux-specific investigation.

Linux scanner behavior must not regress due to this shared SDK extension.

## 13. Acquisition resolution

A public `scannerId` is resolved statelessly for each request:

1. validate and parse the exact `wa1-<backend>-<digest>` format;
2. reject unsupported identity algorithm versions/backends rather than guessing;
3. enumerate the indicated backend;
4. normalize candidate native IDs without changing their exact value;
5. recompute their stable IDs with the same algorithm;
6. select the exact matching endpoint;
7. load current capabilities;
8. validate source/duplex;
9. resolve auto to a concrete source when requested;
10. acquire and export PDF.

WebAssistant does not persist a reverse mapping database. This avoids stale state and makes reboot/process stability a pure function of backend-native identity.

## 14. HTTP error semantics

Preserve existing `409 Conflict` for concurrent physical acquisition.

P0 normalized failures:

```text
400 Bad Request
    malformed JSON
    missing/blank scannerId
    malformed scannerId format
    unsupported scanner identity algorithm version/backend prefix
    invalid source enum
    auto + duplex=true
    flatbed + duplex=true

404 Not Found
    syntactically valid supported scannerId does not resolve to a live endpoint

409 Conflict
    another physical acquisition is already active

422 Unprocessable Entity
    scanner exists but requested flatbed/feeder/duplex capability is unsupported

502 Bad Gateway
    scanner/backend acquisition starts but driver/device operation fails
    scanner does not produce a valid PDF

503 Service Unavailable
    required scanner backend cannot be enumerated/used
    or all Windows discovery backends fail for GET /v1/scanners
```

Driver-specific details remain in safe logs; public errors must not leak uncontrolled driver strings or filesystem/process secrets.

## 15. Stateless configuration ownership

WebAssistant stores no mutable scanner preference:

```text
no selected scanner
no last-used source
no per-user profile
no per-scanner mutable defaults
no scanning preferences in appsettings.json
no scanning preferences in environment variables
no scanning preferences in CLI arguments
```

The calling application persists and resends:

```text
scannerId
source
settings
```

The only omitted-value behavior is the immutable API default defined by code/contract.

## 16. Compatibility and semantic authority

This is an observable scanner/API semantic delta. It must not be merged merely as implementation refactoring.

Before acceptance, the candidate contract/conformance authority must explicitly authorize:

- the new `GET /v1/scanners` descriptor/warning shape;
- stable scanner ID semantics;
- independent WIA/TWAIN enumeration;
- the JSON request body for `POST /v1/scan`;
- `source` and `duplex` defaults/validation;
- removal of `/v1/scan/feeder` and `/v1/scan/duplex`;
- deterministic auto-source behavior.

The currently accepted v0.2 authority is not silently rewritten.

## 17. Required automated evidence

TDD must include RED before production implementation for the existing wrong behavior.

At minimum tests must prove:

- current WIA-first/TWAIN-fallback enumeration fails the desired union contract;
- WIA and TWAIN endpoints with equal human names receive distinct scanner IDs;
- stable ID is independent of enumeration order;
- stable ID is deterministic across repeated resolver instances/process-equivalent construction;
- `scannerId` is derived only from canonical backend/native identity, not index;
- duplicate backend-native address keys fail closed and do not receive index-based IDs;
- one backend enumeration failure preserves endpoints from the other with a warning;
- both backend enumeration failures produce 503;
- malformed/unsupported `scannerId` formats produce 400 without backend guessing;
- old source-specific routes are absent;
- scannerId is required;
- omitted source becomes auto;
- omitted duplex becomes false;
- explicit flatbed never falls back;
- explicit feeder never falls back;
- feeder duplex requires support;
- auto + duplex and flatbed + duplex are rejected before acquisition;
- auto maps PRESENT/ABSENT/UNKNOWN exactly as specified;
- feeder-only and flatbed-only auto cases are deterministic;
- NAPS2 WIA feeder-state mapping returns true/false/null correctly;
- NAPS2 TWAIN feeder-state mapping returns true/false/null correctly;
- successful scan response remains raw PDF;
- existing busy/cancellation/logging constraints remain GREEN;
- Linux build/scanner tests remain GREEN after the shared SDK package change.

## 18. Required physical Windows evidence

Automated tests cannot prove actual driver persistence across a real reboot. Candidate acceptance therefore requires physical Windows evidence on the target machine.

Capture before reboot:

```text
GET /v1/scanners
```

Record each returned `scannerId`, backend, and name.

Then:

```text
restart WebAssistant service
GET /v1/scanners
compare IDs

reboot Windows
GET /v1/scanners
compare IDs
```

Where both WIA and TWAIN are exposed for the device, both endpoints must appear and each ID must remain stable independently.

The physical acceptance session must also demonstrate at least one real acquisition through the unified `POST /v1/scan` request. If the hardware supports feeder sensing, exercise `source=auto` both with and without paper when practical.

## 19. Documentation

`webassist/docs/api.md` must be updated in the same semantic transaction to contain the exact P0 wire contract, defaults, source semantics, response/error behavior, stable scanner identity guarantees, and examples suitable for the calling application.

The diagnostic UI may be adapted to the new API so it remains a useful smoke/evidence surface, but it is not the authority for scanner semantics.

## 20. Version/release boundary

The existing unpublished `v0.3.21` Draft remains an immutable historical distribution checkpoint. Do not mutate, overwrite, repack, or publish its installer bytes as the final scanner/API release after this semantic change.

The accepted #163 + #191 work must produce a new product VERSION and new canonical Windows installer candidate. The release build remains build-once/same-byte; no release-only rebuild is introduced.

## 21. Explicit non-goals

This P0 design does not implement:

- full DPI/color/paper-size capability schema from #164;
- arbitrary scan rectangle/crop API;
- brightness/contrast/threshold controls;
- OCR, deskew, editing, or post-processing;
- a physical-device abstraction that merges WIA and TWAIN into one endpoint;
- scanner preference persistence in WebAssistant;
- Linux GitLab CI/CD;
- Windows installer localization from #192;
- Windows in-place upgrade implementation from #191;
- final PDF publication/release finalization.

## 22. Acceptance summary

#163 is complete only when all of the following are true:

- one canonical `POST /v1/scan` JSON route exists;
- `scannerId` is required;
- public `scannerId` uses the documented deterministic `wa1` algorithm;
- real process/service restart and Windows reboot evidence confirms stable IDs;
- WIA and TWAIN are enumerated independently and returned as separate endpoints;
- duplicate unaddressable native identities fail closed;
- partial backend failure is deterministic and visible;
- `GET /v1/scanners` exposes backend and minimal source capabilities;
- top-level `source=auto|flatbed|feeder`, default `auto`;
- `duplex` is separate, default `false`;
- strict tri-state paper presence drives auto selection;
- explicit source never silently falls back;
- invalid duplex/source combinations are rejected before acquisition;
- old `/scan/feeder` and `/scan/duplex` routes are gone;
- NAPS2 repository-owned package provenance/version is updated without overwriting old package bytes;
- raw PDF, multipage, concurrency, cancellation, logging, and Linux regression tests remain GREEN;
- candidate contract/conformance explicitly authorizes the semantic delta;
- `api.md` exactly matches implementation;
- real Windows unified scan evidence is GREEN.
