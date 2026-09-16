# Multi-root filesystem Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Заменить single-root filesystem WebAssistant на набор именованных логических корней и обновить публичный API, двухпанельный `/filesystem.html`, тесты, документацию и candidate v0.3 без изменения accepted v0.2.

**Architecture:** Существующий `IRootedFileSystem` остаётся authority одного physical root и сохраняет native handle/dirfd containment. Новый логический слой разбирает первый сегмент публичного пути, разрешает его через case-collision-safe registry и диспетчеризует операцию в отдельный root context. Cross-root move отклоняется до native operation.

**Tech Stack:** .NET 10 / ASP.NET Core, native Windows/Linux rooted filesystem implementation, xUnit, Playwright/Chromium, GitHub Actions.

**Spec:** Issue #196, включая комментарии 2026-09-16; #227 только для совместимости будущего развития и не входит в scope.

## Global Constraints

- GitHub = единственный источник истины; никаких direct commits в `main`.
- Accepted `webassistant-contract/v0.2` и `webassistant-conformance/v0.2` неизменяемы.
- Candidate v0.3 остаётся `accepted=false`.
- `WebAssistant:FileSystem` — словарь `logicalRootName -> physicalRootPath`.
- Grammar logical root: `[A-Za-z0-9][A-Za-z0-9._-]*`, `.` и `..` запрещены; case-only collisions запрещены.
- Public path: `<rootName>/<relativePathInsideRoot>` с `/` независимо от ОС.
- Один runtime-unavailable root не ломает остальные roots, scanner или health.
- Cross-root move запрещён; copy+delete fallback запрещён; no-overwrite сохраняется.
- USER FILE = UNTRUSTED OPAQUE BYTES; inline rendering отсутствует.
- Документация и source comments — на русском, кроме буквальных technical identifiers.
- #227 drag-and-drop/keyboard/fullscreen-scroll scope не реализуется.
- `repo-policy.json` не изменяется.

---

### Task 1: Candidate contract/conformance RED

**Files:**
- Modify: `tests/core/FileSystemCandidateContractTests.cs`
- Modify later: `contracts/webassistant-contract-v0.3.json`
- Modify later: `contracts/webassistant-conformance-v0.3.json`

**Interfaces:**
- Produces required observable vocabulary: `/v1/filesystem/roots`, multi-root paths, new stable error codes, same-root move, two-panel browser acceptance.

- [ ] **Step 1: Write failing tests** that require `logicalRootName`, `/v1/filesystem/roots`, `filesystem_not_configured`, `filesystem_configuration_invalid`, `filesystem_root_not_found`, `filesystem_root_unavailable`, `filesystem_path_invalid`, same-root-only move and two-panel UI evidence; retain assertions that v0.3 is candidate and v0.2 is accepted/untouched.
- [ ] **Step 2: Verify RED** with `dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter FullyQualifiedName~FileSystemCandidateContractTests`; expected: failures because candidate contract still describes one `RootDirectory`.
- [ ] **Step 3: Commit RED only** with message `test: define #196 multi-root candidate contract`.

### Task 2: Configuration and logical path RED -> GREEN

**Files:**
- Modify: `tests/core/RuntimeOptionsTests.cs`
- Create: `tests/core/FileSystemRootRegistryTests.cs`
- Modify: `webassist/src/WebAssistant/Runtime/WebAssistantRuntimeOptions.cs`
- Create: `webassist/src/WebAssistant/FileSystem/FileSystemRootRegistry.cs`
- Create: `webassist/src/WebAssistant/FileSystem/FileSystemLogicalPath.cs`

**Interfaces:**
- Produces `FileSystemRootRegistry` with subsystem state and root lookup; produces parsed `RootName` + root-relative `RelativePath`.

- [ ] **Step 1: Write RED tests** for missing/null/empty `FileSystem`, one/many roots, grammar, `.`/`..`, case-only collision, empty path, current-OS absolute path validation, Windows UNC forms where applicable, Linux absolute/mounted forms, unknown root and traversal/mixed-separator/absolute injection.
- [ ] **Step 2: Verify RED** with focused xUnit filter; expected failures from missing registry/model.
- [ ] **Step 3: Implement minimal GREEN** configuration parsing and logical path parsing without opening any physical root during syntactic validation.
- [ ] **Step 4: Verify GREEN** focused tests and existing `RootedPathResolverTests`.
- [ ] **Step 5: Commit** `feat: add multi-root configuration registry`.

### Task 3: Root lifecycle/dispatcher RED -> GREEN

**Files:**
- Modify: `tests/core/FileSystemLifecycleTests.cs`
- Create: `tests/core/MultiRootFileSystemTests.cs`
- Modify: `webassist/src/WebAssistant/FileSystem/RootedFileSystemProvider.cs`
- Modify: `webassist/src/WebAssistant/Program.cs`

**Interfaces:**
- Consumes: registry entries and logical path parser from Task 2.
- Produces: one independently disposable/reacquirable native `IRootedFileSystem` context per configured root.

- [ ] **Step 1: Write RED tests** proving two available roots are isolated, one unavailable root does not break another, invalid whole configuration disables only filesystem capability, scanner/health remain usable, and an unavailable root may be safely reacquired on a later request.
- [ ] **Step 2: Verify RED**; expected failure from singleton single-root provider.
- [ ] **Step 3: Implement minimal GREEN** root-context lifecycle/dispatcher while preserving current `LinuxRootedFileSystem` and `WindowsRootedFileSystem` authority semantics.
- [ ] **Step 4: Verify GREEN** plus existing Linux/Windows rooted filesystem suites.
- [ ] **Step 5: Commit** `feat: dispatch filesystem operations by logical root`.

### Task 4: HTTP API/error model RED -> GREEN

**Files:**
- Modify: `tests/core/HttpFileSystemContractTests.cs`
- Modify: `webassist/src/WebAssistant/Http/FileSystemEndpointHandlers.cs`
- Modify: `webassist/src/WebAssistant/Http/FileSystemHttpModels.cs`
- Modify if needed: `webassist/src/WebAssistant/Http/RequestLoggingMiddleware.cs`

**Interfaces:**
- Adds `GET /v1/filesystem/roots` returning logical names only.
- Existing operations accept logical-root-prefixed paths.

- [ ] **Step 1: Write RED HTTP tests** for root discovery, list/download/upload/create/delete/move under two roots, unknown root, not-configured, invalid configuration, root unavailable, path invalid, cross-root move rejection, no physical path leakage, and existing no-overwrite/restricted/link semantics.
- [ ] **Step 2: Verify RED**; expected failures because endpoint layer still injects one `IRootedFileSystem`.
- [ ] **Step 3: Implement minimal GREEN** dispatcher calls, `/roots`, stable `application/problem+json` mapping and logical-only diagnostics/log fields.
- [ ] **Step 4: Verify GREEN** HTTP tests and CORS regressions.
- [ ] **Step 5: Commit** `feat: expose multi-root filesystem api`.

### Task 5: Candidate contract/conformance GREEN

**Files:**
- Modify: `contracts/webassistant-contract-v0.3.json`
- Modify: `contracts/webassistant-conformance-v0.3.json`

- [ ] **Step 1: Update only candidate v0.3** to the executable multi-root behavior proven by Tasks 2-4.
- [ ] **Step 2: Run `FileSystemCandidateContractTests`**; expected PASS and v0.2 immutability assertions PASS.
- [ ] **Step 3: Commit** `docs: bind candidate v0.3 to multi-root filesystem`.

### Task 6: Two-panel browser contract RED -> GREEN

**Files:**
- Modify: `tests/core/FileSystemPageContractTests.cs`
- Modify: `webassist/src/WebAssistant/wwwroot/filesystem.html`

- [ ] **Step 1: Write RED static page tests** for logical-root buttons, LEFT/RIGHT independent state, two breadcrumbs/toolbars, `..`, one-step upload, icon-based kind display, no textual `Тип` column, exactly three row actions per panel, accessible `title`/`aria-label`, click-to-open/download and no inline-preview elements.
- [ ] **Step 2: Verify RED**; expected failure on current one-panel page.
- [ ] **Step 3: Implement minimal GREEN UI** using the public API only. Every rendered row stores its immutable full logical path; panel navigation uses request generation tokens so stale responses cannot rebind actions to a newer directory/root.
- [ ] **Step 4: Verify GREEN** static page contract tests.
- [ ] **Step 5: Commit** `feat: add two-panel multi-root filesystem page`.

### Task 7: Playwright acceptance RED -> GREEN

**Files:**
- Modify: `tests/core/FileSystemBrowserTests.cs`
- Modify if required by Windows selection: `.github/workflows/core.yml`

- [ ] **Step 1: Replace single-root Playwright scenario with RED multi-root scenario**: discover `archive`/`nfs`; select `archive`; LEFT→`incoming`, RIGHT→`processed`; upload/download; `→` then `←`; rename/delete; independent sort/navigation; `..`; same-directory arrows disabled; switch to `nfs` resets both panels; no archive state survives; one unavailable root leaves another usable; external mutation appears after refresh.
- [ ] **Step 2: Verify RED** against the prior UI/API revision if not already superseded by Task 6 test evidence.
- [ ] **Step 3: Make only acceptance-driven UI/API adjustments** needed for GREEN.
- [ ] **Step 4: Verify Playwright/Chromium GREEN** and existing browser security regressions.
- [ ] **Step 5: Commit** `test: accept #196 multi-root browser workflow`.

### Task 8: Defaults, docs and version

**Files:**
- Modify: `webassist/build/common/default-appsettings.json`
- Modify: `webassist/README.md`
- Modify: `webassist/docs/api.md`
- Modify only where exact search proves stale single-root prose: relevant service/config docs
- Modify: `webassist/VERSION`

- [ ] **Step 1: Change safe default configuration** to an empty `FileSystem` map, meaning not configured; do not invent a default root.
- [ ] **Step 2: Update Russian documentation** for multi-root configuration, `/roots`, logical paths, errors, two-panel UI, breaking migration from old `RootDirectory`, and unchanged installer ownership semantics.
- [ ] **Step 3: Set VERSION** to the next patch relative to the live base (`0.3.42` if base remains `0.3.41`).
- [ ] **Step 4: Run focused docs/config/contract tests**.
- [ ] **Step 5: Commit** `docs: document multi-root filesystem configuration`.

### Task 9: Regression and exact-head acceptance

- [ ] **Step 1: Run full core suite** including Playwright.
- [ ] **Step 2: Run/observe Windows rooted filesystem/publication suite and cross-platform acceptance required by `ci/change-plan.sh` for the exact PR head.
- [ ] **Step 3: Confirm changed files do not include** `repo-policy.json`, accepted v0.2 files or unrelated scanner/installer implementation.
- [ ] **Step 4: Confirm exact-head CI and repo-guard GREEN**.
- [ ] **Step 5: Perform PR review against #196 comments and #227 exclusions; fix findings through new RED tests before implementation changes.
- [ ] **Step 6: Mark PR ready only after exact-head evidence is green; merge only with unchanged expected head SHA; record post-merge SHA/VERSION/evidence in #196.
