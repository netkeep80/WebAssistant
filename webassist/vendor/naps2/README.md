# Fixed NAPS2 SDK provenance

WebAssistant vendors a repository-owned NAPS2 SDK package because the published `NAPS2.Sdk 1.3.0` predates fixes and capability data required by the product's direct scanner adapters.

Current committed package:

- ID: `WebAssistant.NAPS2.Sdk`
- version: `1.3.0-webassistant.2.450cba65`
- file: `../nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.2.450cba65.nupkg`
- source repository: `https://github.com/cyanfish/naps2`
- exact source commit: `450cba65aaffe6387041050a573051a64cd80fe9`
- upstream baseline includes: `Sane: Fix handling of fixed-point WordList options`
- WebAssistant SDK delta: add nullable `PaperSourceCaps.FeederHasPaper`
- WIA mapping: read feeder-ready state when a feeder exists; unreadable state is `null`
- TWAIN mapping: read `CAP_FEEDERLOADED` when supported; unsupported or unreadable state is `null`

The `.2` package is built from that exact public upstream commit with only the package identity/version customization and the minimal feeder-paper-state SDK delta described above. No WebAssistant scanner policy is implemented inside NAPS2; `auto` source selection remains WebAssistant-owned.

The previous immutable `.1` package remains in repository history as the earlier SANE fixed-point baseline and is not mutated in place.

## Rebuild

Run from the `webassist` directory on a machine with Git, Python 3 and .NET SDK 10:

```bash
./vendor/naps2/rebuild-fixed-sdk.sh
```

The rebuild script checks out the exact upstream commit, applies exact-layout fail-closed source edits, performs a project-only `net10.0` build with package generation disabled, and then runs `dotnet pack --no-build`. This avoids building unrelated platform projects and keeps dependency materialization bounded.

NuGet packages are ZIP containers and the pack output is not assumed to be byte-for-byte reproducible across separate invocations. The repository integrity test therefore pins the SHA-256 of the exact committed `.nupkg` bytes.
