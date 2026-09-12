# Product Metadata Branding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement one validated cross-platform build-time product metadata contract that parameterizes Windows/Linux user-visible branding and installer basenames while preserving `webassist/VERSION` and internal WebAssistant lifecycle identifiers.

**Architecture:** Both canonical producers call one repository-owned .NET 10 `ProductMetadataResolver`. It merges public defaults with the optional `src/WebAssistant/product-metadata.json`, validates a closed schema, emits a normalized Base64 environment record plus hashes, and fails closed before packaging. Windows/Linux producers consume the same output, while project/WiX/provenance surfaces use effective metadata and technical service/executable IDs stay fixed.

**Tech Stack:** .NET 10/C#, PowerShell, Bash, WiX Toolset 7, xUnit, JSON.

**Spec:** `docs/superpowers/specs/2026-09-12-product-metadata-branding-design.md`

## Global Constraints

- Canonical override path is exactly `webassist/src/WebAssistant/product-metadata.json`.
- Public GitHub defaults remain `WebAssistant`; override absence must preserve current public behavior.
- `webassist/VERSION` is the only version authority and cannot be overridden by metadata.
- Root GitHub `.gitignore` ignores the canonical override path; exported `webassist/.gitignore` must not ignore it.
- Accepted `contracts/*v0.2.json` and `repo-policy.json` must not change.
- Candidate v0.3 is updated before observable implementation semantics are accepted.
- Internal `WebAssistant.exe`, service name `WebAssistant`, `webassist.service`, WiX technical IDs and lifecycle paths are not renamed by branding.
- VERSION advances exactly once only after functional GREEN.

---

### Task 1: Candidate authority and RED contract tests

**Files:**
- Modify: `contracts/webassistant-contract-v0.3.json`
- Modify: `contracts/webassistant-conformance-v0.3.json`
- Modify: `tests/core/DistributionContractCandidateTests.cs`
- Modify: `tests/core/InstallerArtifactContractTests.cs`
- Create: `tests/core/ProductMetadataContractTests.cs`

**Interfaces:**
- Produces: candidate requirements/vectors defining parameterized effective product identity and test expectations that current hard-coded producers do not satisfy.

- [ ] Add a trusted GovernanceGrant for the two candidate files to issue #190 body before touching candidate authority.
- [ ] Change `WA-DIST-002` from hard-coded `WebAssistant-*` names to `<effective installer basename>-<rid>-<VERSION>` with public default `WebAssistant` and invariant VERSION equality.
- [ ] Extend Windows/Linux/artifact requirements with effective metadata/provenance semantics while preserving technical IDs.
- [ ] Add conformance vector(s) for default metadata, override metadata, invalid override and cross-platform identity consistency.
- [ ] Replace static filename assertions in `InstallerArtifactContractTests` with resolver/effective-basename expectations.
- [ ] Add `ProductMetadataContractTests` asserting canonical paths, GitHub-vs-export ignore boundary, closed schema requirements, same resolver usage and provenance fields.
- [ ] Run `dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release` in CI and record expected RED failures caused only by missing #190 implementation.

### Task 2: Shared metadata resolver

**Files:**
- Create: `webassist/build/common/product-metadata.defaults.json`
- Create: `webassist/build/common/ProductMetadataResolver/ProductMetadataResolver.csproj`
- Create: `webassist/build/common/ProductMetadataResolver/Program.cs`
- Modify: `.gitignore`
- Test: `tests/core/ProductMetadataContractTests.cs`

**Interfaces:**
- Consumes: defaults path and optional override path.
- Produces: line-oriented environment file with `metadataMode`, Base64 effective fields, `metadataInputSha256`, `effectiveMetadataSha256`.

- [ ] Define complete public defaults using schema `webassistant-product-metadata/v1`.
- [ ] Implement strict JSON object parsing; reject duplicate/unknown keys and every version-like key.
- [ ] Implement partial override merge onto defaults.
- [ ] Validate non-empty strings/no control characters/max 256 Unicode scalar values.
- [ ] Validate `installerBaseName` with `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$` and reject `.`/`..`.
- [ ] Canonicalize effective metadata in fixed field order and compute SHA-256.
- [ ] Emit Base64 values so shell/PowerShell never evaluate metadata as code.
- [ ] Add exact root `.gitignore` rule and assert exported `webassist/.gitignore` remains free of it.
- [ ] Run focused resolver/core tests GREEN.

### Task 3: Windows producer and binary/WiX metadata

**Files:**
- Modify: `webassist/build/windows/package.ps1`
- Modify: `webassist/src/WebAssistant/WebAssistant.csproj`
- Modify: `webassist/build/windows/installer/Package.wxs`
- Modify: `webassist/build/windows/installer/Bundle.wxs`
- Test: `tests/core/ProductMetadataContractTests.cs`
- Test: `tests/core/InstallerArtifactContractTests.cs`

**Interfaces:**
- Consumes: normalized resolver environment output.
- Produces: `<installerBaseName>-win-x64-<VERSION>.exe` with effective .NET/WiX display metadata.

- [ ] Resolve metadata before final artifact name is constructed.
- [ ] Decode Base64 into data-only PowerShell variables and export MSBuild properties.
- [ ] Map .NET SDK assembly metadata: Product, AssemblyTitle/Description, Company, Copyright; leave version properties on `webassist/VERSION`.
- [ ] Parameterize WiX user-visible `Name`/`Manufacturer` through build properties.
- [ ] Keep `WebAssistant.exe`, Windows service name, WiX IDs and install/state compatibility identifiers unchanged.
- [ ] Build final filename from effective installer basename and exact VERSION.
- [ ] Run focused core tests and Windows package acceptance.

### Task 4: Linux producer human-visible metadata

**Files:**
- Modify: `webassist/build/linux/package.sh`
- Modify: `webassist/install/linux/webassist.service` only if template tokenization is required; technical unit name must remain unchanged.
- Test: `tests/core/ProductMetadataContractTests.cs`
- Test: `tests/core/InstallerArtifactContractTests.cs`

**Interfaces:**
- Consumes: the same resolver/default/override contract as Windows.
- Produces: `<installerBaseName>-linux-x64-<VERSION>.zip` and staged human-visible service description using effective metadata.

- [ ] Resolve metadata after .NET SDK selection but before artifact naming/publish.
- [ ] Decode Base64 safely and export the same MSBuild metadata properties used by Windows.
- [ ] Construct ZIP basename/archive root from effective installer basename + VERSION.
- [ ] Rewrite only staged service `Description=` from effective `fileDescription`; keep unit name, user/group, paths and executable unchanged.
- [ ] Run focused core tests and Linux SANE/systemd artifact acceptance.

### Task 5: Provenance and documentation

**Files:**
- Modify: `webassist/build/common/write-provenance.ps1`
- Modify: `webassist/build/common/write-provenance.sh`
- Modify: `webassist/docs/build.md` if present, otherwise the canonical product build documentation identified from `webassist/README.md`.
- Modify: `webassist/README.md` only where public/default vs downstream override behavior must be documented.
- Test: `tests/core/InstallerArtifactContractTests.cs`
- Test: `tests/core/ProductMetadataContractTests.cs`

**Interfaces:**
- Produces provenance fields `metadataMode`, `applicationName`, `installerBaseName`, `metadataInputSha256`, `effectiveMetadataSha256` on both platforms.

- [ ] Extend both writers with identical metadata arguments/closed `metadataMode` enum.
- [ ] Pass resolved metadata from both producer scripts before SHA/provenance freeze.
- [ ] Document default GitHub behavior and downstream GitLab tracking rule without exposing private values.
- [ ] Document VERSION non-overridability and technical-ID compatibility boundary.
- [ ] Run all core tests GREEN.

### Task 6: Full acceptance and single VERSION transition

**Files:**
- Modify only after functional GREEN: `webassist/VERSION`

**Interfaces:**
- Produces final #190 exact head suitable for repo-guard/full CI/merge.

- [ ] Verify default-mode producers still produce public `WebAssistant` filenames and lifecycle behavior.
- [ ] Verify override resolver semantics with a temporary non-committed metadata file; no private value is committed.
- [ ] Run full CI scanner/core/Windows WiX/ALT systemd acceptance on exact head.
- [ ] Advance VERSION exactly once from `0.3.26` to `0.3.27`.
- [ ] Re-run exact-head required CI and blocking repo-guard.
- [ ] Review diff for must-not-touch boundaries (`v0.2`, `repo-policy.json`, scanner semantics, #192 localization/icon work).
- [ ] Merge only with exact expected head SHA and record post-merge evidence in #190/#160/#154/#10.
