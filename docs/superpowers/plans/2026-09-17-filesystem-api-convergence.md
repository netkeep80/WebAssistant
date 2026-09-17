# План реализации #230: filesystem API convergence

Дата: 2026-09-17

Owning Issues: #230, #27. Baseline: `main=6561103005e9e380799ac5afe09d5143099146e7`, `VERSION=0.3.43`.

## Цель

Перевести filesystem capability на утверждённую семантику #230 без ослабления native security boundary #196:

```text
READ -> GET
MUTATION -> POST
move files -> сохраняет имя
move directory -> сохраняет имя
rename -> сохраняет parent
```

Добавить wildcard, streaming ZIP, exact-name find и desktop batch-selection. Accepted `v0.2` не менять. `main` protection не входит в работу.

## Архитектурная граница

```text
HTTP/FileSystemEndpointHandlers
  -> FileSystemApplicationService
       wildcard/list
       find
       batch ordinary-file move
       directory move
       rename
       ZIP selection
  -> FileSystemRootRegistry
  -> IRootedFileSystem
  -> Windows/Linux native rooted implementation
```

`IRootedFileSystem.MoveNoReplaceAsync(source,destination)` остаётся низкоуровневой atomic no-replace namespace primitive. Public semantics запрещают accidental rename структурой DTO и application-layer guards. Native implementations меняются только там, где требуется доказать object kind / pre-pagination filtering без TOCTOU/security regression.

## Task 1 — Governance co-change #27

**Изменить:** `repo-policy.json`.

Расширить bounded API semantic source set, сохранив `RequestLoggingMiddleware.cs` исключённым. В trigger должны войти текущие semantic sources и заранее известные #230 sources:

```text
webassist/src/WebAssistant/Program.cs
webassist/src/WebAssistant/Http/ApiVersion.cs
webassist/src/WebAssistant/Http/ScannerEndpointHandlers.cs
webassist/src/WebAssistant/Http/ScannerSettingsEndpointHandlers.cs
webassist/src/WebAssistant/Http/ScanRequest.cs
webassist/src/WebAssistant/Http/ScanCoordinator.cs
webassist/src/WebAssistant/Http/FileSystemEndpointHandlers.cs
webassist/src/WebAssistant/Http/FileSystemHttpModels.cs
webassist/src/WebAssistant/FileSystem/FileSystemApplicationService.cs
webassist/docs/scanner-settings.schema.json
```

`must_change_any = webassist/docs/api.md`.

Проверить repo-guard NEGATIVE/POSITIVE evidence через PR head: без `api.md` изменение bounded source должно быть RED; в финальном PR с `api.md` — GREEN. Policy relaxation не использовать.

## Task 2 — Candidate v0.3 convergence, сначала RED

**Изменить тесты:**
- `tests/core/FileSystemCandidateContractTests.cs`
- при необходимости `tests/core/DependencyOwnershipTests.cs`

**Затем изменить:**
- `contracts/webassistant-contract-v0.3.json`
- `contracts/webassistant-conformance-v0.3.json`

Сначала добавить структурные тесты, которые требуют:

- точную POST-only mutation surface;
- отдельные `/move`, `/directory/move`, `/rename`;
- отсутствие `PUT`/`DELETE` filesystem target semantics;
- wildcard/list, `/files`, `/find`;
- move preserves name; rename same-parent;
- conformance vectors с concrete executable evidence;
- current vendored SDK `.3` присутствует в `requiredRepositoryPaths`, `.2` остаётся historical immutable evidence.

RED должен падать на current candidate. После этого обновить candidate pair до approved #230. Accepted v0.2 не трогать.

## Task 3 — Application-level list/wildcard policy, RED -> GREEN

**Изменить тесты:**
- `tests/core/HttpFileSystemContractTests.cs`
- `tests/core/FileSystemPolicyTests.cs` либо новый `tests/core/FileSystemApplicationServiceTests.cs` (предпочтительно новый файл, если application policy удобно тестировать без HTTP).

**Создать:**
- `webassist/src/WebAssistant/FileSystem/FileSystemApplicationService.cs`

**Изменить при необходимости:**
- `webassist/src/WebAssistant/FileSystem/RootedFileSystemContracts.cs`
- `webassist/src/WebAssistant/FileSystem/LinuxRootedFileSystem.cs`
- `webassist/src/WebAssistant/FileSystem/WindowsRootedFileSystem.cs`

Тесты:

1. wildcard отсутствует -> old listing behavior;
2. `*.xml,*.json` = OR;
3. matching `OrdinalIgnoreCase` на всех ОС;
4. `*.*` = все ordinary files, включая `README`;
5. directories всегда остаются;
6. wildcard фильтрует до pagination;
7. empty/invalid masks и >32 masks -> `filesystem_path_invalid`;
8. cursor нельзя использовать с другим effective wildcard;
9. canonical-equivalent wildcard может продолжить cursor.

Реализация должна позволять filter-before-pagination без загрузки arbitrary directory целиком в RAM. Native listing enumeration остаётся rooted; application policy задаёт bounded selection/cursor scope.

## Task 4 — HTTP cutover POST-only, RED -> GREEN

**Изменить:**
- `tests/core/HttpFileSystemContractTests.cs`
- `tests/core/CorsConfigurationTests.cs`
- `webassist/src/WebAssistant/Http/FileSystemHttpModels.cs`
- `webassist/src/WebAssistant/Http/FileSystemEndpointHandlers.cs`
- `webassist/src/WebAssistant/Program.cs`
- `webassist/src/WebAssistant/FileSystem/FileSystemApplicationService.cs`

Тест-first зафиксировать target routes:

```text
GET  roots/list/file/files/find
POST file
POST file/delete
POST directory
POST directory/delete
POST move
POST directory/move
POST rename
```

И отсутствие filesystem PUT/DELETE compatibility routes.

После удаления последних PUT/DELETE production routes CORS должен разрешать только `GET, POST`.

## Task 5 — Batch ordinary-file move, RED -> GREEN

Тесты должны доказать:

- DTO только `sourcePath`, `destinationPath`, `fileNames`;
- 1..1000 names;
- duplicate/name-as-path rejected 400;
- source/destination directories same root;
- same directory rejected;
- ordinary files only;
- each destination basename == source basename;
- no wildcard;
- conflict/not_found -> skip and continue;
- response only moved names in request order;
- fatal security/native error stops request;
- already successful moves are not rolled back;
- row-single move is same semantics with array length 1.

Не использовать `copy+delete`.

## Task 6 — Directory move и rename, RED -> GREEN

Тесты:

### `/directory/move`
- source = directory object;
- destination = parent directory;
- basename preserved;
- root/source/descendant invalid;
- same root only;
- existing destination 409;
- file passed as directory source rejected;
- no copy/delete fallback.

### `/rename`
- file и directory supported;
- only `path + newName`;
- parent preserved;
- `newName` cannot contain path separators/navigation;
- file final blocked extension policy enforced;
- existing destination 409;
- link/reparse/restricted fails closed.

Production code must make public accidental rename-via-move unrepresentable.

## Task 7 — Exact-name find, RED -> GREEN

Route:

```text
GET /v1/filesystem/find?path=<dir>&name=a.xml&name=b.xml
```

Тесты:
- repeated `name=` only;
- 1..100 names;
- duplicates rejected;
- strict `StringComparison.Ordinal` on all OS;
- actual directory entry casing used on case-insensitive FS;
- ordinary unrestricted files only;
- no wildcard/recursion/dirs/links;
- response order follows request;
- missing names skipped; none -> `200 {fileNames:[]}`.

## Task 8 — Streaming ZIP, RED -> GREEN

**Изменить/создать:**
- `tests/core/HttpFileSystemContractTests.cs`
- при необходимости dedicated ZIP tests;
- `FileSystemApplicationService.cs`
- `FileSystemEndpointHandlers.cs`

Route:

```text
GET /v1/filesystem/files?path=<dir>&wildcard=<...>
```

Tests:
- `application/zip` attachment;
- only immediate ordinary matching files;
- no dirs/recursion;
- entry names are basename only;
- blocked/link/hardlink restrictions cannot be bypassed;
- zero matches -> valid empty ZIP;
- body is streamed, not assembled as arbitrary `byte[]`/`MemoryStream` archive;
- preflight before response where possible;
- late read failure after response starts aborts output rather than claiming valid complete ZIP.

Use `ZipArchive` over `HttpResponse.Body`/streaming callback or equivalent response streaming primitive; open each source through rooted `OpenReadAsync`.

## Task 9 — Browser UI state + wildcard/find/selection, RED -> GREEN

**Изменить:**
- `tests/core/FileSystemPageContractTests.cs`
- `tests/core/FileSystemBrowserTests.cs`
- `webassist/src/WebAssistant/wwwroot/filesystem.html`

Refactor panel state to explicit fields:

```text
relativePath
entries
nextCursor
sortKey/sortDirection
currentRow
wildcard
selectedNames Set
selectionAnchor
findNames/findMarks
loading/generation
```

UI acceptance:
- per-panel wildcard default `*.*`;
- dirs stay visible under filters;
- ZIP per panel current dir+wildcard;
- click single, Ctrl toggle, Shift range;
- non-file/restricted/parent rows excluded;
- LEFT/RIGHT selections independent;
- sort/refresh preserves surviving names;
- directory/root change resets selection;
- header action arrows disabled for empty/same dir;
- exactly one batch `/move` request for selection;
- row file arrow -> array length 1;
- row/DnD directory -> `/directory/move`;
- pencil -> `/rename`;
- find -> exact names -> found rows added to selection;
- zero find is successful/no navigation change.

Не добавлять frontend framework/build chain.

## Task 10 — Docs/current-state contract

**Изменить:**
- `webassist/docs/api.md`
- при необходимости `webassist/README.md`, только если current product docs содержат старую filesystem API surface.
- `webassist/VERSION` -> next patch version only when production implementation is complete and tests specify the release convention.

`api.md` должен описывать только current final #230 surface, без development history. CORS -> GET/POST only. Документация — на русском.

## Task 11 — Verification and PR gates

Перед финальным merge:

1. exact branch head;
2. targeted core tests GREEN;
3. full core GREEN;
4. Playwright browser GREEN;
5. Linux rooted tests GREEN;
6. Windows rooted tests GREEN on Windows CI;
7. accepted v0.2 byte-for-byte unchanged;
8. candidate v0.3 candidate/accepted=false;
9. changed-file scope соответствует #230 + #27 grants;
10. repo-guard GREEN на exact head;
11. CI GREEN на exact head;
12. review exact diff;
13. fixed-head merge;
14. post-merge main checks;
15. final evidence comments in #230 and #27;
16. #213 остаётся OPEN для real post-#230 pilot.

## TDD cadence через GitHub CI

Локальный checkout в текущей среде недоступен, поэтому RED/GREEN evidence фиксируется отдельными commits/runs в feature branch:

```text
plan
-> test-only RED commit
-> GitHub CI: ожидаемый RED по отсутствующей #230 semantics
-> production GREEN commit(s)
-> GitHub CI: GREEN
-> refactor while GREEN
```

Не писать production implementation до наблюдаемого корректного RED.