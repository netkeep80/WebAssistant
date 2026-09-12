# Windows Installer Presentation Design

Issue: #192  
Parent roadmap: #160 / #154  
Baseline main at design start: `bed517f8ec1110c62ed7c32e20976b0ad9967f5e`  
Baseline version: `0.3.27`

## Goal

Make the canonical Windows WebAssistant installer look like a finished Russian product without changing the accepted scanner/runtime semantics, stable Windows Service identity, WiX upgrade identity, or the cross-platform metadata contract completed by #190.

The result must preserve the existing canonical Windows distribution architecture:

```text
WebAssistant payload
+ UpgradePreflight
+ internal MSI
+ WiX 7 Burn bundle with WixStandardBootstrapperApplication
-> <effective installerBaseName>-win-x64-<VERSION>.exe
```

The presentation delta is limited to:

1. Russian default user-facing Burn/WixStdBA text;
2. one repository-owned graphical WebAssistant identity derived reproducibly from editable source artwork;
3. application of that graphical identity to the bundle EXE, Installed Apps / ARP, and the standard installer UI where supported;
4. tests and evidence proving that #191 upgrade behavior and #190 artifact/version/provenance rules remain intact.

## Non-goals and invariants

This transaction must not:

- replace WixStandardBootstrapperApplication with a custom bootstrapper application;
- introduce a custom installer UI framework solely for decoration;
- rename `WebAssistant.exe`;
- rename the Windows Service key `WebAssistant`;
- change stable WiX package/bundle upgrade identity;
- change API/runtime identifiers;
- change scanner behavior or scanner contracts;
- change `product-metadata.json` schema or make graphical branding configurable through it;
- change Linux packaging;
- translate developer, CI, MSI, Burn, .NET, or UpgradePreflight diagnostic logs;
- add a second localized installer artifact;
- modify accepted `webassistant-contract/v0.2` or `webassistant-conformance/v0.2`.

`webassist/VERSION` remains the only canonical version authority. #192 performs exactly one normal VERSION transition after functional GREEN, expected from `0.3.27` to `0.3.28` unless main advances before the transaction reaches that point.

## Current architecture and relevant extension points

The current bundle is WiX Toolset 7.0.0 with `WixStandardBootstrapperApplication` using the `hyperlinkLicense` theme. The bundle already receives effective textual metadata from #190 through `ProductDisplayName`, `ProductCompanyName`, and `ProductFileDescription`.

The pinned WiX 7.0.0 `HyperlinkTheme.wxl` defines the user-visible localization IDs used by the current theme, including install, cancel, options, progress, modify, repair, uninstall, success, failure, restart, and Files In Use strings. The design uses those IDs as the closed localization surface for this transaction.

The pinned WiX 7.0.0 `HyperlinkTheme.xml` renders `logo.png` in a `64x64` `ImageControl`. Therefore the canonical WixStdBA raster logo output for #192 is exactly `64x64` pixels; no later layout decision is required.

WixStdBA exposes `LocalizationFile` and `LogoFile` presentation hooks, while the bundle exposes its icon surface. These existing hooks are preferred over a custom BA because they preserve the standard WiX interaction model and #191 lifecycle behavior.

## Chosen approach

Keep WixStdBA and add three repository-owned presentation inputs:

```text
webassist/build/windows/installer/localization/ru-RU.wxl
webassist/build/windows/installer/branding/webassistant-icon.svg
webassist/build/windows/installer/branding/<generated outputs in staging>
```

The `.wxl` file is source-controlled and is the canonical authority for Russian installer copy.

The SVG is source-controlled and is the canonical authority for graphical branding. Binary icon/logo derivatives are generated deterministically during the canonical Windows producer before the WiX bundle is built. Generated derivatives are staging artifacts, not manually edited authorities.

The final bundle remains a single locale-default Russian installer. No separate `-ru` artifact is created.

## Russian localization design

### Scope

`ru-RU.wxl` must provide all localization IDs consumed by the pinned WiX 7.0.0 `hyperlinkLicense` theme that are visible in normal install/upgrade/maintenance/failure interaction.

The covered groups include:

- bundle caption/title and version;
- initial install copy;
- install/cancel/options buttons;
- options labels;
- progress labels;
- maintenance/modify copy;
- repair/uninstall actions;
- success copy;
- restart copy;
- failure copy;
- log-file explanation where owned by WixStdBA localization;
- Files In Use copy and actions;
- confirmation prompts;
- previous-version / upgrade-related copy;
- built-in help text where exposed by the standard BA.

The Russian text should be concise, neutral, and operational. Product names continue to flow through `[WixBundleName]`; localization must not hard-code `WebAssistant` where WixStdBA already provides a bundle-name variable, preserving #190 textual branding.

### Default locale behavior

The canonical Windows producer must explicitly select the repository-owned Russian localization rather than relying on the machine locale or on whatever localization resources happen to ship in the WiX extension package.

Therefore the build outcome is deterministic:

```text
same source + same VERSION + same product metadata
-> same Russian presentation resources
```

The installer is Russian by default for the intended deployment. This transaction does not add runtime language selection.

### UpgradePreflight boundary

`UpgradePreflight` messages such as `preflight-start`, `preflight-pass`, and `preflight-fail` remain English/machine-oriented diagnostics. They are written to console/logging surfaces and are part of acceptance diagnostics, not the normal interactive installer UI.

No localization work may alter their tokens because #191 acceptance depends on them as evidence.

### Downgrade and failure behavior

The existing deterministic downgrade policy remains unchanged. The UI must present a comprehensible Russian user-facing failure/downgrade result where WixStdBA exposes localizable text. Low-level MSI/Burn error details, numeric codes, and tool-generated diagnostics may remain English.

The acceptance criterion is not "every byte of error text is Russian". It is:

```text
user can understand in Russian that installation/upgrade/maintenance failed or is not permitted,
while technical diagnostic detail remains intact for support.
```

## Graphical identity design

### Canonical visual concept

The icon represents WebAssistant as a bridge between physical scanning and files/filesystem structure.

The canonical concept is a compact geometric mark containing:

- a simplified scanner base;
- a sheet emerging from or entering the scanner;
- a file/folder cue integrated into the sheet or its silhouette.

Constraints:

- no text or letters inside the icon;
- no third-party scanner/folder trademark or copied icon asset;
- recognizable at 16x16;
- strong silhouette with minimal interior detail;
- no dependence on gradients or tiny strokes for recognizability;
- visually coherent at 16, 32, 48, and 256 px;
- original repository-owned artwork.

### Source of truth

The editable canonical source is:

```text
webassist/build/windows/installer/branding/webassistant-icon.svg
```

The SVG is the only hand-authored graphical authority. Generated `.ico` and raster logo files must not become independent manually maintained sources.

### Deterministic renderer

Generation is performed by a small repository-owned .NET 10 utility invoked by the canonical Windows producer. The renderer uses a pinned managed SVG/raster library version committed through normal NuGet project metadata.

The generator consumes only the repository SVG and explicit output dimensions. It emits:

```text
webassistant-icon.ico
  - 16x16
  - 32x32
  - 48x48
  - 256x256

webassistant-logo.png
  - 64x64
```

The `64x64` raster size matches the pinned WiX 7.0.0 `HyperlinkTheme.xml` `ImageControl` exactly.

The output is written under the producer's isolated staging directory and is removed with other staging artifacts after packaging.

The build must fail closed if:

- the SVG is missing;
- rendering fails;
- the ICO does not contain all required dimensions;
- generated outputs are missing or empty;
- the generated WixStdBA PNG is not exactly 64x64.

No machine-local ImageMagick, Inkscape, Photoshop, or manual conversion step is part of canonical packaging.

## WiX integration

The existing `Bundle.wxs` remains the central bundle definition. Presentation integration must use the standard WiX hooks only.

Required mapping:

```text
ru-RU.wxl
  -> WixStandardBootstrapperApplication LocalizationFile

webassistant-icon.ico
  -> Bundle icon / final installer EXE icon
  -> Installed Apps / ARP icon where provided by Burn

webassistant-logo.png
  -> WixStandardBootstrapperApplication LogoFile
```

The existing `Theme="hyperlinkLicense"` is retained. A custom theme or custom BA is not authorized by this design; if pinned WiX 7 build evidence unexpectedly proves either is technically required, implementation stops and returns to design review rather than silently expanding scope.

No icon/localization change may change:

```text
Bundle Id = netkeep80.WebAssistant.Bundle
Package Id = netkeep80.WebAssistant
Service Name = WebAssistant
payload executable = WebAssistant.exe
```

Textual product identity continues to come from #190 effective metadata.

## Build data flow

Canonical Windows packaging becomes:

```text
resolve VERSION
resolve #190 effective product metadata
publish UpgradePreflight
publish WebAssistant payload
render SVG -> ICO + 64x64 PNG into isolated staging
build internal MSI
build Burn bundle with:
  - resolved textual product metadata
  - explicit Russian localization
  - generated ICO
  - generated WixStdBA logo
copy bundle bytes to canonical versioned artifact
freeze SHA-256 + provenance
cleanup staging
```

Presentation resources must be applied before artifact SHA/provenance freeze. There is no post-build executable patching.

## Provenance boundary

#190 provenance fields remain authoritative and unchanged. #192 does not expand `product-metadata.json` or redefine provenance semantics merely to record the icon.

The presentation inputs are repository source files at the exact source SHA already recorded in artifact provenance. Therefore the exact source SHA provides the immutable linkage to the `.wxl`, SVG, generator source, and pinned renderer dependency.

If implementation discovers that an additional presentation hash is required to preserve existing build-evidence invariants, that is allowed only as an additive provenance field and must be justified by a failing test before implementation.

## Testing strategy

Development follows strict RED -> GREEN.

### Static/contract tests

Tests must initially fail because the repository does not yet contain the #192 presentation surface. They then prove at minimum:

- a repository-owned `ru-RU.wxl` exists;
- it declares Russian culture/language and contains the required WixStdBA localization IDs for the pinned theme;
- the bundle project/source explicitly consumes the localization file;
- the SVG authority exists at the canonical path;
- the renderer project is pinned and deterministic by construction;
- the bundle receives generated icon/logo paths;
- stable technical IDs remain unchanged;
- scanner/runtime paths are untouched;
- VERSION changes only once after functional GREEN.

### Renderer tests

The generator must be exercised against the canonical SVG and prove:

- deterministic output for identical input;
- ICO contains 16, 32, 48, and 256 px images;
- PNG output is exactly 64x64;
- missing/invalid SVG fails closed.

Pixel-perfect visual similarity is not a unit-test requirement. Human visual review is required for readability and concept quality.

### Windows artifact acceptance

The existing canonical Windows producer must build the actual bundle on Windows using the new presentation resources.

Existing #191 lifecycle acceptance remains mandatory and must stay GREEN, including historical `v0.3.21 -> candidate` live-worker upgrade, install, repair/uninstall paths already exercised by the harness, immutable artifact consumption, and no Files In Use regression.

Before the VERSION transition, functional GREEN may include targeted build/tests on the current branch VERSION. Those results are development evidence only and are not final release evidence.

After the single VERSION transition, all final artifact/lifecycle evidence must be regenerated on the exact final head and final VERSION. No pre-bump installer bytes may satisfy final #192 acceptance.

The final exact-head gate must include:

- core tests GREEN;
- repo-guard GREEN;
- canonical Windows producer GREEN;
- Windows WiX lifecycle GREEN;
- scanner acceptance gates unchanged/GREEN where required by repository CI;
- final `ci-required` GREEN.

### Real Windows visual evidence

Automated tests cannot prove Explorer shell rendering or subjective small-size readability. Final #192 acceptance therefore also requires real Windows screenshot evidence showing:

1. Russian installer initial/install UI;
2. representative upgrade/maintenance UI in Russian where reachable;
3. final installer EXE icon in Explorer;
4. Installed Apps / ARP icon where the platform exposes it;
5. representative failure or downgrade-facing Russian result.

The screenshots are acceptance evidence, not binary inputs to the build.

## Version transaction

The branch starts from VERSION `0.3.27` at baseline `bed517f8ec1110c62ed7c32e20976b0ad9967f5e`.

The sequence is:

```text
presentation tests RED
-> localization + icon generator + WiX integration functional GREEN
-> targeted pre-bump build/lifecycle checks as useful development evidence
-> exactly one VERSION transition
-> final exact-head canonical Windows artifact + lifecycle GREEN
-> final Ready CI / ci-required GREEN
-> real Windows visual evidence against the final-version artifact
-> merge
```

If `main` advances before implementation is ready, the implementation plan must rebase the expected version transition on the then-current canonical VERSION; it must not force `0.3.28` if that version has already been consumed.

## Expected files

Likely new files:

```text
webassist/build/windows/installer/localization/ru-RU.wxl
webassist/build/windows/installer/branding/webassistant-icon.svg
webassist/build/windows/installer/branding/WebAssistant.IconGenerator/WebAssistant.IconGenerator.csproj
webassist/build/windows/installer/branding/WebAssistant.IconGenerator/Program.cs
```

Likely modified files:

```text
webassist/build/windows/package.ps1
webassist/build/windows/installer/Bundle.wxs
webassist/build/windows/installer/WebAssistant.Bundle.wixproj
tests/core/InstallerArtifactContractTests.cs
webassist/README.md or Windows packaging documentation, only where required to explain reproducible presentation resources
webassist/VERSION, exactly once after functional GREEN
```

Exact file names for generated staging outputs are implementation details but must be stable within the producer and covered by tests.

## Pinned upstream references

Implementation and tests should reason against the pinned WiX 7.0.0 sources that match the repository dependency, not an arbitrary newer WiX version:

```text
wixtoolset/wix@v7.0.0
src/ext/Bal/stdbas/Resources/HyperlinkTheme.xml
src/ext/Bal/stdbas/Resources/HyperlinkTheme.wxl
```

The current repository pins:

```text
WixToolset.Sdk/7.0.0
WixToolset.BootstrapperApplications.wixext 7.0.0
```

## Acceptance mapping

#192 is complete only when all of the following are proven:

- install flow user-facing text is Russian;
- upgrade flow user-facing text is Russian;
- uninstall/maintenance flow user-facing text is Russian where applicable;
- representative failure/downgrade result is understandable in Russian;
- original scanner + file/filesystem icon source exists in repository;
- deterministic ICO includes 16/32/48/256;
- canonical installer EXE displays that icon;
- Installed Apps / ARP uses the same visual identity where supported by Burn;
- WixStdBA uses a 64x64 graphical asset derived from the same SVG authority;
- small-size icon is visually recognizable;
- #191 deterministic upgrade acceptance remains GREEN;
- #190 VERSION/filename/provenance rules remain intact;
- technical identifiers remain unchanged;
- no scanner/API behavior changes;
- final-version exact-head CI is GREEN;
- real Windows screenshots are attached as final evidence.
