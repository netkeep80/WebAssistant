# Distribution Installers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Реализовать distribution-only semantic candidate и два canonical versioned installer artifacts: Windows EXE и ALT Linux 10.1 ZIP, затем перевести CI на exact-artifact acceptance и выпускать те же bytes в GitHub Release.

**Architecture:** Пять execution scenarios сводятся к двум repository-owned producer entrypoints: `build/windows/package.bat` и `build/linux/package.sh`. Producer формирует final artifact, SHA-256 и provenance; lifecycle consumers получают уже собранный immutable artifact, проверяют identity и устанавливают его без повторного publish/package. Scanner/API semantics current accepted v0.2 остаётся неизменной; #161–#164 не входят в работу.

**Tech Stack:** .NET 10, PowerShell 5.1+/pwsh, Bash, WiX Toolset 7.0.0, Windows Installer/Burn, systemd, apt-rpm, GitHub Actions, xUnit, JSON contract/conformance.

**Spec:** `docs/superpowers/specs/2026-09-09-distribution-installer-candidate-design.md`

## Global Constraints

- Accepted `webassistant-contract/v0.2` и `webassistant-conformance/v0.2` не редактировать.
- Scanner/API redesign #161–#164 DEFERRED; product scanner source/routes не менять.
- `webassist/VERSION` — единственный product-version authority; каждый принимаемый product transition увеличивает его монотонно.
- Windows final filename: `WebAssistant-win-x64-<VERSION>.exe`.
- ALT final filename: `WebAssistant-linux-x64-<VERSION>.zip`.
- Filename VERSION == product metadata VERSION == installer/package metadata VERSION == release/tag VERSION.
- Source `src/WebAssistant/appsettings.json` present -> package exact bytes; absent -> producer generates safe default; installer never generates/replaces packaged config.
- Target installation не требует installed .NET Runtime/SDK, NuGet, compiler или build tools.
- ALT runtime distro dependencies may use apt-rpm; clean offline build #94 remains deferred.
- GitLab infrastructure #159 remains deferred; do not invent runner/container/registry details.
- Branch protection #3/#25 remains paused; use fixed-head discipline.
- Do not add environment-specific/publicly forbidden identities; sanitization policy must remain fail-closed.
- WiX build tool is pinned to stable `7.0.0`; no floating `latest` dependency. This is the latest non-prerelease GitHub release observed during plan preparation on 2026-09-09.
- User-facing Windows release surface is one EXE; internal MSI is build intermediate only.
- Lifecycle consumers must not call `dotnet publish`, `package.bat`, `package.sh`, or mutate payload after checksum.

---

## File Structure

New focused files expected during implementation:

- `contracts/<selected-distribution-candidate-contract>.json` — distribution-only semantic candidate; preserves v0.2 scanner/API requirements.
- `contracts/<selected-distribution-candidate-conformance>.json` — candidate vectors/evidence.
- `tests/core/DistributionContractCandidateTests.cs` — validates candidate pair without switching `repo-policy.json.current`.
- `tests/core/InstallerArtifactContractTests.cs` — static artifact naming/version/config/provenance rules.
- `webassist/build/common/default-appsettings.json` — repository-owned safe package default, copied only when source config is absent.
- `webassist/build/windows/installer/WebAssistant.Package.wixproj` — internal MSI project.
- `webassist/build/windows/installer/Package.wxs` — machine-wide payload/service MSI definition.
- `webassist/build/windows/installer/WebAssistant.Bundle.wixproj` — final EXE bundle project.
- `webassist/build/windows/installer/Bundle.wxs` — final user-facing EXE/Burn metadata.
- `webassist/build/windows/installer/Directory.Build.props` — pinned WiX `7.0.0` and shared version/payload properties if needed by both projects.
- `webassist/build/common/write-provenance.ps1` — deterministic Windows provenance writer.
- `webassist/build/common/write-provenance.sh` — deterministic Linux provenance writer.
- `tests/windows-service/run-installer-acceptance.ps1` — consumes an existing EXE + checksum/provenance and tests install/ARP/service/uninstall.
- `tests/linux-systemd/run-installer-acceptance.sh` — consumes an existing ZIP + checksum/provenance and tests ALT lifecycle.
- `.github/workflows/build-installers.yml` — reusable producer workflow; builds each OS artifact once and uploads immutable workflow artifacts.
- `.github/workflows/installer-acceptance.yml` — reusable consumer workflow; downloads and tests producer artifacts without rebuild.
- `.github/workflows/release.yml` — accepted-main release orchestration, same-byte publication.
- `webassist/docs/installation-guide.md` — editable repository-owned source for final installation guide.
- `webassist/docs/installation-guide-assets/` — real screenshots captured only after installer UX stabilizes.

Existing files to modify:

- `webassist/build/windows/package.bat`
- `webassist/build/windows/package.ps1`
- `webassist/build/linux/package.sh`
- `webassist/install/linux/install.sh`
- `tests/core/PackagingLayoutTests.cs`
- `tests/core/ConfigurationOwnershipTests.cs`
- `tests/core/SystemServiceProductTests.cs`
- `tests/core/CiWorkflowContractTests.cs`
- `tests/core/ChangePlanClassifierTests.cs`
- `ci/change-plan.sh`
- `.github/workflows/ci.yml`
- `.github/workflows/windows-service.yml`
- `.github/workflows/linux-systemd.yml`
- `webassist/README.md`
- `webassist/docs/windows-service.md`
- `webassist/docs/linux-service.md`
- `README.md` only where current distribution authority must be referenced after candidate acceptance/promotion.
- `webassist/VERSION` in every product-bearing accepted PR, following current monotonic policy.

Do not delete current `webassist/install/windows/*.ps1/*.bat` in the first installer slice because accepted v0.2 currently lists them as required evidence paths. They become non-canonical implementation history and can be removed only in a later candidate/current cleanup after the new pair is authoritative.

---

### Task 1: Record repository-identity sanitization as an independent bounded transaction

**Files:**
- Modify: `repo-policy.json`
- Test: repository-wide search + repo-guard policy tests already present

**Interfaces:**
- Consumes: current `content_rules[id=no-legacy-or-environment-identities]`.
- Produces: equivalent fail-closed regex rules with no literal environment/customer identity stored in repository text.

- [ ] **Step 1: Open a dedicated Issue and GovernanceGrant before editing policy**

Create a linked issue whose grant authorizes exactly:

```text
repo-policy.json
```

with:

```text
allow_policy_relaxation: []
```

The issue must describe the target generically as removal of literal environment/customer identifiers from policy text while retaining equivalent blocking behavior; do not repeat the forbidden literals in the issue body.

- [ ] **Step 2: Write a failing negative/positive policy probe**

Use the existing repo-guard test/probe mechanism to prove the current rule blocks synthesized strings built from fragments at runtime, without storing the forbidden complete strings in repository files. The test must assert both forbidden identities remain rejected after policy rewriting.

- [ ] **Step 3: Run the probe against current policy**

Expected: the behavioral probe is GREEN, while repository-wide literal search still reports the policy-file literals; this establishes the cleanup target without weakening enforcement.

- [ ] **Step 4: Replace literal regex tokens with equivalent composed regular expressions**

Change only the offending entries in `no-legacy-or-environment-identities`. Compose equivalent patterns using grouped fragments/character classes so the complete identities do not appear literally in repository text.

- [ ] **Step 5: Verify behavior and literal absence**

Run repo-guard validation and repository-wide case-insensitive search across tracked files. Expected: both synthesized forbidden identities still fail policy checks, and their complete literal spellings have zero tracked-file matches.

- [ ] **Step 6: Merge the bounded governance transaction fixed-head**

Require repo-guard GREEN, applicable CI GREEN, exact head, `behind_by=0`, then merge and reread `main`.

---

### Task 2: Select and authorize the distribution-only candidate pair

**Files:**
- Create later in Task 3: exact candidate contract/conformance paths selected here.
- Modify in this task: Issue #165 only; no repository files.

**Interfaces:**
- Consumes: accepted v0.2 pair and Issue #165.
- Produces: two exact candidate paths plus GovernanceGrant authorizing only those paths.

- [ ] **Step 1: Fresh-read contract namespace**

List `contracts/webassistant-contract-v*.json` and `contracts/webassistant-conformance-v*.json` from fresh `main`. Determine the next unused pair identity by monotonic repository convention. If any pair newer than v0.2 exists, STOP and reconcile rather than reusing/overwriting it.

- [ ] **Step 2: Record exact candidate filenames in #165**

Post the selected two exact paths and candidate schemas to #165. The candidate must use:

```text
status = candidate
accepted = false
```

and must not change `repo-policy.json.current`.

- [ ] **Step 3: Add GovernanceGrant to #165**

Authorize exactly the two selected new contract/conformance files:

```text
allow_policy_relaxation: []
```

No `repo-policy.json`, workflows or product paths are included in this grant.

- [ ] **Step 4: Verify authorization before file creation**

Run/check repo-guard governance authorization logic against the intended ChangeIntent. Expected: exact new pair paths authorized; unrelated governance paths rejected.

---

### Task 3: Create the distribution-only candidate contract/conformance pair

**Files:**
- Create: exact candidate contract selected in Task 2.
- Create: exact candidate conformance selected in Task 2.
- Create: `tests/core/DistributionContractCandidateTests.cs`
- Modify: `webassist/VERSION`

**Interfaces:**
- Consumes: exact pair identity/grant from Task 2; v0.2 requirements/vectors.
- Produces: candidate semantic authority for #155/#156 without becoming current accepted pair.

- [ ] **Step 1: Write the failing candidate validation test**

Create `DistributionContractCandidateTests.cs` that resolves the exact candidate paths recorded by Task 2 from a small test constant and asserts:

```text
contract.status == candidate
contract.accepted == false
conformance.status == candidate
conformance.accepted == false
conformance.contract == contract.schema
contract.conformanceCorpus == candidate conformance path
all v0.2 requirement IDs remain present with identical statements
all v0.2 scanner/API vectors remain present with identical assertions/requirements
new distribution requirement IDs are present
```

For the initial candidate-only PR, `requiredRepositoryPaths` references only already-existing evidence plus `tests/core/DistributionContractCandidateTests.cs`. Implementation PRs extend candidate conformance monotonically only after new evidence files exist.

- [ ] **Step 2: Run the test and observe failure**

Run core tests filtered to `DistributionContractCandidateTests`. Expected: FAIL because candidate files do not exist.

- [ ] **Step 3: Create the candidate by copying v0.2 semantics and adding atomic distribution requirements**

Add these requirement IDs and meanings:

```text
WA-DIST-001  five execution scenarios / two canonical producers
WA-DIST-002  versioned canonical filenames and VERSION identity equality
WA-DIST-003  package-time config selection and post-package immutability
WA-DIST-004  target self-contained with respect to .NET/SDK/NuGet/build tools
WA-WIN-INSTALL-001  Windows EXE elevation/machine-wide/service/ARP/uninstall
WA-ALT-INSTALL-001  ALT Linux 10.1 ZIP/systemd/apt-rpm runtime-dependency boundary
WA-ARTIFACT-001  build once / SHA-256+provenance / test exact bytes / no consumer rebuild or mutation
WA-RELEASE-001  accepted VERSION publishes the same tested versioned installer bytes
WA-INSTALL-DOC-001  one repository-owned installation guide PDF release artifact
```

Preserve every v0.2 scanner/network/API/filesystem/diagnostic/dependency statement byte-for-byte unless JSON formatting alone differs.

- [ ] **Step 4: Add candidate conformance vectors**

Add:

```text
WA-C-DIST-PRODUCERS-001
WA-C-DIST-VERSION-IDENTITY-001
WA-C-PACKAGE-CONFIG-001
WA-C-SELF-CONTAINED-TARGET-001
WA-C-WINDOWS-INSTALLER-001
WA-C-ALT-INSTALLER-001
WA-C-IMMUTABLE-ARTIFACT-001
WA-C-RELEASE-SAME-BYTES-001
WA-C-INSTALL-GUIDE-001
```

Initially anchor them to design/candidate tests where executable implementation evidence does not yet exist. Do not falsely claim lifecycle evidence before #155/#156 land.

- [ ] **Step 5: Increment `webassist/VERSION` once for the accepted candidate transaction**

Use the next semver value greater than fresh `main`, preserving current `major.minor.revision` convention. The candidate contract schema version is independent of product VERSION.

- [ ] **Step 6: Run core + repo-guard**

Expected: candidate validation GREEN; current accepted v0.2 baseline tests remain GREEN; repo-policy `current` still points to v0.2.

- [ ] **Step 7: Open/merge candidate PR fixed-head**

PR ChangeIntent scope: exact candidate files, candidate test, VERSION. No product/workflow implementation in this PR.

---

### Task 4: Define artifact/config/provenance contracts in tests before changing producers

**Files:**
- Create: `tests/core/InstallerArtifactContractTests.cs`
- Modify: `tests/core/PackagingLayoutTests.cs`
- Modify: `tests/core/ConfigurationOwnershipTests.cs`
- Modify: candidate conformance from Task 3
- Modify: `webassist/VERSION`

**Interfaces:**
- Consumes: candidate requirement IDs.
- Produces: executable static constraints used by both producer implementations.

- [ ] **Step 1: Add failing filename tests**

Tests must parse `webassist/VERSION` and assert producer scripts contain/construct exactly:

```text
WebAssistant-win-x64-$VERSION.exe
WebAssistant-linux-x64-$VERSION.zip
```

Reject versionless canonical output names.

- [ ] **Step 2: Add failing config-ownership tests for both OS producers**

Assert producer scripts look for `src/WebAssistant/appsettings.json`; if absent they copy from `build/common/default-appsettings.json`. Assert Linux `install.sh` contains no here-doc/default JSON generation and fails if packaged `appsettings.json` is missing.

- [ ] **Step 3: Add failing provenance contract tests**

Assert both producer paths emit:

```text
<artifact>.sha256
<artifact>.provenance.json
```

and provenance has fields:

```text
artifact
version
sourceSha
rid
sha256
size
sdkVersion
configMode
packageEntrypoint
```

`configMode` enum is exactly `source-appsettings | generated-default`.

- [ ] **Step 4: Add failing immutable-consumer tests**

Static tests must reject `dotnet publish`, `package.bat`, `package.sh` and payload writes in future installer acceptance consumer scripts.

- [ ] **Step 5: Run filtered tests**

Expected: FAIL against current directory-package implementation.

- [ ] **Step 6: Commit tests only**

Do not make producers GREEN in the same commit; preserve TDD evidence.

---

### Task 5: Implement repository-owned safe default configuration once

**Files:**
- Create: `webassist/build/common/default-appsettings.json`
- Modify: `webassist/build/windows/package.ps1`
- Modify: `webassist/build/linux/package.sh`
- Modify: `webassist/install/linux/install.sh`
- Modify: `tests/core/ConfigurationOwnershipTests.cs`

**Interfaces:**
- Consumes: package config contract from Task 4.
- Produces: one cross-platform safe default source and package-time config semantics.

- [ ] **Step 1: Create safe default JSON**

Exact content:

```json
{
  "WebAssistant": {
    "Port": 17654,
    "LogDirectory": "",
    "Cors": {
      "Enabled": false,
      "AllowedOrigins": []
    },
    "FileSystem": {
      "RootDirectory": ""
    }
  }
}
```

`WebAssistantRuntimeOptions` already treats empty `LogDirectory` and `FileSystem:RootDirectory` as platform defaults (`ProgramData` paths on Windows, `/var/log/webassistant` and `/var/lib/webassistant` on Linux), so this file is cross-platform and contains no deployment-specific values.

- [ ] **Step 2: Make Windows producer select config before final packaging**

In `package.ps1`, after `dotnet publish` and before WiX build, copy source `appsettings.json` into publish payload when present; otherwise copy the repository safe default. Record `configMode`.

- [ ] **Step 3: Make Linux producer select config before ZIP creation**

Apply the same exact decision and `configMode` in `package.sh`.

- [ ] **Step 4: Remove Linux install-time default generation**

Replace the here-doc with:

```bash
[[ -f "$source_app/appsettings.json" ]] || {
    echo "Package повреждён: отсутствует appsettings.json" >&2
    exit 1
}
```

then copy payload byte-for-byte.

- [ ] **Step 5: Add byte-preservation fixture tests**

Create a temporary isolated product root, inject a harmless synthetic source config, run the canonical producer far enough to stage payload, compare SHA-256 source vs staged/installed config. Add absent-source fixture proving generated safe default exists.

- [ ] **Step 6: Run configuration + packaging tests**

Expected: config tests GREEN; unrelated scanner tests unchanged.

---

### Task 6: Implement ALT Linux 10.1 versioned ZIP producer

**Files:**
- Modify: `webassist/build/linux/package.sh`
- Create: `webassist/build/common/write-provenance.sh`
- Modify: `tests/core/InstallerArtifactContractTests.cs`
- Modify: `tests/core/SystemServiceProductTests.cs`
- Modify: candidate conformance
- Modify: `webassist/VERSION`

**Interfaces:**
- Consumes: staged self-contained app + package-owned config from Task 5.
- Produces: `WebAssistant-linux-x64-<VERSION>.zip`, `.sha256`, `.provenance.json`.

- [ ] **Step 1: Write failing executable packaging test for ZIP layout**

On Linux-capable CI, invoke `build/linux/package.sh <output-dir>` and assert exactly one final ZIP matching VERSION exists plus evidence files. Unzip to temp and assert:

```text
app/WebAssistant executable
app/appsettings.json
VERSION
install.sh executable
uninstall.sh executable
webassist.service
```

- [ ] **Step 2: Refactor package staging**

Use a temporary staging directory under output root, never the final ZIP path. Preserve executable mode bits and LF endings.

- [ ] **Step 3: Create ZIP**

Require `zip` on the builder with an explicit presence check. GitHub CI installs `zip` as a builder dependency when absent. The target machine does not need `zip` after the user has unpacked the distribution. Final name is constructed from validated VERSION.

- [ ] **Step 4: Generate SHA-256 and provenance after final ZIP closes**

`write-provenance.sh` computes hash and byte size from final ZIP, reads SDK version from the selected `dotnet_command`, takes source SHA from `WEBASSISTANT_SOURCE_SHA` when supplied by CI and otherwise records `unknown` for manual builds, and records `packageEntrypoint=build/linux/package.sh`.

- [ ] **Step 5: Ensure final artifact is immutable after hashing**

No write to ZIP/staging-derived final bytes after evidence generation. Recompute hash at script end and fail if different from recorded hash.

- [ ] **Step 6: Run Linux package tests**

Expected: ZIP/evidence tests GREEN; no .NET requirement on target payload.

---

### Task 7: Convert Linux lifecycle acceptance into a pure artifact consumer

**Files:**
- Create: `tests/linux-systemd/run-installer-acceptance.sh`
- Modify: `.github/workflows/linux-systemd.yml`
- Modify: `tests/core/SystemServiceProductTests.cs`
- Modify: `tests/core/InstallerArtifactContractTests.cs`

**Interfaces:**
- Consumes: existing ZIP + `.sha256` + provenance.
- Produces: lifecycle verdict without calling package producer.

- [ ] **Step 1: Copy current lifecycle assertions into new consumer harness**

Keep service user, dependency, health, loopback, restart and uninstall assertions.

- [ ] **Step 2: Replace producer invocation with evidence verification**

Inputs:

```text
ZIP path
SHA-256 file path
provenance path
```

Verify recorded filename/version/hash/size before unzip.

- [ ] **Step 3: Assert no rebuild/mutation commands in consumer**

Core test rejects producer/publish tokens and rejects writes into the ZIP itself.

- [ ] **Step 4: Update workflow to build once then consume exact ZIP**

Until shared `build-installers.yml` lands, the workflow contains a producer job that uploads the artifact and a consumer job that downloads it. Packaging never occurs inside the consumer job.

- [ ] **Step 5: Keep ALT target naming honest**

Do not merely rename p11 to ALT 10.1. Replace image/environment only after an actually verified ALT Linux 10.1 builder/runtime image is identified. If no trustworthy public image is available, keep p11 regression job explicitly historical and add a separate ALT 10.1 acceptance job backed by the verified environment. Do not claim target acceptance from p11.

- [ ] **Step 6: Run lifecycle acceptance**

Expected: exact ZIP hash verified before install; systemd lifecycle GREEN.

---

### Task 8: Implement Windows WiX 7 internal MSI + final EXE bundle

**Files:**
- Create: `webassist/build/windows/installer/Directory.Build.props`
- Create: `webassist/build/windows/installer/WebAssistant.Package.wixproj`
- Create: `webassist/build/windows/installer/Package.wxs`
- Create: `webassist/build/windows/installer/WebAssistant.Bundle.wixproj`
- Create: `webassist/build/windows/installer/Bundle.wxs`
- Modify: `webassist/build/windows/package.ps1`
- Create: `webassist/build/common/write-provenance.ps1`
- Modify: tests from Task 4
- Modify: candidate conformance
- Modify: `webassist/VERSION`

**Interfaces:**
- Consumes: staged self-contained Win-x64 payload with package-owned config.
- Produces: `WebAssistant-win-x64-<VERSION>.exe`, `.sha256`, `.provenance.json`.

- [ ] **Step 1: Add failing static WiX structure tests**

Assert pinned `WixToolset.Sdk/7.0.0`, internal MSI project, bundle project, machine-wide scope, service install/control, Program Files target, version propagation, and bundle output EXE.

- [ ] **Step 2: Implement internal MSI project**

`Package.wxs` installs staged payload under `ProgramFilesFolder\WebAssistant`, installs `WebAssistant` Windows Service with automatic start, controls stop/remove on uninstall, and does not generate runtime config. MSI version comes from validated `webassist/VERSION` passed by `package.ps1`.

- [ ] **Step 3: Implement final Burn bundle**

`Bundle.wxs` embeds/chains the internal MSI, requests per-machine elevation, registers one WebAssistant uninstall entry, uses VERSION as bundle version, and hides the nested MSI from the user-facing uninstall surface using the supported WiX package visibility setting.

- [ ] **Step 4: Pin WiX build dependency**

Use `WixToolset.Sdk` exactly `7.0.0`. `package.ps1` uses `dotnet build` on the `.wixproj` projects after application publish; no floating WiX install command.

- [ ] **Step 5: Emit only final EXE as canonical Windows artifact**

MSI/output intermediates stay in temporary/intermediate output and are not canonical release assets. Copy only final versioned EXE to the requested artifact output directory.

- [ ] **Step 6: Generate and verify SHA-256/provenance**

`write-provenance.ps1` records artifact/version/sourceSha/rid/hash/size/sdkVersion/configMode/packageEntrypoint. Recompute final hash before success.

- [ ] **Step 7: Run Windows packaging tests**

Expected: final EXE exists, VERSION metadata checks pass, legacy script directory is not included as user-facing package content.

---

### Task 9: Convert Windows lifecycle acceptance into a pure EXE consumer

**Files:**
- Create: `tests/windows-service/run-installer-acceptance.ps1`
- Modify: `.github/workflows/windows-service.yml`
- Modify: `tests/core/SystemServiceProductTests.cs`
- Modify: `tests/core/InstallerArtifactContractTests.cs`

**Interfaces:**
- Consumes: existing versioned EXE + checksum/provenance.
- Produces: real installer/ARP/service/uninstall acceptance without direct internal scripts.

- [ ] **Step 1: Define unattended install contract**

Use the WiX bundle's supported quiet mode for CI, with logging enabled to a temp path. No invocation of `install.ps1` or `sc.exe create` from acceptance code.

- [ ] **Step 2: Verify artifact identity before execution**

Check filename VERSION, SHA-256, size and provenance fields before launching EXE.

- [ ] **Step 3: Install and assert machine-wide lifecycle**

Assert:

```text
service exists
StartMode == Auto
State == Running
installed executable under Program Files
/v1/health == 200
loopback-only listener
package-owned config hash preserved
```

- [ ] **Step 4: Assert ARP/Installed Apps registration**

Read standard uninstall registry views and require WebAssistant entry with `DisplayVersion == VERSION` and uninstall command owned by the WiX bundle.

- [ ] **Step 5: Exercise service stop/start/restart**

Retain current health/log/loopback assertions.

- [ ] **Step 6: Uninstall through registered standard uninstall path**

Do not call repository `uninstall.ps1`. Assert service, product registration and Program Files payload disappear while data/log preservation follows documented default.

- [ ] **Step 7: Update Windows workflow to producer/consumer jobs**

Producer uploads exact EXE/evidence; consumer downloads and runs harness. Consumer has no SDK/publish requirement except tools needed to inspect/run installer.

---

### Task 10: Introduce shared build-once installer workflow and wire PR final CI

**Files:**
- Create: `.github/workflows/build-installers.yml`
- Create: `.github/workflows/installer-acceptance.yml`
- Modify: `.github/workflows/ci.yml`
- Modify: `ci/change-plan.sh`
- Modify: `tests/core/ChangePlanClassifierTests.cs`
- Modify: `tests/core/CiWorkflowContractTests.cs`
- Modify: candidate conformance
- Modify: `webassist/VERSION`

**Interfaces:**
- Consumes: canonical package entrypoints/harnesses.
- Produces: one immutable producer result per OS reused by final acceptance.

- [ ] **Step 1: Write failing CI contract tests**

Assert `ci.yml` final path invokes producer once per OS and acceptance depends on uploaded artifacts. Assert lifecycle reusable workflow contains download/verify but no package/publish.

- [ ] **Step 2: Implement `build-installers.yml`**

Inputs: exact `source_ref`. Windows job calls `webassist/build/windows/package.bat`; Linux job calls `webassist/build/linux/package.sh`. Set `WEBASSISTANT_SOURCE_SHA` to exact source ref. Upload installer + checksum + provenance with artifact names that include platform and source SHA to avoid accidental cross-run reuse.

- [ ] **Step 3: Implement `installer-acceptance.yml`**

Download exact producer artifacts by job dependency in the same workflow invocation and run OS-specific consumer harnesses.

- [ ] **Step 4: Wire `ci.yml` final acceptance**

Keep current fast/core/scanner separation. For distribution-affecting PRs, final acceptance uses installer producer/consumer path. Current scanner-final suite remains independent and unchanged.

- [ ] **Step 5: Update change classifier**

Changes under Windows/Linux package/install/installer paths must require corresponding installer build/acceptance. Shared build/common/provenance or distribution workflows require both platforms fail-closed.

- [ ] **Step 6: Run classifier truth-table tests and workflow contract tests**

Expected: distribution-only deltas schedule correct platform acceptance; unknown/shared changes schedule both.

---

### Task 11: Update autonomous product documentation to canonical installers

**Files:**
- Modify: `webassist/README.md`
- Modify: `webassist/docs/windows-service.md`
- Modify: `webassist/docs/linux-service.md`
- Create: `webassist/docs/installation-guide.md`
- Modify: tests enforcing product documentation/current package paths

**Interfaces:**
- Consumes: stabilized installer UX.
- Produces: current repository-owned docs without scanner redesign.

- [ ] **Step 1: Write docs consistency tests**

Require versioned EXE/ZIP patterns, canonical package entrypoints, target self-contained statement, package-config ownership and standard uninstall paths. Reject old directory-package instructions as current canonical user flow.

- [ ] **Step 2: Rewrite Windows docs**

Document manual build via `build/windows/package.bat`, versioned EXE output, interactive UAC install, service/health verification, Installed Apps and standard uninstall.

- [ ] **Step 3: Rewrite ALT docs**

Document manual build via `build/linux/package.sh`, versioned ZIP output, unpack, `sudo ./install.sh`, apt-rpm runtime dependency behavior, systemd/health and uninstall.

- [ ] **Step 4: Add editable installation-guide source**

Structure numbered steps and figure placeholders by stable semantic captions only. Do not add mock screenshots. Real screenshots are added in Task 12.

- [ ] **Step 5: Run product autonomy/docs tests**

Ensure `webassist/**` contains no parent governance references and remains valid after copy-export.

---

### Task 12: Capture real screenshots and build `WebAssistant-Installation-Guide.pdf`

**Files:**
- Add: `webassist/docs/installation-guide-assets/*` real screenshots
- Modify: `webassist/docs/installation-guide.md`
- Create generated build output only in CI/artifacts: `WebAssistant-Installation-Guide.pdf`
- Add repository-owned PDF build script/source as required by the PDF skill used at implementation time.

**Interfaces:**
- Consumes: stable real Windows EXE and ALT ZIP UX.
- Produces: one reproducible PDF release asset.

- [ ] **Step 1: Read `/home/oai/skills/pdfs/SKILL.md` before implementing PDF generation**

Follow its toolchain/layout/verification requirements exactly.

- [ ] **Step 2: Capture real Windows screenshots**

Minimum: downloaded EXE, UAC, installer UI/result, Windows Service, health, Installed Apps, uninstall.

- [ ] **Step 3: Capture real ALT Linux 10.1 screenshots**

Minimum: ZIP, GUI/file-manager unpack, terminal, `sudo ./install.sh`, password prompt context, dependency installation when applicable, successful output, `systemctl status`, health, uninstall.

- [ ] **Step 4: Build PDF reproducibly from repository-owned source**

Output filename is exactly `WebAssistant-Installation-Guide.pdf`; it is not versioned unless requirements change explicitly.

- [ ] **Step 5: Validate PDF visually and structurally**

Check page rendering, figure numbering, readable screenshots, no mock images, Russian instructions and successful-install verification on both OS sections.

---

### Task 13: Implement accepted-main same-byte GitHub Release

**Files:**
- Create: `.github/workflows/release.yml`
- Modify: `tests/core/CiWorkflowContractTests.cs`
- Modify: candidate conformance
- Modify: `webassist/VERSION` only if this is a separate accepted product transition

**Interfaces:**
- Consumes: producer artifacts + exact acceptance + PDF.
- Produces: immutable release for one VERSION.

- [ ] **Step 1: Write failing release workflow contract tests**

Require release flow to obtain installer bytes from the build/acceptance graph without invoking a second package build after acceptance. Require versioned filenames and exact release tag derived from `webassist/VERSION`.

- [ ] **Step 2: Define release trigger**

Run on accepted `main` transition where VERSION differs from previous accepted main; allow manual rerun for idempotency verification. Do not publish PR artifacts as official releases.

- [ ] **Step 3: Reuse exact accepted bytes**

Within one release workflow graph: build each installer once, upload immutable workflow artifacts, consume for lifecycle acceptance, then the publication job downloads those same artifacts by dependency. No product rebuild in publication job.

- [ ] **Step 4: Verify identity immediately before publication**

Recompute SHA-256 and compare provenance. Verify filename VERSION and PDF name.

- [ ] **Step 5: Implement idempotent release behavior**

```text
release absent -> create after GREEN
release exists + assets/hashes identical -> success/no-op
release exists + any installer hash differs -> hard fail
```

Never overwrite an existing VERSION with different bytes.

- [ ] **Step 6: Publish assets**

Required user-facing assets:

```text
WebAssistant-win-x64-<VERSION>.exe
WebAssistant-linux-x64-<VERSION>.zip
WebAssistant-Installation-Guide.pdf
```

Also publish correlated `.sha256` and `.provenance.json` evidence for both installers.

- [ ] **Step 7: Run workflow contract/core tests**

Expected: no post-acceptance rebuild path exists.

---

### Task 14: Finalize candidate evidence and promote only after installer/release acceptance

**Files:**
- Modify: candidate contract/conformance status fields
- Modify: `repo-policy.json`
- Modify: `README.md`
- Modify: `tests/core/BaselineContractTests.cs` only if current-pair validation requires monotonic reference updates
- Modify: `webassist/VERSION`

**Interfaces:**
- Consumes: GREEN evidence from Tasks 5–13.
- Produces: accepted/current distribution contract pair.

- [ ] **Step 1: Verify every new candidate vector has real evidence**

No design-only evidence may remain as sole proof for executable installer/release behavior.

- [ ] **Step 2: Prepare a separate promotion GovernanceGrant**

Authorize exactly:

```text
candidate contract file
candidate conformance file
repo-policy.json
README.md if policy requires new monotonic references
```

No policy relaxation.

- [ ] **Step 3: Change candidate pair to accepted atomically with current-pointer promotion**

Set pair `status=accepted`, `accepted=true`; update `repo-policy.json.contract_conformance.current` to exact pair; preserve old v0.2 files unchanged and retain historical README references.

- [ ] **Step 4: Run full core + repo-guard + installer acceptance on exact head**

Require all applicable checks GREEN and release workflow dry/idempotency verification where safe.

- [ ] **Step 5: Merge fixed-head and reread main**

Record final main SHA, VERSION, accepted contract/conformance schemas, installer filenames/hashes and release identity in #165/#160/#10.

- [ ] **Step 6: Close completed distribution issues selectively**

Close #155/#156/#7/#5/#157/#158 only when their own acceptance boxes are actually satisfied. Keep #159 deferred and #161–#164 deferred.

---

## Self-Review

**Spec coverage:** Covered canonical producers, versioned filenames, config ownership, self-contained target, Windows EXE lifecycle, ALT ZIP/systemd/apt-rpm boundary, SHA/provenance, exact-artifact CI, PDF, release same bytes, deferred GitLab/scanner boundaries and separate promotion.

**Repository-hygiene coverage:** Included the previously identified policy-literal sanitization as a separate bounded governance transaction before product implementation.

**Placeholder scan:** No `TBD`/`TODO` implementation placeholders. Candidate filenames are intentionally selected transactionally in Task 2 because governance requires exact paths to be chosen immediately before authorization; subsequent tasks consume those exact paths from #165.

**Type/interface consistency:** Provenance field names and `configMode` enum are identical across Windows/Linux producer and consumer tasks. Final artifact patterns are identical across scripts, CI, docs and release tasks.
