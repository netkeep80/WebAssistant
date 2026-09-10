# Linux distribution corrections design

Date: 2026-09-10

Issues: #186, #187, #188
Parent: #157

Base source:

```text
main = bcfc06bb207d5f0f9f58436e8e73d76f26ab9e9b
VERSION = 0.3.20
```

## 1. Context and observed evidence

The first real frozen candidate for `v0.3.20` was built once, lifecycle-tested, and staged as an unpublished Draft Release. It must not be modified or repacked in place.

Manual testing on a real ALT Workstation 10.1 (Autolycus) VM established two important facts:

1. `install.sh` fails before service installation because it hard-codes `libicu74`, which does not exist in the configured p10 repositories.
2. The packaged `app/WebAssistant` binary starts successfully on the same ALT 10.1 host with the system's installed ICU 69 libraries. The diagnostic page works and `/v1/scanners` returns HTTP 200.

Inspection of the exact staged Linux artifact also showed that normal managed `.dll` files are expected in a self-contained .NET Linux publish, but some Windows-oriented managed assemblies are currently present because platform-specific package references are unconditional.

This design replaces the abandoned `v0.3.20` candidate with a new accepted source/version transition rather than mutating any staged bytes.

## 2. Goals

The implementation transaction must:

1. satisfy #186 by producing a versioned Linux ZIP with exactly one top-level directory whose name equals the ZIP basename without `.zip`;
2. satisfy #187 by removing the hard dependency on a specific ICU package major and making ALT runtime dependency handling compatible with ALT Linux 10.1/p10;
3. investigate #188 and remove only platform-specific Windows dependencies that can be removed cleanly at the project/dependency graph level without changing scanner API semantics or Linux scanner behavior;
4. preserve build-once/same-bytes provenance and release invariants;
5. produce a new VERSION after the implementation is otherwise complete and verified.

## 3. Non-goals

This transaction must not redesign the scanner API, stable scanner identifiers, scanner settings schema, TWAIN/WIA aggregation, or any deferred scanner-contract work.

It must not promote candidate v0.3 authority, change accepted v0.2 semantics, relax repo-guard, or modify the existing `v0.3.20` Draft assets in place.

It must not remove `.dll` files merely because of their file extension or name. No post-publish manual pruning is allowed.

## 4. Linux ZIP layout

For version `<VERSION>`, the canonical artifact remains:

```text
WebAssistant-linux-x64-<VERSION>.zip
```

Its archive entries must all be rooted under exactly one directory:

```text
WebAssistant-linux-x64-<VERSION>/
```

Required shape:

```text
WebAssistant-linux-x64-<VERSION>.zip
└── WebAssistant-linux-x64-<VERSION>/
    ├── app/
    ├── VERSION
    ├── install.sh
    ├── uninstall.sh
    └── webassist.service
```

No payload file or directory may exist directly at ZIP root. No absolute paths, `..` traversal components, or second top-level directory are allowed.

The layout must be created before compression by staging the canonical directory and invoking `zip` from its parent. The artifact must not be repacked or mutated after provenance/hash generation.

## 5. ALT Linux runtime dependency contract

The current implementation treats package names as the contract:

```text
libicu74 libgtk+3 libsane sane
```

This is incorrect for ALT Linux 10.1 because ICU package major numbers vary between repositories/releases, while the actual application was proven to run with ICU 69.

The new contract is capability-oriented:

- required runtime capabilities are checked first;
- already satisfied capabilities cause no package-manager mutation;
- ICU compatibility is detected through runtime/library availability rather than a hard-coded `libicuNN` package name;
- GTK/SANE dependencies are likewise considered satisfied when the installed system already provides the required package/runtime capability;
- package installation is attempted only for missing capabilities;
- failure output must identify the missing capability and the attempted package resolution clearly.

For this transaction, ALT Linux 10.1/p10 is the real target evidence. The implementation must not replace `libicu74` with `libicu69` as another hard-coded major-specific contract.

The exact mechanism may use `rpm`, `ldconfig`, and/or package capability queries, provided it is deterministic, testable, and available on supported ALT installations.

## 6. Platform-purity boundary (#188)

The current project file unconditionally references Windows-specific packages such as:

```text
Microsoft.Extensions.Hosting.WindowsServices
NAPS2.Sdk.Worker.Win32
```

The Linux artifact correspondingly contains Windows-oriented managed assemblies such as `NAPS2.Wia.dll`, `NAPS2.Sdk.Worker.Win32.dll`, and `Microsoft.Extensions.Hosting.WindowsServices.dll`.

The implementation must first establish dependency/runtime reachability. It may then condition Windows-only `PackageReference` items on Windows RID/target if all of the following remain true:

- Windows build and service behavior stay GREEN;
- Linux self-contained publish stays GREEN;
- Linux startup stays GREEN;
- Linux scanner enumeration/path stays GREEN;
- no scanner API or adapter architecture change is required.

If satisfying those conditions requires scanner redesign, #188 remains open and is explicitly deferred. That outcome must not block #186/#187 or the next candidate.

`NTwain.dll`, `NAPS2.Wia.dll`, and similarly named files are not removed solely by name. Only dependency-graph evidence may justify removal.

## 7. Expected implementation surfaces

Likely production surfaces:

```text
webassist/build/linux/package.sh
webassist/install/linux/install.sh
webassist/src/WebAssistant/WebAssistant.csproj   # only if #188 clean conditioning is proven
webassist/src/WebAssistant/Program.cs            # only if service registration must be platform-guarded without semantic change
webassist/VERSION                                # exactly one final transition
```

Expected test surfaces include existing packaging/dependency/lifecycle suites. New tests should be added only where existing suites cannot express the regression clearly.

No changes are expected under:

```text
repo-policy.json
contracts/webassistant-contract-v0.2.json
contracts/webassistant-conformance-v0.2.json
webassist/src/WebAssistant/Scanning/**
```

## 8. TDD sequence

Implementation follows strict RED → GREEN:

1. add/extend tests proving current `0.3.20` ZIP layout is wrong;
2. add/extend tests proving `install.sh` incorrectly requires `libicu74` and must accept an ICU capability independent of package major;
3. add platform-purity regression/audit tests for the Linux publish dependency graph;
4. observe RED on the exact tests-only head before production edits;
5. implement #186 minimally and return the relevant tests GREEN;
6. implement #187 minimally and return dependency/install tests GREEN;
7. attempt #188 only through dependency/project conditioning; if the clean boundary fails, document/defer it rather than broadening scope;
8. run core and packaging/lifecycle regressions;
9. bump VERSION exactly once after implementation/tests are complete;
10. run full Ready distribution CI and repo-guard;
11. merge only on exact GREEN head;
12. freeze the new main and build a fresh candidate once.

## 9. Acceptance criteria

The transaction is ready to merge only if:

- a freshly built Linux ZIP has exactly one same-name top-level directory;
- extraction into an arbitrary existing directory creates that directory automatically;
- installer dependency preflight does not require `libicu74` or any fixed ICU major;
- a fixture modeling ALT 10.1 with ICU 69 is accepted without attempting ICU replacement;
- missing runtime capability behavior remains fail-closed and actionable;
- exact Linux payload starts successfully in automated lifecycle testing;
- #188 cleanup, if included, is proven by build/runtime tests on both platforms;
- canonical producer → same-byte acceptance remains intact;
- VERSION advances exactly once from 0.3.20;
- repo-guard and required CI are GREEN on the exact PR head.

After merge, the new frozen candidate must be tested again on the real ALT Workstation 10.1 VM before final PDF/public Release publication.

## 10. Release handling

The existing `v0.3.20` Draft is an unpublished abandoned candidate. It must not be edited, overwritten, or published as the final release after these requirements changed.

The next accepted main transition receives a new VERSION. A fresh `release-candidate.yml` run builds installers once from the new exact merge SHA, lifecycle-tests those exact bytes, and stages a new unpublished Draft candidate under the new version.
