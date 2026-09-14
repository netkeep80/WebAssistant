# #196 Rooted Filesystem Exchange Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the #196 rooted browser-facing filesystem exchange API, atomic file publication, native containment on Windows/Linux, visual `/filesystem.html` navigator, and executable browser/platform acceptance evidence.

**Architecture:** HTTP handlers depend only on an `IRootedFileSystem` operation boundary and root-relative paths. Windows and Linux implementations keep trusted root/parent handles or descriptors alive while resolving and mutating objects; no security-sensitive operation validates a full path string and later reopens that path. Upload uses an internal same-filesystem staging area and atomic no-replace publication. The browser page is an ordinary public API client and is verified with Playwright.

**Tech Stack:** .NET 10 / ASP.NET Core minimal API, C# P/Invoke for Windows native file APIs and Linux `openat2`/`*at` syscalls, xUnit, `Microsoft.AspNetCore.Mvc.Testing`, Microsoft.Playwright 1.55.0, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-14-filesystem-exchange-design.md`

## Global Constraints

- GitHub is source of truth; work only through issue #196 / draft PR #210.
- Accepted `contracts/webassistant-contract-v0.2.json` and `contracts/webassistant-conformance-v0.2.json` are immutable.
- Candidate `v0.3` may change but remains `accepted=false` until a separate explicit promotion.
- `FILESYSTEM AUTHORITY = configured RootDirectory only`.
- Absolute paths are never accepted or returned by the public API.
- No implicit overwrite, append or recursive delete.
- File create/upload and move are no-replace operations; destination collision is `409 destination_exists`.
- Directory deletion is empty-only.
- User files are opaque bytes and never become static web resources or inline previews.
- Upload/download stream with bounded buffering; no arbitrary whole-file RAM buffering.
- Upload final-name visibility is atomic: final name absent before commit and complete immediately after commit.
- Link/reparse traversal is forbidden; hard-link files with link count > 1 are fail-closed.
- Scanning and filesystem capabilities remain independent.
- Production logs never contain file contents and do not persist filenames/relative paths by default.
- The filesystem test page uses only public API endpoints and no private bypass.
- Browser E2E must exercise navigation and every MVP operation plus external host mutation.
- #211 owns the later broad adversarial/fuzz/stress campaign; #196 still proves its core containment/race invariants before merge.

---

### Task 1: Freeze candidate API/conformance authority before runtime implementation

**Files:**
- Modify: `contracts/webassistant-contract-v0.3.json`
- Modify: `contracts/webassistant-conformance-v0.3.json`
- Create: `tests/core/FileSystemCandidateContractTests.cs`

**Interfaces:**
- Produces the exact route/error/semantic vocabulary runtime tests will implement.
- Keeps v0.2 byte-for-byte unchanged.

- [ ] **Step 1: Write failing candidate-contract tests**

Create tests that parse candidate v0.3 JSON and require all of these literals/semantics to be represented: `/v1/filesystem/list`, `/v1/filesystem/file`, `/v1/filesystem/directory`, `/v1/filesystem/move`, `destination_exists`, `directory_not_empty`, `unsafe_link`, `hardlink_rejected`, `blocked_file_type`, `locked`, `filesystem_unavailable`, atomic no-replace publication, streaming/opaque download, pagination 200/1000, test page `/filesystem.html`, and browser acceptance evidence. Also hash/read v0.2 only to assert it still reports accepted/current semantics and is not promoted/rewritten by this transaction.

- [ ] **Step 2: Run the focused tests and record RED**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter FullyQualifiedName~FileSystemCandidateContractTests
```

Expected: FAIL because v0.3 does not yet contain the #196 filesystem transaction.

- [ ] **Step 3: Extend candidate v0.3 only**

Add the filesystem API semantic surface and falsification vectors. Preserve existing candidate fields/status and `accepted=false`. Do not edit v0.2.

- [ ] **Step 4: Run focused candidate tests GREEN**

Same command; expected PASS.

- [ ] **Step 5: Commit**

```bash
git add contracts/webassistant-contract-v0.3.json contracts/webassistant-conformance-v0.3.json tests/core/FileSystemCandidateContractTests.cs
git commit -m "test: define candidate filesystem contract"
```

### Task 2: Define filesystem domain types, lexical input policy and stable errors

**Files:**
- Create: `webassist/src/WebAssistant/FileSystem/IRootedFileSystem.cs`
- Create: `webassist/src/WebAssistant/FileSystem/FileSystemModels.cs`
- Create: `webassist/src/WebAssistant/FileSystem/FileSystemException.cs`
- Create: `webassist/src/WebAssistant/FileSystem/FileNamePolicy.cs`
- Modify or replace responsibility of: `webassist/src/WebAssistant/FileSystem/RootedPathResolver.cs`
- Create: `tests/core/FileSystemPolicyTests.cs`

**Interfaces:**
- `IRootedFileSystem` operations: `ListAsync`, `CreateDirectoryAsync`, `OpenReadAsync`, `PublishNewFileAsync`, `MoveNoReplaceAsync`, `DeleteFileAsync`, `DeleteEmptyDirectoryAsync`.
- `FileSystemException.Code` is one of the normalized spec codes and maps later to HTTP status.
- `FileNamePolicy` validates lexical segments and the restricted-extension policy without constructing trusted absolute paths.

- [ ] **Step 1: Write RED lexical/extension/error tests**

Cover absolute POSIX/drive/UNC paths, `.`, `..`, empty interior segment, NUL, embedded separators in name fields, reserved staging namespace, mixed-case restricted extensions and `document.pdf.exe`.

- [ ] **Step 2: Run focused RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter FullyQualifiedName~FileSystemPolicyTests
```

Expected: FAIL because the new types/policy do not exist.

- [ ] **Step 3: Implement minimal domain/policy layer**

Keep lexical validation pure and platform-neutral where possible. Do not use `RootedPathResolver.Resolve()` as a security authorization step. Keep the old resolver only for compatibility tests until all callers migrate, then delete or reduce it to non-authoritative lexical helper behavior.

- [ ] **Step 4: Run focused GREEN and existing rooted-path tests**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter "FullyQualifiedName~FileSystemPolicyTests|FullyQualifiedName~RootedPathResolver"
```

- [ ] **Step 5: Commit**

```bash
git add webassist/src/WebAssistant/FileSystem tests/core/FileSystemPolicyTests.cs
git commit -m "feat: define rooted filesystem domain contract"
```

### Task 3: Implement Linux descriptor-relative containment first

**Files:**
- Create: `webassist/src/WebAssistant/FileSystem/Linux/LinuxNative.cs`
- Create: `webassist/src/WebAssistant/FileSystem/Linux/LinuxRootedFileSystem.cs`
- Create: `tests/core/LinuxRootedFileSystemTests.cs`

**Interfaces:**
- Implements `IRootedFileSystem` on Linux.
- Keeps an opened trusted root dirfd.
- Uses `openat2` with `RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS` for resolution/open and `mkdirat`, `unlinkat`, `renameat2(..., RENAME_NOREPLACE)` / descriptor-relative equivalents for mutation.

- [ ] **Step 1: Write Linux RED tests**

On Linux only, create a temporary root and outside sentinel. Prove ordinary list/create/read/delete/move; then prove `../`, symlink parent, symlink final object and parent-swap attempts cannot read or mutate the outside sentinel. Require hard-link `st_nlink > 1` to be surfaced/rejected according to the spec.

- [ ] **Step 2: Run Linux focused RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter FullyQualifiedName~LinuxRootedFileSystemTests
```

Expected: FAIL because Linux implementation is absent.

- [ ] **Step 3: Implement minimal native wrapper and Linux filesystem**

Use `SafeHandle`/deterministic disposal. Normalize `ENOENT`, `EEXIST`, `ENOTEMPTY`, `ELOOP`, `EXDEV`, permission/lock-style failures into the domain errors without returning absolute paths in exception details.

- [ ] **Step 4: Run focused GREEN repeatedly including race loop**

Require at least hundreds of deterministic parent replacement iterations with oracle = safe in-root completion or normalized safe error; never outside sentinel change.

- [ ] **Step 5: Commit**

```bash
git add webassist/src/WebAssistant/FileSystem/Linux tests/core/LinuxRootedFileSystemTests.cs
git commit -m "feat: add Linux rooted filesystem containment"
```

### Task 4: Implement Windows handle-relative containment

**Files:**
- Create: `webassist/src/WebAssistant/FileSystem/Windows/WindowsNative.cs`
- Create: `webassist/src/WebAssistant/FileSystem/Windows/WindowsRootedFileSystem.cs`
- Create: `tests/core/WindowsRootedFileSystemTests.cs`

**Interfaces:**
- Implements `IRootedFileSystem` on Windows.
- Opens/traverses segments relative to a trusted root/parent handle using `NtCreateFile` + `OBJECT_ATTRIBUTES.RootDirectory` and no-follow reparse semantics.
- Uses handle-based enumeration and mutation (`GetFileInformationByHandleEx`, `SetFileInformationByHandle`/equivalent) with no replace.

- [ ] **Step 1: Write Windows RED tests**

On Windows only, prove normal operations, junction/symlink/reparse rejection, outside sentinel protection under parent replacement, no-replace rename, locked-object normalization and hard-link rejection via link count.

- [ ] **Step 2: Run focused RED on Windows CI**

```powershell
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter FullyQualifiedName~WindowsRootedFileSystemTests
```

Expected: FAIL before implementation.

- [ ] **Step 3: Implement minimal native wrapper and Windows filesystem**

Keep P/Invoke structures/constants private to `WindowsNative`; wrap native handles in `SafeFileHandle`; never re-open a validated full pathname for a security-sensitive operation.

- [ ] **Step 4: Run focused GREEN including race repetitions**

Same command; outside sentinel must remain byte-for-byte unchanged.

- [ ] **Step 5: Commit**

```bash
git add webassist/src/WebAssistant/FileSystem/Windows tests/core/WindowsRootedFileSystemTests.cs
git commit -m "feat: add Windows rooted filesystem containment"
```

### Task 5: Add root lifecycle/factory and keep unrelated capabilities alive

**Files:**
- Create: `webassist/src/WebAssistant/FileSystem/RootedFileSystemProvider.cs`
- Modify: `webassist/src/WebAssistant/Runtime/WebAssistantRuntimeOptions.cs`
- Modify: `webassist/src/WebAssistant/Program.cs`
- Modify: `tests/core/DiagnosticsContractTests.cs`
- Create: `tests/core/FileSystemLifecycleTests.cs`

**Interfaces:**
- Provider exposes availability state and `IRootedFileSystem` when safely initialized.
- Diagnostics reports filesystem capability state without disclosing absolute root.

- [ ] **Step 1: Write RED lifecycle tests**

Require valid root -> available; missing/invalid/link root -> service still starts, `/v1/diag/info` remains available and reports filesystem unavailable; scanner endpoints are not disabled by filesystem failure.

- [ ] **Step 2: Run RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter "FullyQualifiedName~FileSystemLifecycleTests|FullyQualifiedName~DiagnosticsContractTests"
```

- [ ] **Step 3: Implement provider/startup registration**

Select Windows/Linux native implementation by OS. Do not silently switch roots. Avoid throwing during entire app construction for a filesystem-only failure.

- [ ] **Step 4: Run GREEN**

Same command.

- [ ] **Step 5: Commit**

```bash
git add webassist/src/WebAssistant/FileSystem webassist/src/WebAssistant/Runtime/WebAssistantRuntimeOptions.cs webassist/src/WebAssistant/Program.cs tests/core
git commit -m "feat: isolate filesystem capability lifecycle"
```

### Task 6: Implement atomic staging/publication and cleanup semantics

**Files:**
- Create: `webassist/src/WebAssistant/FileSystem/FilePublicationService.cs`
- Extend platform implementations for same-filesystem staging/no-replace commit.
- Create: `tests/core/FilePublicationTests.cs`

**Interfaces:**
- `PublishNewFileAsync(relativePath, Stream source, CancellationToken)` is atomic final-name publication.
- Internal staging namespace constant is centralized and unreachable through public path policy.

- [ ] **Step 1: Write RED publication tests**

Use a controllable slow input stream. Assert final name is absent while transfer is blocked; after release/success exact bytes appear. Assert cancellation/error leaves no final file; concurrent same-name writers yield one success and conflicts; staging entries never surface through `ListAsync` and cannot be explicitly addressed.

- [ ] **Step 2: Run RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter FullyQualifiedName~FilePublicationTests
```

- [ ] **Step 3: Implement streaming staging and no-replace commit**

Use bounded stream copy; close staging handle before atomic publication. Clean up ordinary failures/cancellation. Add startup orphan staging cleanup only inside the reserved staging area.

- [ ] **Step 4: Run GREEN**

Same command plus platform filesystem tests.

- [ ] **Step 5: Commit**

```bash
git add webassist/src/WebAssistant/FileSystem tests/core/FilePublicationTests.cs
git commit -m "feat: publish exchange files atomically"
```

### Task 7: Add public HTTP filesystem endpoints, pagination, CORS and download hardening

**Files:**
- Create: `webassist/src/WebAssistant/Http/FileSystemEndpointHandlers.cs`
- Create: `webassist/src/WebAssistant/Http/FileSystemHttpModels.cs`
- Modify: `webassist/src/WebAssistant/Program.cs`
- Modify: `tests/core/CorsConfigurationTests.cs`
- Create: `tests/core/HttpFileSystemContractTests.cs`

**Interfaces:**
- Exact routes from spec section 4.
- Problem responses include stable `code`.
- Listing default 200, max 1000, opaque cursor.
- Download forces `application/octet-stream`, attachment and `X-Content-Type-Options: nosniff`.

- [ ] **Step 1: Write RED HTTP tests**

Cover list/root, create dir, upload, zero-byte create, download exact bytes/headers, move, delete file/empty dir, non-empty dir error, duplicate destination, blocked extension, restricted external file behavior, invalid path, filesystem unavailable and pagination/cursor validation.

- [ ] **Step 2: Write RED CORS/preflight tests**

Require exact allowed origins and methods GET/POST/PUT/DELETE; rejected origin must not receive permissive ACAO; upload/json mutation shapes must be preflighted cross-origin.

- [ ] **Step 3: Run RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter "FullyQualifiedName~HttpFileSystemContractTests|FullyQualifiedName~CorsConfigurationTests"
```

- [ ] **Step 4: Implement handlers and route mapping**

Keep containment logic out of handlers. Map domain errors centrally to statuses/codes. Do not log user path/name values from handlers.

- [ ] **Step 5: Run GREEN**

Same command plus diagnostics/scanner HTTP regression tests.

- [ ] **Step 6: Commit**

```bash
git add webassist/src/WebAssistant/Http webassist/src/WebAssistant/Program.cs tests/core
git commit -m "feat: expose rooted filesystem API"
```

### Task 8: Build the visual filesystem test page

**Files:**
- Create: `webassist/src/WebAssistant/wwwroot/filesystem.html`
- Modify: `webassist/src/WebAssistant/wwwroot/index.html`
- Create: `tests/core/FileSystemPageContractTests.cs`

**Interfaces:**
- `/filesystem.html` uses only `/v1/filesystem/*` public endpoints.
- Browser-owned `currentPath`; server has no navigation session.

- [ ] **Step 1: Write RED page contract tests**

Assert dedicated page exists, diagnostics page links to it, breadcrumb/root/up/refresh/list controls exist, no absolute root rendering or preview/embed/object of user files exists, and every MVP operation has a control path.

- [ ] **Step 2: Run RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter FullyQualifiedName~FileSystemPageContractTests
```

- [ ] **Step 3: Implement minimal dependency-free HTML/JS file manager**

Render breadcrumb `Root / ...`, buttons `В корень`, `На уровень вверх`, `Обновить`, paged directory table and action controls. Disable traversal/actions on restricted entries. Use download navigation/blob only as file transfer; never inline preview.

- [ ] **Step 4: Run GREEN**

Same command.

- [ ] **Step 5: Commit**

```bash
git add webassist/src/WebAssistant/wwwroot tests/core/FileSystemPageContractTests.cs
git commit -m "feat: add filesystem diagnostic page"
```

### Task 9: Add real Playwright browser acceptance

**Files:**
- Create: `tests/core/FileSystemBrowserTests.cs`
- Modify if needed: `.github/workflows/ci.yml` (or the current required core workflow discovered live)

**Interfaces:**
- Starts WebAssistant on an ephemeral loopback port with isolated temporary RootDirectory.
- Uses installed Playwright Chromium from CI.

- [ ] **Step 1: Write RED browser scenario**

Automate: open `/filesystem.html`; verify Root; create `dir-a`; enter; create nested; breadcrumb back; upload known binary `a.bin`; verify metadata; download and hash; rename `b.bin`; create `dir-b`; move into it; delete file; delete empty dir; return root; delete `dir-a`.

- [ ] **Step 2: Add RED navigation/external-mutation scenario**

Prove Root -> child -> grandchild -> breadcrumb -> Root -> up-at-root stays Root. While page remains open, host test creates/renames/deletes a file directly under root and `Обновить` reflects each state.

- [ ] **Step 3: Add RED browser-negative scenario**

Duplicate destination shows `409`, blocked extension is rejected, non-empty directory delete fails, externally vanished object gives visible `404` and refresh recovers.

- [ ] **Step 4: Run browser tests and confirm RED where UI behavior is incomplete**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter FullyQualifiedName~FileSystemBrowserTests
```

- [ ] **Step 5: Make only UI/API fixes needed for GREEN**

Do not add private test endpoints.

- [ ] **Step 6: Ensure CI installs Playwright Chromium before browser tests**

Use the .NET Playwright install script generated by the restored package. Keep browser acceptance in required CI unless runtime proves prohibitive; if split, preserve a required fast browser scenario and move only high-cost repeats to a dedicated job.

- [ ] **Step 7: Commit**

```bash
git add tests/core/FileSystemBrowserTests.cs .github/workflows
git commit -m "test: exercise filesystem page in Chromium"
```

### Task 10: Prove logging/privacy and regression boundaries

**Files:**
- Modify: `tests/core/DiagnosticsContractTests.cs`
- Create: `tests/core/FileSystemLoggingTests.cs`
- Modify production logging only if RED demonstrates leakage.

**Interfaces:**
- Logs contain operation/result/correlation data, never content and not normal user filenames/relative paths.

- [ ] **Step 1: Write RED privacy tests**

Use unique path and content markers; invoke upload/list/download/move/delete; read daily log and assert neither marker nor base64 nor absolute root is present.

- [ ] **Step 2: Run RED/GREEN cycle**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter "FullyQualifiedName~FileSystemLoggingTests|FullyQualifiedName~DiagnosticsContractTests"
```

- [ ] **Step 3: Commit**

```bash
git add tests/core webassist/src/WebAssistant/Logging webassist/src/WebAssistant/Http
git commit -m "test: protect filesystem logging privacy"
```

### Task 11: Documentation, version transaction and full verification

**Files:**
- Modify: `webassist/docs/api.md`
- Modify relevant runtime/security docs discovered live.
- Modify: `webassist/VERSION` only after functional GREEN.
- Update PR #210 / issue #196 evidence comments.

**Interfaces:**
- Docs exactly match candidate/runtime route and error semantics.

- [ ] **Step 1: Document API and integration model**

Document RootDirectory authority, external third-party mutation, routes, pagination, atomic publication, no-overwrite, restricted file classes, opaque download, link/hardlink behavior, browser page and normalized errors.

- [ ] **Step 2: Run full core suite before version bump**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj
```

Expected: all PASS.

- [ ] **Step 3: Run repository guard / required local validation commands from current repo policy**

Read live workflow/repo-policy first; do not guess obsolete commands.

- [ ] **Step 4: Choose next patch VERSION per repository version discipline and change only `webassist/VERSION`**

Do not change VERSION earlier in the implementation. Add/update any tests that explicitly bind the new version according to existing repository practice.

- [ ] **Step 5: Re-run full verification after VERSION change**

Core tests, contract/conformance validators, browser acceptance, Windows native filesystem tests, Linux native filesystem tests, and package/release-policy tests that are required by the diff.

- [ ] **Step 6: Push exact-head evidence to draft PR and issue**

Record exact head SHA, test counts, Windows/Linux native evidence, browser evidence and known deferred #211 adversarial campaign.

- [ ] **Step 7: Only then move PR from Draft to Ready**

Wait for required CI/repo-guard on the exact head. Do not merge on stale GREEN evidence.
