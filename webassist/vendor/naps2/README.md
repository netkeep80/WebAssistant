# Fixed NAPS2 SDK provenance

WebAssistant vendors a repository-owned NAPS2 SDK package because the published `NAPS2.Sdk 1.3.0` predates fixes and lifecycle/capability data required by the product's direct scanner adapters.

Current committed package:

- ID: `WebAssistant.NAPS2.Sdk`
- version: `1.3.0-webassistant.3.450cba65`
- file: `../nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.3.450cba65.nupkg`
- SHA-256: `e8abde3b7bd7e756eea714883c6e6ed79c6bb5f5052cd630b3dc763e45a50915`
- source repository: `https://github.com/cyanfish/naps2`
- exact source commit: `450cba65aaffe6387041050a573051a64cd80fe9`
- upstream baseline includes: `Sane: Fix handling of fixed-point WordList options`
- WebAssistant SDK delta: add nullable `PaperSourceCaps.FeederHasPaper`
- WIA mapping: read feeder-ready state when a feeder exists; unreadable state is `null`
- TWAIN mapping: read `CAP_FEEDERLOADED` when supported; unsupported or unreadable state is `null`
- worker lifecycle: idempotent shared worker stop task with bounded graceful stop and bounded forced termination
- factory lifecycle: track all owned workers and in-flight starts, reject publication after shutdown begins, and drain ownership through `ShutdownAsync()`
- scanning-context lifecycle: expose a shared `ShutdownAsync()` barrier and make `Dispose` wait for it

The `.3` package is built from that exact public upstream commit with only the package identity/version customization and the WebAssistant-owned feeder-state and worker-lifecycle deltas described above. No WebAssistant scanner policy is implemented inside NAPS2; `auto` source selection remains WebAssistant-owned.

The `.2` package remains immutable and is retained as the previous feeder-paper-state package. Its pinned SHA-256 remains `2dbc6e96cf0d46a554318f3224561861e669dd09b60fc618319c53fed10dcc9f`; it is never rebuilt, overwritten or substituted by the `.3` cutover.

## Rebuild

Run from the `webassist` directory on a machine with Git, Python 3 and .NET SDK 10:

```bash
./vendor/naps2/rebuild-fixed-sdk.sh
```

The rebuild script checks out the exact upstream commit, applies exact-layout fail-closed source edits, performs a project-only `net10.0` build with package generation disabled, and then runs `dotnet pack --no-build`. Build and pack use a stable compiler source mapping and omit debug-path output so random temporary checkout paths do not alter the SDK assembly.

After `dotnet pack`, the script rewrites the `.nupkg` canonically: entries are sorted, timestamps and ZIP attributes are fixed, extra fields/comments are removed, and entries use `ZIP_STORED`. The resulting package is byte-for-byte reproducible across repeated builds of the same exact inputs. The canonical `.3` bytes are 986644 bytes with SHA-256 `e8abde3b7bd7e756eea714883c6e6ed79c6bb5f5052cd630b3dc763e45a50915`.
