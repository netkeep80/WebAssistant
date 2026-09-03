# WebAssistant migration design

## Context

`netkeep80/ScannerAgent` is now a read-only source repository. No commits, branches, pull requests, issue edits, or workflow-triggering mutations are allowed there.

The migration source is the exact open PR #120 head:

```text
527b9186e23c75a549cfe1a8c5c44902d584d8de
```

The destination is the public repository:

```text
netkeep80/WebAssistant
```

The product is no longer scanner-only. Scanner functionality remains a module of a broader local browser-facing service.

## Naming

The migration is a complete product rename:

```text
ScannerAgent -> WebAssistant
tmk5scan     -> webassist
```

The rename applies to source directories, namespaces, projects, solution names, executable/service names, systemd units/users, install paths, logs, configuration keys, test projects, workflow paths, documentation, contracts, policy paths, package metadata owned by this project, and user-visible strings.

The fixed NAPS2 package must not retain the old project-owned package identity. Rebuild the same fixed upstream source as:

```text
WebAssistant.NAPS2.Sdk
1.3.0-webassistant.1.450cba65
```

with provenance pinned to upstream NAPS2 commit:

```text
450cba65aaffe6387041050a573051a64cd80fe9
```

Do not alter upstream third-party names such as `NAPS2`, `SANE`, `WIA`, or `TWAIN`.

## Public-repository sanitization

The destination must contain no corporate/private branding or endpoints. At minimum, reject all migrated text containing case-insensitive matches for:

```text
Triumf
depfin.nnov.ru
tmk5
```

The migration must also inspect binary/package metadata for project-owned old identities. `ScannerAgent` is not allowed to survive as the product/service/package identity.

The destination may retain generic technical facts such as GitLab support, Windows, ALT Linux, NAPS2, WIA, TWAIN, SANE, systemd, and .NET.

## Product root and repository model

The public repository remains a development/governance wrapper with an autonomous export root:

```text
webassist/
```

Copying the contents of `webassist/` to the root of another Git repository must remain a supported corporate GitLab workflow. Product build/package/install must not depend on files outside `webassist/`.

## Scanner semantics retained

The migration retains current accepted scanner behavior from the PR #120 source tree:

```text
GET  /
GET  /v1/health
GET  /v1/scanners
POST /v1/scan
POST /v1/scan/feeder
POST /v1/scan/duplex
GET  /v1/diag/info
GET  /v1/diag/logs
```

Scanner invariants remain:

- 0 scanners -> 503;
- 1 scanner -> auto-selection allowed;
- 2+ scanners without `scannerId` -> 409;
- unknown `scannerId` -> 404 before acquisition;
- empty `scannerId` -> 400;
- one physical acquisition globally;
- concurrent competing acquisition -> 409 busy;
- one/multiple pages -> one raw `application/pdf`;
- no silent source fallback;
- Windows: WIA-first, TWAIN only after successful empty WIA enumeration, never after WIA error;
- Linux: direct NAPS2 SDK with `Driver.Sane` and SDK-native device IDs.

The known PR #120 Linux HTTP E2E defect is migrated as a test correction: the busy test must enumerate scanners first and use one explicit `scannerId` for both concurrent requests and the repeat request.

## Runtime JSON configuration

Use standard JSON configuration loaded from the executable directory, independent of process current working directory. Canonical product configuration is `appsettings.json` with a `WebAssistant` section.

Initial shape:

```json
{
  "WebAssistant": {
    "Port": 17654,
    "Cors": {
      "Enabled": false,
      "AllowedOrigins": []
    },
    "FileSystem": {
      "RootDirectory": ""
    },
    "LogDirectory": ""
  }
}
```

Environment/command-line overrides may remain available through normal ASP.NET Core configuration precedence, but product defaults and documented configuration live in JSON.

### CORS

CORS is disabled by default.

When `WebAssistant:Cors:Enabled=false`:

- no CORS policy is applied;
- no hard-coded origin is allowed implicitly.

When enabled:

- `AllowedOrigins` is an explicit exact allowlist;
- wildcard `*` is rejected;
- origins contain only scheme + host + optional port;
- only `http`/`https` origins are accepted;
- current GET/POST API methods are allowed.

## Filesystem root boundary

WebAssistant will later expose filesystem operations, but no arbitrary filesystem endpoint is introduced in this migration without an operation-level requirement.

The migration does introduce the security primitive all future filesystem endpoints must use.

Canonical configuration:

```text
WebAssistant:FileSystem:RootDirectory
```

Default roots when the JSON value is empty:

```text
Windows: %ProgramData%\WebAssistant\data
Linux:   /var/lib/webassistant
Other:   <AppContext.BaseDirectory>/data
```

A dedicated rooted-path component must:

1. canonicalize the configured root once;
2. accept only relative application paths;
3. reject rooted/absolute input;
4. reject `.` and `..` traversal segments;
5. canonicalize the resulting path and require it to remain under the root using OS-appropriate path comparison;
6. reject existing symlink/reparse-point components so an existing link cannot escape the root;
7. return only a validated full path to future filesystem operations.

The installer creates the default data root and grants the service identity access to that root.

This is an application boundary guarantee. Future file operations must call the rooted-path component immediately before I/O; they must not concatenate paths independently.

## HTTPS

HTTPS is expected in a future slice, but it is not implemented speculatively here. The migration must avoid hard-coding architecture that makes HTTPS impossible: listener/configuration code should remain isolated from API/scanner/filesystem modules. Current network exposure remains loopback-only.

## Service and platform identity

Target identity:

```text
Windows Service: WebAssistant
Linux unit:       webassistant.service
Linux user:       webassistant
Linux install:    /opt/webassistant
Linux logs:       /var/log/webassistant
Linux data:       /var/lib/webassistant
Windows logs:     %ProgramData%\WebAssistant\logs
Windows data:     %ProgramData%\WebAssistant\data
```

Build/install entrypoints retain their existing behavior of working independently of the current working directory.

## Contracts and governance

Do not mechanically carry a policy whose paths refer to the old product root.

Migrate accepted contract/conformance history by product rename, then add a new current additive pair for WebAssistant-specific migration requirements: generic service identity, optional JSON CORS, rooted filesystem boundary, and autonomous `webassist` export root. Historical scanner semantics remain immutable after migration.

All contract IDs and conformance vector IDs that encoded the old `SA-` product identity are renamed consistently to `WA-` in the new repository. `repo-policy.json` and workflows must reference only the migrated WebAssistant artifacts/paths.

`repo-guard` remains a blocking governance check. Because the destination repository is public, branch/ruleset protection should be re-evaluated after baseline CI is green rather than assuming the old private-repository limitation still applies.

## CI

Migrate the meaningful blocking checks and update their paths/names for WebAssistant:

- repo-guard;
- core;
- product isolation;
- product autonomy;
- ALT Linux systemd;
- Windows Service;
- virtual scanner.

The destination CI must never call back into the old private repository as a build dependency.

## Issue migration

After the code baseline is stabilized, migrate meaningful open and closed Issues from `ScannerAgent` to `WebAssistant`.

For each migrated Issue:

- sanitize title/body/comments;
- replace product/root names with WebAssistant/webassist;
- remove `Triumf`, `depfin.nnov.ru`, and corporate-specific `tmk5*` details;
- update obsolete old-repository links when a WebAssistant referent exists;
- preserve open/closed state;
- add `Migrated from ScannerAgent #N` provenance without linking the new product to private corporate material.

Do not migrate known accidental tracker garbage:

```text
#74
#75
#76
#104
#105
#106
```

Issue numbers are not expected to be preserved because GitHub allocates them in the destination repository.

## Acceptance

The migration is accepted only when:

- the old repository has received zero mutations;
- WebAssistant contains the PR #120 product behavior plus the explicit migration changes;
- no tracked text contains `Triumf`, `depfin.nnov.ru`, or `tmk5`;
- no product-owned runtime/service/project/package identity remains `ScannerAgent`;
- product root is `webassist`;
- CORS is JSON-configured and disabled by default;
- filesystem root configuration and path-escape guard have RED->GREEN tests;
- Linux direct SDK acquisition remains green;
- the Linux HTTP E2E busy test uses explicit scanner ID and becomes green;
- Windows virtual scanner E2E remains green;
- service/package paths are renamed on both OSes;
- contracts, conformance, repo-policy, CI, docs, and tests consistently use WebAssistant identities;
- all applicable destination CI checks are green on the exact PR head;
- the baseline is merged only after a fresh compare against `main` with `behind_by=0` and exact expected head SHA;
- meaningful Issues are migrated and sanitized after the code baseline is stable.
