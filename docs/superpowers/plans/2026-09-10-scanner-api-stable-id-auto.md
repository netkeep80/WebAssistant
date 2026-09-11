# #163 — implementation plan: unified scanner API

## Goal

Deliver the P0 scanner transaction approved in #163:

```text
GET  /v1/scanners
POST /v1/scan
```

with stable scanner IDs, independent Windows WIA + TWAIN discovery, and deterministic automatic source selection.

## Fixed boundaries

- GitHub is source of truth.
- Current accepted authority remains `webassistant-contract/v0.2` + `webassistant-conformance/v0.2` and is not edited.
- Existing `v0.3` stays `candidate / accepted=false` and is the only candidate updated.
- `repo-policy.json.current` stays on v0.2.
- #191 Windows upgrade, #164 extended settings, #192 localization, Linux GitLab CI/CD, final PDF/release are out of scope.
- No Release is published from this PR.
- TDD: tests-only RED must be observed on an exact head before each production behavior block.
- Product VERSION advances exactly once after implementation is GREEN; expected `0.3.21 -> 0.3.22` if base remains unchanged.

## Candidate supersession rule

Keep inheritance simple:

```text
v0.2 accepted files: immutable

v0.3 candidate:
  preserve every v0.2 requirement/vector verbatim
  EXCEPT the scanner semantics intentionally replaced by #163:
    WA-SCAN-001
    WA-SCAN-002
    WA-C-SCANNER-HTTP-001
```

New scanner requirements may be added to v0.3. No generic migration framework is introduced.

## Public API

`POST /v1/scan` accepts JSON:

```json
{
  "scannerId": "wa1-wia-...",
  "source": "auto",
  "settings": {
    "duplex": false
  }
}
```

Rules:

```text
scannerId        required
source           optional; auto | flatbed | feeder; default auto
settings         optional
settings.duplex  optional boolean; default false
```

Remove `/v1/scan/feeder` and `/v1/scan/duplex` rather than keeping aliases.

## Stable scanner identity

Canonical form:

```text
wa1-<backend>-<base64url(SHA256(UTF8(backend + "\0" + nativeId)))>
```

Backends:

```text
wia
 twain
 sane
```

The SHA-256 digest is complete and unpadded Base64URL (43 characters).

Native identity is used exactly as supplied: no trim, case fold, Unicode normalization, enumeration index, random GUID, or boot-local state.

Windows native identity:

```text
WIA   -> WIA_DIP_DEV_ID
TWAIN -> exact source/ProductName used by NAPS2 to address the datasource
```

Duplicate exact native identity inside one backend is fail-closed; never append an enumeration index.

## Discovery

Windows performs two independent operations:

```text
WIA enumeration
TWAIN enumeration
```

and returns the union of usable endpoints. WIA never suppresses TWAIN.

Each endpoint returns at least:

```json
{
  "scannerId": "...",
  "name": "...",
  "backend": "wia",
  "sources": {
    "flatbed": true,
    "feeder": true,
    "duplex": true
  }
}
```

A failure of one backend is reported as a warning while valid endpoints from the other backend remain usable. If both Windows backends are unavailable, discovery returns service unavailable.

For a syntactically valid `scannerId`:

```text
backend enumerated successfully + id absent -> 404
backend unavailable                     -> 503
```

## Source policy

Internal paper state:

```text
Present
Absent
Unknown
```

For a scanner with both feeder and flatbed:

```text
Present -> feeder
Absent  -> flatbed
Unknown -> flatbed
```

For feeder-only -> feeder. For flatbed-only -> flatbed.

Explicit source has no fallback.

Duplex remains a separate boolean:

```text
auto + duplex=true    -> 400
flatbed + duplex=true -> 400
feeder + duplex=true  -> allowed only when duplex capability exists
unsupported feeder or duplex -> 422 before acquisition
```

## Minimal SDK extension

The repository-owned NAPS2 SDK receives only one missing fact:

```csharp
bool? FeederHasPaper
```

Meaning:

```text
true  -> Present
false -> Absent
null  -> Unknown
```

WIA reads document-handling `FEED_READY` where available. TWAIN reads `CAP_FEEDERLOADED` where supported. `CAP_AUTOMATICSENSEMEDIUM` alone does not become a positive paper-presence claim.

Create a new immutable package identity:

```text
WebAssistant.NAPS2.Sdk 1.3.0-webassistant.2.450cba65
```

Retain the existing `.1.450cba65` package unchanged.

## TDD execution sequence

### 1. Candidate authority

RED:
- test requires WIA+TWAIN stable-ID semantics;
- test requires unified `POST /v1/scan` semantics;
- test requires PRESENT/ABSENT/UNKNOWN auto semantics;
- test rejects old source-specific routes.

GREEN:
- update only candidate v0.3 scanner statements;
- accepted v0.2 stays byte-for-byte unchanged.

### 2. Scanner identity domain

RED first in `tests/core/ScannerIdentityTests.cs` for:
- deterministic identity;
- backend namespace separation;
- exact zero separator;
- no native-ID normalization;
- malformed ID rejection;
- enumeration-order independence.

GREEN with small domain types under `webassist/src/WebAssistant/Scanning/` and `ScannerIdentity` only.

### 3. Repository-owned NAPS2 paper state

RED first in `DependencyOwnershipTests`:
- new immutable package expected;
- `PaperSourceCaps.FeederHasPaper` must exist as nullable boolean;
- integrity/provenance checks remain hard-coded and fail-closed.

GREEN:
- auditable source patch against pinned upstream commit;
- rebuild immutable `.2` package;
- pin exact SHA-256 in tests;
- update product package reference.

### 4. Pure source-selection policy

RED first in `ScanSourcePolicyTests` for all Present/Absent/Unknown, explicit-source and duplex combinations.

GREEN with a pure `ScanSourcePolicy` that resolves to a concrete acquisition source before driver execution.

### 5. Discovery/adapters

RED first for:
- Windows WIA+TWAIN union;
- one-backend failure preserves the other;
- duplicate native IDs fail closed;
- stable IDs derived from backend-native identity;
- endpoint capabilities exposed;
- Linux SANE keeps deterministic identity.

GREEN by adapting `IScanAdapter`, Windows and Linux adapters. Keep NAPS2 controller/PDF details behind a narrow testable session boundary only if required.

### 6. HTTP cutover

RED first in `HttpScanContractTests` for:
- required JSON `scannerId`;
- default `source=auto`;
- default `duplex=false`;
- lowercase source values;
- old routes return 404;
- malformed ID -> 400;
- absent endpoint -> 404;
- unavailable indicated backend -> 503;
- unsupported source/duplex -> 422;
- busy -> 409;
- successful response remains raw `application/pdf`.

GREEN by adding the request DTO and changing `Program.cs`, coordinator and scanner-list handler.

### 7. Candidate conformance + docs

Only after executable evidence exists:
- update v0.3 conformance vectors/required paths to the actual tests and implementation files;
- update `webassist/docs/api.md` to exact current wire behavior;
- keep v0.3 candidate/unaccepted.

### 8. Version and acceptance

After functional GREEN:
- bump `webassist/VERSION` exactly once;
- run full core;
- run repo-guard;
- run required Windows/Linux/virtual-scanner CI determined by classifier;
- mark PR Ready only when exact-head required checks are GREEN.

No installer publication in #163.

## Real Windows acceptance after merge candidate exists

On a real Windows machine:

```text
GET /v1/scanners
record scannerId values
restart WebAssistant process/service
GET /v1/scanners
compare IDs
reboot Windows
GET /v1/scanners
compare IDs again
```

Where available, verify the same physical MFP exposes both WIA and TWAIN endpoints and both remain distinct stable IDs.

Then exercise:

```text
auto with paper in feeder
auto without paper in feeder
explicit flatbed
explicit feeder simplex
explicit feeder duplex when supported
```

This physical evidence is required before #163 is considered fully accepted.
