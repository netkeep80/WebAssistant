# Current-State Documentation Audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make canonical WebAssistant documentation describe the exact current pre-pilot runtime/product surface after #196 without changing runtime or semantic authority.

**Architecture:** Treat current runtime code, candidate v0.3 contract/conformance, scanner schema, packaging scripts and executable tests as evidence. Add focused regression tests for previously stale claims, then correct only the canonical documentation that contradicts that evidence. Keep accepted v0.2 immutable and candidate v0.3 non-accepted.

**Tech Stack:** Markdown, C# xUnit repository contract tests, repo-guard, GitHub Actions.

**Spec:** `contracts/webassistant-contract-v0.3.json`, `contracts/webassistant-conformance-v0.3.json`, GitHub Issue #99.

## Global Constraints

- Baseline main is `fb2120f291b57ddb429a3221f47abfbb94b205b4` with `webassist/VERSION = 0.3.29`.
- Accepted authority remains `contracts/webassistant-contract-v0.2.json` + `contracts/webassistant-conformance-v0.2.json` and must not change.
- Candidate v0.3 remains `status = candidate`, `accepted = false`.
- No runtime, API, scanner, filesystem or packaging behavior changes belong to this transaction.
- `webassist/**` remains autonomous and must not gain parent-governance references forbidden by `repo-policy.json`.
- Product version must increase monotonically, so this transaction targets `0.3.30`.
- Public defaults use `installerBaseName = WebAssistant`; documentation must also acknowledge the supported build-time override instead of presenting the default as the only canonical basename.

---

### Task 1: Lock current documentation semantics with regression tests

**Files:**
- Create: `tests/core/CurrentStateDocumentationTests.cs`

**Interfaces:**
- Consumes: canonical Markdown files and current candidate/runtime vocabulary.
- Produces: executable assertions that prevent reintroduction of superseded filesystem/scanner/artifact-name documentation.

- [ ] **Step 1: Write failing tests**

Add tests that require:

```text
root README: current filesystem runtime/candidate surface is acknowledged
webassist README: WIA + TWAIN independent union semantics
webassist README: explicit source has no fallback, auto feeder-empty retry is the documented exception
webassist README: filesystem routes and /filesystem.html are visible current capabilities
api.md: /filesystem.html is documented as ordinary public-API client
Windows/Linux service docs: effective <installerBaseName> artifact pattern plus public WebAssistant default
```

Also assert absence of the exact superseded WIA-first/TWAIN-fallback and filesystem-endpoints-absent claims.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter FullyQualifiedName~CurrentStateDocumentationTests
```

Expected: FAIL on the current stale Markdown claims.

- [ ] **Step 3: Commit the RED evidence**

Commit only the new test file.

---

### Task 2: Correct canonical current-state documentation

**Files:**
- Modify: `README.md`
- Modify: `webassist/README.md`
- Modify: `webassist/docs/api.md`
- Modify: `webassist/docs/windows-service.md`
- Modify: `webassist/docs/linux-service.md`
- Modify: `webassist/VERSION`

**Interfaces:**
- Consumes: evidence asserted by Task 1, `Program.cs`, `FileSystemEndpointHandlers.cs`, scanner candidate requirements, runtime options and product metadata contract.
- Produces: one consistent current-state documentation surface for pre-pilot `0.3.30`.

- [ ] **Step 1: Fix root README authority wording**

Keep accepted v0.2 explicitly current, but distinguish it from the implemented candidate/runtime surface. Replace the obsolete claim that filesystem routes are absent with a concise statement that current runtime/candidate v0.3 exposes rooted filesystem exchange while accepted v0.2 has not been promoted.

- [ ] **Step 2: Fix product README scanner semantics**

State that Windows independently enumerates WIA and TWAIN and combines successful endpoints. Document the exact auto-source exception:

```text
explicit flatbed/feeder -> never fallback
auto -> if feeder was selected and acquisition fails specifically as feeder-empty, retry flatbed once when available
```

Do not reintroduce backend-native details into the public request schema.

- [ ] **Step 3: Add current filesystem capability summary to product README**

Document RootDirectory authority, root-relative/stateless navigation, seven `/v1/filesystem/*` routes at summary level, atomic no-replace upload/move, link/hard-link fail-closed behavior, active-extension policy, attachment-only download, and `/filesystem.html` as an ordinary public-API client. Link to `docs/api.md` for exact schemas/errors.

- [ ] **Step 4: Complete API documentation for the browser filesystem page**

Add `/filesystem.html` to `webassist/docs/api.md` as the repository-owned visual client of the same public API with Root-relative breadcrumb/navigation, no absolute host path and no inline preview/private privilege.

- [ ] **Step 5: Correct installer basename wording in service docs**

Use:

```text
<installerBaseName>-win-x64-<VERSION>.exe
<installerBaseName>-linux-x64-<VERSION>.zip
```

and explicitly say the public default basename is `WebAssistant`; preserve technical service/unit identities.

- [ ] **Step 6: Clarify default filesystem data roots in service docs**

Identify `%ProgramData%\WebAssistant\data` and `/var/lib/webassistant` as the default `WebAssistant:FileSystem:RootDirectory` while preserving configurable package-time runtime configuration semantics.

- [ ] **Step 7: Bump product version**

Set `webassist/VERSION` to:

```text
0.3.30
```

- [ ] **Step 8: Run focused tests and core suite**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter FullyQualifiedName~CurrentStateDocumentationTests
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
```

Expected: PASS.

- [ ] **Step 9: Commit the documentation correction**

Commit docs + VERSION after GREEN evidence.

---

### Task 3: Verify governance and exact-head acceptance

**Files:**
- No new product files expected.
- Update Issue #99 / PR evidence only.

**Interfaces:**
- Consumes: exact branch head after Task 2.
- Produces: merge-ready #99 evidence without semantic promotion.

- [ ] **Step 1: Open PR linked to #99**

Use a docs-focused ChangeIntent. Explicitly protect accepted v0.2 and candidate v0.3 contract/conformance from modification.

- [ ] **Step 2: Verify exact-head repo-guard and CI**

Required evidence:

```text
repo-guard = SUCCESS
applicable core/docs tests = SUCCESS
ci-fast/ci-required according to classifier = SUCCESS
```

- [ ] **Step 3: Re-read changed files**

Confirm no runtime source, governance policy or contract/conformance files changed and `VERSION = 0.3.30`.

- [ ] **Step 4: Record audit result in #99**

List corrected drifts and any deliberately deferred non-doc findings. Close #99 only after merge and post-merge reread.
