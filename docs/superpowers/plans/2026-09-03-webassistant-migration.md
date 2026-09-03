# WebAssistant Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Migrate the exact ScannerAgent PR #120 product state into public `netkeep80/WebAssistant`, fully rename/sanitize it, add JSON-configured optional CORS and a safe rooted-filesystem boundary, then migrate meaningful Issues.

**Architecture:** Keep `webassist/` as the autonomous export root. Preserve scanner behavior as one module of the broader WebAssistant service while moving runtime configuration into `WebAssistant` JSON options and introducing a dedicated rooted-path security component for future file APIs. Treat contracts/policy/CI as migrated governance, not as unreviewed text copies.

**Tech Stack:** .NET 10, ASP.NET Core, NAPS2 SDK, WIA/TWAIN/SANE, xUnit, Windows Service, systemd, GitHub Actions, GitLab CI.

**Spec:** `docs/superpowers/specs/2026-09-03-webassistant-migration-design.md`

## Global Constraints

- `netkeep80/ScannerAgent` is read-only: zero mutations.
- Source snapshot is exact PR #120 head `527b9186e23c75a549cfe1a8c5c44902d584d8de` unless re-read proves it moved before source materialization.
- Rename `ScannerAgent -> WebAssistant` and `tmk5scan -> webassist` across project-owned identities.
- Remove all `Triumf`, `depfin.nnov.ru`, and corporate-specific `tmk5*` content from the destination.
- Keep Windows WIA-first/TWAIN-empty-only policy and Linux direct SDK/SANE behavior.
- Preserve current scanner API and PDF/concurrency/source semantics.
- CORS defaults disabled and is enabled only by explicit JSON configuration.
- Future filesystem operations must be confined to `WebAssistant:FileSystem:RootDirectory` via one path-security component.
- No speculative HTTPS implementation in this migration; keep loopback listener architecture separable.
- Use TDD for behavior changes: observe RED before production implementation.

---

### Task 1: Materialize and mechanically rename the PR #120 baseline

**Files:**
- Create/migrate: `.github/**`, `.gitignore`, `README.md`, `contracts/**`, `repo-policy.json`, `tests/**`, `webassist/**`
- Rename: `webassist/ScannerAgent.sln -> webassist/WebAssistant.sln`
- Rename: `webassist/src/ScannerAgent/** -> webassist/src/WebAssistant/**`
- Rename: `webassist/src/WebAssistant/ScannerAgent.csproj -> webassist/src/WebAssistant/WebAssistant.csproj`
- Rename: `tests/core/ScannerAgent.CoreTests.csproj -> tests/core/WebAssistant.CoreTests.csproj`

**Interfaces:**
- Consumes: exact source Git tree at PR #120 head.
- Produces: a destination-only branch whose text/project/service identities are WebAssistant/webassist and whose scanner behavior is otherwise unchanged.

- [ ] **Step 1: Re-read source and destination refs**

Verify the old PR head and new `main` immediately before materialization. Abort the copy if the old PR head changed unexpectedly and inspect the delta read-only.

- [ ] **Step 2: Materialize the source tree locally/read-only and build a rename manifest**

The manifest must include every tracked path and every text substitution. Explicitly scan for:

```text
ScannerAgent
tmk5scan
Triumf
depfin.nnov.ru
tmk5
```

- [ ] **Step 3: Rebuild the project-owned fixed SDK package identity**

Build the same upstream fix from commit `450cba65aaffe6387041050a573051a64cd80fe9` with package identity:

```text
WebAssistant.NAPS2.Sdk
1.3.0-webassistant.1.450cba65
```

Update product/test restore references and repository-owned feed proof so binary/package metadata no longer carries the old project-owned identity.

- [ ] **Step 4: Create the renamed baseline in the WebAssistant branch**

Do not commit to ScannerAgent. Preserve executable semantics while renaming namespaces, project files, service strings, paths, tests, workflow path filters, and docs.

- [ ] **Step 5: Run static sanitization before behavior work**

Required zero-match scans:

```text
Triumf
depfin.nnov.ru
tmk5
```

`ScannerAgent` may appear only in an explicit migration provenance document outside product/runtime surfaces until the final full-rename task removes it where required.

- [ ] **Step 6: Commit**

```bash
git commit -m "chore: migrate ScannerAgent baseline as WebAssistant"
```

### Task 2: Correct the migrated Linux HTTP E2E busy test

**Files:**
- Modify: `tests/core/PlatformEndToEndTests.cs`

**Interfaces:**
- Consumes: existing `/v1/scanners` and `/v1/scan?scannerId=...` API.
- Produces: a concurrency E2E test valid for multiple SANE devices.

- [ ] **Step 1: Write/retain the failing Linux E2E assertion**

The test must first enumerate scanners:

```csharp
var scanners = await client.GetFromJsonAsync<List<ScannerDeviceDto>>("/v1/scanners");
Assert.NotNull(scanners);
Assert.NotEmpty(scanners);
var scannerId = scanners[0].Id;
```

Then both competing requests and the repeat request use:

```csharp
var path = $"/v1/scan?scannerId={Uri.EscapeDataString(scannerId)}";
```

- [ ] **Step 2: Run the Linux virtual HTTP E2E and confirm the old no-ID form fails with 409/409**

Expected RED reason: two SDK SANE devices and no `scannerId`.

- [ ] **Step 3: Apply only the explicit-ID test correction**

Do not change product scanner-selection semantics.

- [ ] **Step 4: Re-run virtual Linux E2E**

Expected: one concurrent request `200 application/pdf`, the other `409 busy`, then repeat `200 application/pdf`.

- [ ] **Step 5: Commit**

```bash
git commit -m "test: use explicit scanner id in Linux busy E2E"
```

### Task 3: Introduce WebAssistant JSON runtime configuration and optional CORS

**Files:**
- Create: `webassist/src/WebAssistant/appsettings.json`
- Rename/modify: `webassist/src/WebAssistant/Runtime/WebAssistantRuntimeOptions.cs`
- Modify: `webassist/src/WebAssistant/Program.cs`
- Rename/modify tests: `tests/core/CorsConfigurationTests.cs`, runtime-options tests as applicable

**Interfaces:**
- Produces: `WebAssistantRuntimeOptions.Load(IConfiguration)` with `CorsEnabled`, `AllowedOrigins`, `FileSystemRootDirectory`, `LogDirectory`, `Port`.

- [ ] **Step 1: Write RED tests for default CORS disabled**

A request with any `Origin` and no explicit config must not receive `Access-Control-Allow-Origin`.

- [ ] **Step 2: Run the CORS tests and observe RED**

Expected failure: migrated hard-coded/default CORS behavior still allows an origin or CORS middleware is always active.

- [ ] **Step 3: Write RED tests for JSON/configured exact origin**

Configure:

```text
WebAssistant:Cors:Enabled=true
WebAssistant:Cors:AllowedOrigins:0=https://example.test
```

Assert that exact origin is allowed and an unknown origin is not.

- [ ] **Step 4: Write RED validation tests**

Assert startup/options load rejects:

```text
*
https://example.test/path
https://user@example.test
```

- [ ] **Step 5: Implement minimal runtime options and conditional middleware**

Load JSON from `AppContext.BaseDirectory`, use `WebAssistant` keys, call `UseCors` only when enabled, and remove all hard-coded corporate origins.

- [ ] **Step 6: Run focused and full core tests**

Expected: CORS tests GREEN; no scanner regression.

- [ ] **Step 7: Commit**

```bash
git commit -m "feat: configure optional CORS from JSON"
```

### Task 4: Add the rooted filesystem security boundary

**Files:**
- Create: `webassist/src/WebAssistant/FileSystem/RootedPathResolver.cs`
- Create: `tests/core/RootedPathResolverTests.cs`
- Modify: `webassist/src/WebAssistant/Runtime/WebAssistantRuntimeOptions.cs`
- Modify installers: `webassist/install/windows/install.ps1`, `webassist/install/linux/install.sh`

**Interfaces:**
- Produces: `RootedPathResolver(string rootDirectory)` and `string Resolve(string relativePath)`.
- Contract: returned paths are full paths under the configured root; rooted/traversal/link escapes throw before I/O.

- [ ] **Step 1: RED — valid nested relative path**

```csharp
var resolver = new RootedPathResolver(root);
var path = resolver.Resolve("documents/report.pdf");
Assert.StartsWith(Path.GetFullPath(root), path, comparison);
```

- [ ] **Step 2: RED — reject absolute/rooted paths**

Cover Windows-style and Unix-style rooted input according to the executing platform.

- [ ] **Step 3: RED — reject traversal segments**

Cover:

```text
../outside
folder/../../outside
./file
folder/../file
```

- [ ] **Step 4: RED — reject existing symlink/reparse escape**

Create an outside temp directory and a link inside the root that points outside; resolving through that link must fail. Skip only when the test host cannot create the platform link and report the skip explicitly.

- [ ] **Step 5: Implement minimal resolver**

Canonicalize the root, require relative input, reject `.`/`..`, combine and `GetFullPath`, enforce root prefix with separator-aware OS comparison, and walk existing components rejecting `LinkTarget`/reparse points.

- [ ] **Step 6: Add runtime default root selection**

```text
Windows -> %ProgramData%\WebAssistant\data
Linux   -> /var/lib/webassistant
Other   -> <base>/data
```

- [ ] **Step 7: Update installers**

Create the default data root and grant the WebAssistant service identity write access consistent with existing log-directory ownership patterns.

- [ ] **Step 8: Run focused/full tests**

Expected: all root-boundary tests GREEN and no installer contract regression.

- [ ] **Step 9: Commit**

```bash
git commit -m "feat: confine filesystem paths to configured root"
```

### Task 5: Migrate service/package identity, docs, contracts, policy, and CI consistently

**Files:**
- Modify/rename: `webassist/install/linux/webassistant.service`, Linux/Windows install/uninstall/build scripts
- Modify: `webassist/README.md`, `webassist/docs/**`, root `README.md`
- Modify/rename: `contracts/**`, `repo-policy.json`
- Modify: `.github/workflows/**`
- Modify: `webassist/.gitlab-ci.yml`
- Modify tests that assert product/service/path identities

**Interfaces:**
- Produces: consistent WebAssistant identity and governance with no old product/root references.

- [ ] **Step 1: RED static identity tests**

Add/adjust tests that fail if tracked destination text contains forbidden corporate tokens or old product-owned service/project paths.

- [ ] **Step 2: RED service/package tests**

Assert target identities:

```text
WebAssistant
webassistant.service
/opt/webassistant
/var/log/webassistant
/var/lib/webassistant
%ProgramData%\WebAssistant\logs
%ProgramData%\WebAssistant\data
```

- [ ] **Step 3: Rename contracts/conformance and requirement IDs consistently**

Migrate historical accepted scanner pairs to WebAssistant naming/`WA-*` IDs, then add a current additive pair covering optional CORS, rooted filesystem boundary, and `webassist` autonomy. Update `repo-policy.json` current pointers and document/cochange paths.

- [ ] **Step 4: Update repo-guard/product-isolation/product-autonomy policy surfaces**

All path globs use `webassist/**`; product isolation prohibits development-wrapper references inside the export root without prohibiting its own GitLab build documentation.

- [ ] **Step 5: Update service/build/install docs and scripts**

Remove obsolete Linux CLI/RPM statements when production direct SDK no longer requires them. Preserve .NET 10 bootstrap behavior and cwd-independent entrypoints.

- [ ] **Step 6: Run full static zero-match audit**

Expected zero tracked matches for:

```text
Triumf
depfin.nnov.ru
tmk5
```

Old `ScannerAgent` is allowed only in explicit migration provenance outside product/contracts/runtime; eliminate it everywhere else.

- [ ] **Step 7: Run all destination blocking workflows/tests**

Target gates:

```text
repo-guard
core
product-isolation
product-autonomy
ALT Linux systemd
Windows Service
virtual scanner
```

- [ ] **Step 8: Commit**

```bash
git commit -m "refactor: complete WebAssistant product and governance rename"
```

### Task 6: Exact-head verification and baseline PR merge

**Files:** none unless a failing test exposes a scoped defect.

**Interfaces:**
- Produces: merged, green public WebAssistant baseline.

- [ ] **Step 1: Read exact PR head and all workflow runs**

Do not use stale status from earlier commits.

- [ ] **Step 2: Fix any failure by returning to the relevant TDD task**

No check disabling, no policy relaxation merely to force green.

- [ ] **Step 3: Fresh compare with `main`**

Require:

```text
behind_by=0
```

- [ ] **Step 4: Merge with exact expected head SHA**

Use the fresh PR head as `expected_head_sha`.

- [ ] **Step 5: Post-merge reread `main`**

Confirm the merge SHA and re-check destination repository tree identity.

### Task 7: Migrate sanitized Issues

**Files:** GitHub Issues only; no source-repository mutations.

**Interfaces:**
- Consumes: old ScannerAgent issue title/body/comments/state read-only.
- Produces: sanitized WebAssistant Issues with destination-assigned numbers and provenance.

- [ ] **Step 1: Enumerate all meaningful old Issues**

Exclude known accidental garbage:

```text
#74 #75 #76 #104 #105 #106
```

- [ ] **Step 2: Build an old->new issue-number map in memory before rewriting cross-references**

Create destination Issues in source-number order so later passes can replace `#N` references where a migrated destination referent exists.

- [ ] **Step 3: Sanitize each title/body/comment**

Required transformations:

```text
ScannerAgent -> WebAssistant
tmk5scan -> webassist
```

Remove rather than expose corporate-only details involving:

```text
Triumf
depfin.nnov.ru
tmk5*
```

Retain generic GitLab/build facts where useful.

- [ ] **Step 4: Preserve state**

Create content, add sanitized comments, then close destination Issues whose source state is closed.

- [ ] **Step 5: Add provenance**

Each body ends with:

```text
Migrated from ScannerAgent #N.
```

Do not edit or close the old Issue.

- [ ] **Step 6: Audit destination Issues**

Search titles/bodies/comments for forbidden corporate tokens and correct any sanitization miss.

- [ ] **Step 7: Refresh the WebAssistant roadmap Issue**

Create/update the destination roadmap so it points to the new issue numbers and current WebAssistant architecture rather than obsolete ScannerAgent/CLI assumptions.
