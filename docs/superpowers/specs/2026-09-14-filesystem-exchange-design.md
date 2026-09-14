# #196 Rooted Filesystem Exchange — Design

## Status

Design for issue #196. This document defines the initial browser-facing filesystem exchange capability and its diagnostic/test page. It does not promote accepted contract/conformance v0.2; filesystem semantics belong to the active candidate transaction until separately accepted.

Baseline when this design was written:

```text
main = 476d882e369470d2a96fd40a447172bc464cdb5b
webassist/VERSION = 0.3.28
stage = PRE-PILOT
accepted authority = webassistant-contract/v0.2 + webassistant-conformance/v0.2
```

## 1. Product purpose

The filesystem capability is a local integration bridge, not merely storage for scanner output.

Its intended topology is:

```text
browser/web application
        ↕ public WebAssistant filesystem API
WebAssistant
        ↕
configured RootDirectory
        ↕
third-party desktop / legacy / local software
```

Third-party software is expected to create, read, rename, move, lock and delete files in the same exchange tree. External mutation is normal operation, not corruption.

Scanning and filesystem capabilities remain independent. The scan API does not gain implicit access to RootDirectory and does not silently write scan results there.

## 2. Root authority and state model

There is exactly one administrator-configured filesystem authority:

```text
WebAssistant:FileSystem:RootDirectory
```

The public filesystem API addresses only root-relative paths. Absolute paths are never accepted or returned.

The server stores no mutable `currentDirectory`. Navigation state belongs to the caller/browser. Every operation carries an explicit root-relative path.

Core invariant:

```text
FILESYSTEM AUTHORITY = configured RootDirectory only
```

No API operation may read, create, modify, rename, move or delete an object outside this authority through traversal, symlink/junction/reparse traversal, hard-link aliasing, path normalization ambiguity, or a check/use race.

## 3. Functional MVP

The initial public capability exposes:

1. list a directory;
2. create a subdirectory;
3. delete an empty directory;
4. upload/create a file by streaming bytes;
5. delete a file;
6. rename or move a file/directory within RootDirectory;
7. download a file;
8. navigate entirely through caller-owned root-relative paths.

Not exposed in the MVP:

- append;
- implicit overwrite/replace;
- recursive delete;
- copy;
- symlink/junction/reparse creation;
- hard-link creation;
- file execution, parsing, conversion or preview;
- changing RootDirectory through the HTTP API.

## 4. Public HTTP surface

The initial API uses one explicit namespace and root-relative paths carried in query/JSON fields rather than catch-all host paths.

```text
GET    /v1/filesystem/list?path=<relative>&cursor=<opaque>&limit=<n>
GET    /v1/filesystem/file?path=<relative>
PUT    /v1/filesystem/file?path=<relative>
DELETE /v1/filesystem/file?path=<relative>
POST   /v1/filesystem/directory
DELETE /v1/filesystem/directory?path=<relative>
POST   /v1/filesystem/move
```

Bodies:

```json
POST /v1/filesystem/directory
{
  "path": "incoming/2026"
}
```

```json
POST /v1/filesystem/move
{
  "sourcePath": "incoming/a.pdf",
  "destinationPath": "done/a.pdf"
}
```

`PUT /v1/filesystem/file` streams the raw request body as opaque bytes; `application/octet-stream` is the canonical content type. A zero-length body is valid.

`GET /v1/filesystem/file` always returns file-transfer semantics:

```text
Content-Type: application/octet-stream
Content-Disposition: attachment
X-Content-Type-Options: nosniff
```

The service never derives response rendering behavior from the filename extension.

## 5. Directory listing and pagination

A listing response contains the normalized current path, entries, and an optional continuation cursor:

```json
{
  "path": "incoming/2026",
  "entries": [],
  "nextCursor": null
}
```

Each entry contains:

```text
name
kind = file | directory | link
size
createdAt
lastModifiedAt
restrictionCode = null | active_extension | link | hardlink
```

`createdAt` and `lastModifiedAt` are UTC timestamps on the wire.

For directories and links, `size` may be `null`; clients must not infer type from size.

The API never returns the host absolute path.

Listing is paged to keep memory and response size bounded:

```text
default limit = 200
maximum limit = 1000
```

`cursor` is opaque to clients and valid only for the same directory path. Because external mutation is normal, pagination is not a snapshot guarantee: entries may move between pages while another process mutates the directory. A browser refresh starts a new listing from the beginning.

## 6. Path and name policy

Input paths are root-relative and segment based.

Rejected:

- absolute Windows, POSIX or UNC paths;
- `.` and `..` navigation segments;
- empty interior segments;
- NUL;
- a path separator embedded inside a single name field;
- names invalid on the current operating system;
- any reserved internal staging path/name.

The service follows native platform naming rules rather than forcing an artificial Windows/Linux common subset. This is deliberate because the exchange directory integrates with native third-party software.

The browser test page displays only the root-relative virtual path, with the top represented as `Root`.

## 7. Destructive semantics

The following behavior is normative:

```text
upload/create when destination exists -> 409 Conflict
move/rename when destination exists    -> 409 Conflict
delete directory                       -> empty directory only
recursive delete                       -> not exposed
append                                 -> not exposed
implicit overwrite / replace           -> never
```

There is no `exists -> then mutate` decision pattern for destructive operations. Conflict behavior is enforced atomically by the underlying filesystem operation.

## 8. Normalized errors

Filesystem errors use `application/problem+json` plus a stable machine-readable `code` field.

Initial normalization:

```text
400 invalid_path
404 not_found
409 destination_exists
409 directory_not_empty
409 unsafe_link
409 hardlink_rejected
422 blocked_file_type
423 locked
503 filesystem_unavailable
```

The browser page displays a human-readable message while retaining the machine code for diagnostic visibility.

## 9. Atomic file publication

Upload is an atomic publication operation for integration with directory-watching software.

Required visibility model:

```text
HTTP body stream
-> internal staging object on the same filesystem
-> complete write
-> flush userspace/runtime buffers
-> close staging handle
-> atomic no-replace rename into final path
```

Before the commit point, the final filename must not exist because of this upload. After the commit point, the final filename refers to the complete uploaded byte sequence.

The implementation owns a reserved internal staging area under RootDirectory, for example `.webassistant-staging`, with the exact name centralized in code. The staging area is not part of the public namespace: it cannot be addressed through the public API and is excluded from public listings.

The staging object:

- lives on the same filesystem as the destination so publication can be a rename, not copy+delete;
- uses an unpredictable internal filename;
- is removed on ordinary cancellation/failure when possible;
- may be cleaned up on later service startup if a crash left it orphaned.

Atomic publication is a visibility guarantee, not a power-loss durability guarantee. The MVP does not promise `fsync`/hardware-stable storage before returning success.

Concurrent uploads to the same final path use atomic no-replace commit: exactly one may succeed; all losers receive `409 Conflict`. Existing destination content is never replaced.

A zero-byte request body with a valid destination path creates an empty file through the same staging/commit path. No separate empty-file primitive is required.

## 10. Atomic move/rename

Move/rename operates only within RootDirectory and must use an atomic same-filesystem rename with no replacement.

If the destination exists, return `409 Conflict`.

The implementation must not silently emulate move with copy+delete. Because source and destination are under one RootDirectory, a cross-filesystem move indicates an invalid root/filesystem topology and is rejected rather than weakened.

## 11. Opaque content model and active-file deny policy

Canonical rule:

```text
USER FILE = UNTRUSTED OPAQUE BYTES
USER FILE != WEB RESOURCE
```

WebAssistant does not execute, parse, transform or render uploaded content. RootDirectory is never mapped as a static web tree.

The initial standalone active-file deny policy is extension based and case-insensitive. The following final extensions are restricted:

```text
.exe .com .bat .cmd
.ps1 .psm1
.vbs .vbe
.js .jse
.wsf .wsh .hta
.msi .msp
.scr .cpl
.sh .bash .zsh .fish
.desktop
.html .htm
.svg
```

The policy evaluates the final extension, so `document.pdf.exe` is restricted.

For API-created content, a restricted destination extension is rejected before writing begins with `422 blocked_file_type`.

If third-party software creates a restricted file directly under RootDirectory, listing may show it with `restrictionCode=active_extension`, but WebAssistant does not download or rename/move it. Deletion of that directory entry is allowed so the browser can clean an unsafe exchange artifact without exposing its bytes.

A rename/move to a restricted destination extension is rejected. A rename/move from an already restricted source is also rejected so the API cannot turn an externally introduced active file into an apparently benign downloadable filename.

This is not content inspection. A renamed executable such as `payload.txt` is not claimed to be detected. Embedded JavaScript/macros inside PDF/DOC/XLS family documents are outside this MVP and those documents remain opaque bytes.

Antivirus/content sanitization is not part of #196.

## 12. Streaming and resource usage

There is no product-level maximum file size in the MVP.

Upload and download must stream with bounded buffering/backpressure. The implementation must not read an arbitrary user file fully into RAM.

Security/path checks happen before the first content byte is exposed to the file handle used for the operation.

Directory listing is bounded by the pagination limits in section 5.

## 13. External mutation, locking and races

A listing is advisory current state, not a stable snapshot for a later mutation.

Expected race normalization:

```text
source disappeared                    -> 404 not_found
destination appeared                  -> 409 destination_exists
object changed into unsafe link       -> 409 unsafe_link
hard-link alias detected              -> 409 hardlink_rejected
OS/share lock prevents operation      -> 423 locked
root/capability temporarily unusable  -> 503 filesystem_unavailable
```

The API does not automatically wait or retry locked destructive operations. The caller may refresh state and retry explicitly.

## 14. Symlink, junction, reparse and hard-link policy

Externally created symbolic links, junctions and other reparse/link objects may be visible in a directory listing as:

```text
kind = link
restrictionCode = link
```

They are diagnostic entries only. The public API does not traverse, read, download, upload-through, rename/move-through or delete them in the MVP.

A path containing a link/reparse component is rejected.

Hard links are treated fail-closed. If the platform reports a regular file link count greater than one, listing may surface the entry with `restrictionCode=hardlink`, but public read/download and destructive operations are rejected. This prevents an in-root directory entry from becoming an alias to content also reachable outside RootDirectory.

The MVP does not attempt a global filesystem search to prove that every hard-link alias is inside RootDirectory.

## 15. Root lifecycle

`RootDirectory` is administrator-owned configuration. The browser/API cannot change it.

Configuration changes take effect after service restart.

At startup the service validates the configured root. The root itself must be a real directory and must not be a symlink/junction/reparse point.

If the filesystem capability cannot safely open/validate the configured root, WebAssistant remains available for unrelated capabilities (for example diagnostics/scanning), but filesystem endpoints return `503 filesystem_unavailable` and diagnostics expose filesystem capability state as unavailable. The service must not silently switch to another directory.

Platform/default installation may create the normal default data directory as part of installation/bootstrap; runtime API requests do not invent a replacement root.

## 16. Security implementation boundary

The existing string-returning `RootedPathResolver` is not a sufficient mutation security boundary under concurrent external mutation.

The filesystem core is an operation abstraction, conceptually:

```text
IRootedFileSystem
  List
  CreateDirectory
  OpenRead
  PublishNewFile
  MoveNoReplace
  DeleteFile
  DeleteEmptyDirectory
```

HTTP handlers pass root-relative paths only. They never receive or construct trusted absolute paths for security decisions.

### 16.1 Linux

Use a root directory descriptor and descriptor-relative operations. Prefer `openat2` with:

```text
RESOLVE_BENEATH
RESOLVE_NO_SYMLINKS
RESOLVE_NO_MAGICLINKS
```

and `*at` family operations for mutation. `renameat2(..., RENAME_NOREPLACE)` is the required no-replace primitive when available on the supported target.

### 16.2 Windows

Use handle-relative traversal rooted in an opened RootDirectory handle, rejecting reparse components while parent handles remain pinned. `NtCreateFile` with `OBJECT_ATTRIBUTES.RootDirectory` is the intended strong primitive where ordinary .NET path APIs cannot provide the invariant.

Rename/delete/listing remain handle-relative/handle-based rather than validating a pathname and reopening it later.

A narrow native interop layer is preferred over weakening containment.

## 17. HTTP/browser security

Existing platform security remains:

```text
loopback-only listener
+ exact CORS allowlist
+ rooted filesystem sandbox
+ least-privilege service identity
```

CORS intentionally allows the methods required by section 4:

```text
GET
POST
PUT
DELETE
```

State-changing cross-origin browser requests require normal browser preflight. `POST` mutation bodies use `application/json`; upload uses `application/octet-stream`; wildcard CORS is forbidden.

The implementation must not choose simple-request shapes merely to bypass OPTIONS.

Protection from arbitrary native local processes is a separate future authentication/threat-model transaction.

## 18. Logging

Filesystem request logs contain technical operation/result information only.

Default application logs must not contain file contents or base64/file previews. To minimize leakage, normal request logs do not include full relative file paths or filenames. Correlation/request identifiers and normalized result codes are sufficient for routine diagnostics.

Security/debug evidence may record synthetic test paths in tests, but production default logging does not turn filenames into a durable audit trail.

## 19. Test filesystem web page

A dedicated repository-owned page is provided at:

```text
/filesystem.html
```

The existing diagnostics page links to it.

The page is a real client of the public filesystem API. It has no private endpoint and no privilege unavailable to a normal allowed browser origin.

It is a small visual file manager constrained to RootDirectory, not a collection of disconnected API forms.

### 19.1 Navigation UX

The page visibly represents the current root-relative location.

Required controls:

- breadcrumb, for example `Root / incoming / 2026 / reports`;
- `В корень`;
- `На уровень вверх`;
- `Обновить`;
- table/list of the current directory.

Directory names are directly navigable. Breadcrumb segments are clickable and return to that ancestor.

At Root, `На уровень вверх` is disabled or is an idempotent no-op that remains at Root. The page must never represent or navigate above Root.

The absolute host path (`C:\...`, `/var/lib/...`) is not shown.

Navigation state is held in browser JavaScript and every API request sends the complete current root-relative path. The server does not acquire session/current-directory state.

### 19.2 Directory table

The current directory view visibly includes at least:

```text
name
kind
size
createdAt
lastModifiedAt
```

Directories are visually distinguishable from files. Restricted entries are visibly marked and unavailable actions are disabled.

The page renders the first listing page and provides `Показать ещё` while `nextCursor` is present. A full refresh restarts pagination from the beginning.

The user can refresh after third-party changes and immediately see current filesystem state.

### 19.3 Operations exposed by the page

The page must allow a human to exercise every filesystem MVP operation:

- list/refresh current directory;
- navigate into a directory;
- navigate to parent/root;
- create directory;
- upload a local file into the current directory;
- create a zero-byte file by submitting a valid destination name with zero bytes;
- download a file;
- rename an entry;
- move an entry to another root-relative destination;
- delete a file;
- delete an empty directory.

Every operation shows success or the normalized API error in the page. Destructive failures do not optimistically remove an entry from the UI; the page refreshes authoritative state.

No user file is previewed or rendered inline, including PDF, HTML or SVG. Download remains download/attachment behavior.

### 19.4 External-integration visibility

The page must support the real integration use case rather than assuming exclusive ownership of the tree.

If a third-party process creates, renames, modifies or removes an object directly under RootDirectory, pressing `Обновить` shows the resulting state.

No client-side cache is treated as filesystem authority.

## 20. Browser integration acceptance

The test page is covered by real browser-level integration tests, not only static source assertions.

Use a headless Chromium browser through Playwright in CI against a running WebAssistant instance configured with an isolated temporary RootDirectory.

The primary browser scenario is:

```text
open /filesystem.html
-> verify Root location
-> create dir-a
-> enter dir-a
-> create nested directory
-> navigate by breadcrumb
-> navigate back into dir-a
-> upload known binary bytes as a.bin
-> verify visible metadata
-> download a.bin and verify exact bytes/hash
-> rename a.bin to b.bin
-> create dir-b
-> move b.bin to dir-b/b.bin
-> enter dir-b
-> delete b.bin
-> return to dir-a
-> delete empty dir-b
-> return to Root
-> delete dir-a
```

The browser test checks both visible DOM state and the actual isolated RootDirectory on disk after relevant steps.

Navigation acceptance additionally proves:

```text
Root -> child -> grandchild
breadcrumb -> child
В корень -> Root
На уровень вверх at Root -> still Root
```

### 20.1 External mutation browser scenario

While the page is open, the integration test directly changes the isolated RootDirectory through host filesystem APIs:

```text
external create -> Refresh -> entry appears
external rename -> Refresh -> new name appears / old disappears
external delete -> Refresh -> entry disappears
```

This is mandatory evidence for the third-party integration use case.

### 20.2 Browser negative scenarios

Browser integration verifies at least:

- duplicate create/upload destination -> visible `409 destination_exists` and original bytes unchanged;
- prohibited active extension -> visible `422 blocked_file_type` and no final object created;
- delete non-empty directory -> visible `409 directory_not_empty` and tree unchanged;
- object externally deleted between listing and requested operation -> visible `404 not_found` and refresh recovers.

## 21. Non-browser integration/security tests

Browser E2E does not replace lower-level filesystem proofs.

Backend/platform integration tests separately prove:

- final upload name is absent while staging is incomplete;
- final name appears only after complete commit;
- committed bytes/hash are exact;
- staging names are inaccessible through the public API/listing;
- concurrent same-destination publish has one winner and conflict losers;
- move uses no-replace semantics;
- traversal/link/reparse escape attempts cannot cross RootDirectory;
- TOCTOU replacement probes cannot redirect an operation outside RootDirectory;
- hard-link aliases are rejected according to this design;
- streaming does not require full-file memory buffering;
- pagination remains bounded and cursors cannot switch directory authority.

Windows and Linux each require executable platform evidence for their native containment layer.

## 22. Candidate contract/conformance

Filesystem routes, responses and error semantics are an observable API delta.

Implementation therefore updates candidate v0.3 contract/conformance (or the then-current candidate) with falsification vectors for:

- root-relative containment;
- exact HTTP surface;
- bounded listing/pagination;
- no overwrite;
- empty-directory-only delete;
- atomic publish;
- opaque download behavior;
- active-extension deny policy;
- link/hard-link rejection;
- external race normalization;
- browser test-page availability and public-API-only behavior.

Accepted v0.2 remains immutable until a separate explicit promotion transaction.

## 23. Completion criteria

#196 implementation is ready for semantic acceptance only when all of the following are true:

- every MVP filesystem operation works through the public API;
- root containment has platform-specific executable evidence on Windows and Linux;
- atomic publication and no-replace semantics are proven under concurrency;
- `/filesystem.html` provides complete visual RootDirectory navigation and all MVP operations;
- headless browser integration exercises the real page end-to-end;
- external host mutation is reflected after page refresh;
- prohibited/unsafe content and link classes fail closed as specified;
- user files remain opaque and are never exposed as static/same-origin content;
- full existing scanner/diagnostics/install regressions remain green;
- candidate contract/conformance reflects the implemented observable semantics;
- accepted v0.2 is unchanged.
