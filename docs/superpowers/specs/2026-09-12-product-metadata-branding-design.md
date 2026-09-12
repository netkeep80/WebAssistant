# #190 — configurable product metadata / installer branding design

## Цель

Добавить один cross-platform build-time contract product metadata для Windows и Linux, сохранив `webassist/VERSION` единственным источником версии и не меняя internal technical identity WebAssistant.

## Граница изменения

Observable branding может меняться:

- application/product display name;
- installer artifact basename;
- file description;
- company/vendor name;
- copyright;
- Windows WiX/ARP display identity;
- Linux human-visible service description;
- provenance identity.

Не меняются только из-за branding:

- `WebAssistant.exe` / Linux `WebAssistant` executable name;
- Windows service name `WebAssistant`;
- Linux unit name `webassist.service`;
- install/state paths `/opt/webassist`, `/var/lib/webassist`, Program Files product technical directory where lifecycle compatibility depends on it;
- WiX package/bundle technical IDs and upgrade identity;
- `webassist/VERSION` authority.

## Canonical paths

Public defaults:

`webassist/build/common/product-metadata.defaults.json`

Optional override:

`webassist/src/WebAssistant/product-metadata.json`

Override расположен рядом с `appsettings.json`.

GitHub development repository ignores override only from repository-root `.gitignore` via exact path `webassist/src/WebAssistant/product-metadata.json`.

`webassist/.gitignore` MUST NOT contain this rule. Because downstream export copies the contents of `webassist` to a separate repository root, GitLab receives no GitHub-root ignore rule and can track `src/WebAssistant/product-metadata.json` normally.

## Schema v1

Both defaults and override use one JSON object schema:

```json
{
  "schema": "webassistant-product-metadata/v1",
  "applicationName": "WebAssistant",
  "installerBaseName": "WebAssistant",
  "fileDescription": "WebAssistant",
  "companyName": "WebAssistant",
  "copyright": "Copyright © WebAssistant"
}
```

Defaults file contains all fields. Override requires exact `schema` and may override any subset of the five metadata fields.

Unknown properties are rejected. In particular `version`, `productVersion`, `fileVersion`, assembly/service/executable identifiers and arbitrary build properties are forbidden by the closed schema.

String values must be non-empty, must not contain control characters, and are bounded to 256 Unicode scalar values. `installerBaseName` has the stricter portable filename grammar `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`; `.` and `..` are invalid.

## Single resolver authority

A repository-owned .NET 10 build utility under `webassist/build/common/ProductMetadataResolver` is the only parser/validator/merger used by both canonical producers.

Inputs:

- canonical defaults JSON;
- optional canonical override path.

Output is a normalized line-oriented UTF-8 environment file containing metadata mode, Base64-encoded effective string values, raw input SHA-256 and effective normalized metadata SHA-256. Base64 keeps arbitrary Unicode values data-only and avoids shell/MSBuild quoting differences.

Deterministic precedence:

```text
public defaults
-> optional override
-> validation
-> normalized effective metadata
```

`metadataMode` is `defaults` when override is absent and `override` when present.

The effective metadata hash is SHA-256 over canonical UTF-8 JSON emitted in fixed field order. The input hash is SHA-256 of the actual selected metadata input file: override when present, defaults otherwise.

Any malformed JSON, wrong schema, unknown property, invalid string, invalid basename or forbidden version-like field fails before product publish/package output is accepted.

## Producer integration

Both producer entrypoints call the same resolver before constructing final artifact names.

### Windows

`package.ps1` resolves metadata, decodes the environment file and exports MSBuild environment properties for `dotnet publish` and WiX builds.

`WebAssistant.csproj` maps effective build properties to SDK assembly metadata:

- ProductName/Product -> `applicationName`;
- AssemblyTitle + Description -> `fileDescription`;
- Company -> `companyName`;
- Copyright -> `copyright`.

`ProductVersion`, `Version`, `FileVersion`, `AssemblyVersion` remain derived exclusively from `webassist/VERSION`.

WiX `Package.wxs` and `Bundle.wxs` use effective display name/manufacturer for user-visible Name/Manufacturer/ARP surfaces. Technical service name and package/bundle identifiers remain `WebAssistant`.

Final artifact name:

`<installerBaseName>-win-x64-<VERSION>.exe`

### Linux

`package.sh` uses the same resolver output, exports the same assembly metadata build properties and creates:

`<installerBaseName>-linux-x64-<VERSION>.zip`

The archive root follows the artifact basename. The staged `webassist.service` keeps unit/exec/path semantics unchanged while its `Description=` is rewritten in staging from effective `fileDescription`; the repository template itself remains the technical public-default template.

## Provenance

Both provenance writers add:

- `metadataMode` (`defaults|override`);
- `applicationName`;
- `installerBaseName`;
- `metadataInputSha256`;
- `effectiveMetadataSha256`.

These fields are written only after final artifact bytes exist. SHA-256 evidence continues to hash the already branded final artifact; there is no post-freeze rename or patch.

## Contract semantics

Candidate v0.3 changes `WA-DIST-002`, Windows/Linux installer requirements and artifact provenance semantics from hard-coded `WebAssistant` filenames to parameterized effective metadata while preserving public defaults.

Accepted v0.2 remains byte-for-byte unchanged. `repo-policy.json` remains unchanged.

## Failure semantics

- override absent -> successful public WebAssistant build;
- override present and valid -> all Windows/Linux branding surfaces use the same effective values;
- override malformed/schema-invalid/unknown property/version field/invalid basename -> fail closed before final artifact;
- no partial field application across producers;
- lifecycle technical identifiers remain stable, so existing upgrade/uninstall/service behavior remains compatible.

## Testing strategy

TDD begins with candidate/test changes that fail against current hard-coded producers.

Tests prove:

1. root GitHub ignore contains exact override path while `webassist/.gitignore` does not;
2. canonical defaults/schema/resolver exist;
3. resolver default, partial override and invalid-input behavior;
4. both producers invoke the same resolver and construct basename from effective metadata;
5. VERSION is not part of metadata schema and remains independent;
6. Windows project/WiX map only display metadata, not technical IDs;
7. Linux staging updates human-readable service description without changing unit identity;
8. provenance writers expose identical metadata fields;
9. candidate v0.3 no longer requires hard-coded canonical filenames but preserves default WebAssistant identity;
10. existing Windows upgrade and Linux lifecycle acceptance remain GREEN.
