# Fixed NAPS2 SDK provenance

WebAssistant vendors a repository-owned NAPS2 SDK package because the published `NAPS2.Sdk 1.3.0` predates the SANE fixed-point `WordList` fix required by the Linux adapter.

Committed package:

- ID: `WebAssistant.NAPS2.Sdk`
- version: `1.3.0-webassistant.1.450cba65`
- file: `../nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.1.450cba65.nupkg`
- source repository: `https://github.com/cyanfish/naps2`
- exact source commit: `450cba65aaffe6387041050a573051a64cd80fe9`
- upstream change: `Sane: Fix handling of fixed-point WordList options`

The package is produced from that exact public upstream commit. Only NuGet package identity/version are changed; the SDK source remains the upstream source at the pinned commit.

## Rebuild

Run from the `webassist` directory on a machine with Git, Python 3 and .NET SDK 10:

```bash
./vendor/naps2/rebuild-fixed-sdk.sh
```

The rebuild script performs a project-only `net10.0` build with package generation disabled, followed by `dotnet pack --no-build`. This deliberately avoids building unrelated platform projects and keeps the dependency materialization bounded.
