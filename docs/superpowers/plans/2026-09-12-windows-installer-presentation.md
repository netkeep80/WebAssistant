# Windows Installer Presentation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Сделать canonical Windows installer WebAssistant русскоязычным и снабдить его одной воспроизводимой repository-owned графической identity, не меняя scanner/runtime semantics, technical lifecycle IDs и #190 metadata/version authority.

**Architecture:** Сохраняется WiX Toolset 7.0.0 + `WixStandardBootstrapperApplication` с темой `hyperlinkLicense`. Русский UI задаётся explicit repository-owned `ru-RU.wxl`; один SVG является единственным hand-authored graphic source и детерминированно преобразуется repository-owned .NET 10 utility в multi-resolution ICO и PNG 64x64 до сборки Burn bundle. Bundle получает localization/logo/icon до final artifact checksum/provenance freeze.

**Tech Stack:** .NET 10, WiX Toolset 7.0.0, WixStandardBootstrapperApplication, xUnit 2.9.3, `Svg.Skia` 5.2.3, `SkiaSharp` 4.151.2, platform-specific SkiaSharp native assets 4.151.2.

**Spec:** `docs/superpowers/specs/2026-09-12-windows-installer-presentation-design.md`

## Global Constraints

- GitHub `main` is source of truth. Re-fetch live `main` immediately before implementation writes.
- Baseline at plan creation: `main = bed517f8ec1110c62ed7c32e20976b0ad9967f5e`, `webassist/VERSION = 0.3.27`.
- If `main` advances before implementation begins, rebase/refresh the transaction before code changes and recompute the single next VERSION; never force `0.3.28` if it has already been consumed.
- Work only through issue `#192` -> branch `feature/192-windows-installer-presentation` -> Draft PR -> RED -> GREEN -> Ready -> merge.
- Accepted `contracts/webassistant-contract-v0.2.json` and `contracts/webassistant-conformance-v0.2.json` are immutable.
- `repo-policy.json` is immutable.
- Scanner/runtime implementation paths are out of scope.
- `product-metadata.json` schema from #190 is unchanged; graphical branding is not configurable in this transaction.
- `WebAssistant.exe`, Windows Service name `WebAssistant`, Bundle Id `netkeep80.WebAssistant.Bundle`, Package Id `netkeep80.WebAssistant`, and #191 upgrade behavior remain stable.
- `UpgradePreflight` diagnostic tokens remain English and unchanged.
- Linux packaging is out of scope.
- `webassist/VERSION` changes exactly once, only after functional GREEN. Final lifecycle acceptance is run again after that bump on the exact final head.
- Canonical final artifact remains `<effective installerBaseName>-win-x64-<VERSION>.exe`; no `-ru` sibling artifact.
- No post-build patching of accepted installer bytes.

---

## File Structure

New files:

- `webassist/build/windows/installer/localization/ru-RU.wxl` — closed Russian WixStdBA localization surface for pinned WiX 7 `hyperlinkLicense`.
- `webassist/build/windows/installer/branding/webassistant-icon.svg` — only hand-authored graphical authority.
- `webassist/build/windows/installer/branding/WebAssistant.IconGenerator/WebAssistant.IconGenerator.csproj` — pinned deterministic renderer dependencies.
- `webassist/build/windows/installer/branding/WebAssistant.IconGenerator/IconGenerator.cs` — SVG rasterization, PNG output, ICO container creation and validation.
- `webassist/build/windows/installer/branding/WebAssistant.IconGenerator/Program.cs` — fail-closed CLI adapter.
- `tests/core/WindowsInstallerPresentationContractTests.cs` — contract/TDD coverage for localization, stable IDs, WiX wiring and source assets.
- `tests/core/IconGeneratorTests.cs` — deterministic renderer/ICO/PNG/fail-closed tests.

Modified files:

- `contracts/webassistant-contract-v0.3.json` — extend candidate `WA-WIN-INSTALL-001` with default Russian WixStdBA presentation and repository-owned icon invariant.
- `contracts/webassistant-conformance-v0.3.json` — add source paths and `WA-C-WINDOWS-PRESENTATION-001` evidence vector.
- `tests/core/WebAssistant.CoreTests.csproj` — reference icon generator project for renderer tests.
- `webassist/build/windows/installer/Bundle.wxs` — `IconSourceFile`, explicit `LocalizationFile`, `LogoFile`; stable IDs preserved.
- `webassist/build/windows/installer/WebAssistant.Bundle.wixproj` — carry localization/icon/logo paths into WiX preprocessor constants.
- `webassist/build/windows/package.ps1` — invoke generator into isolated staging and pass exact resources to bundle build.
- `webassist/README.md` — factual build/presentation authority documentation.
- `webassist/VERSION` — exactly one transition after functional GREEN.

No change is planned for `Package.wxs`, scanner/runtime source, Linux producer, accepted v0.2, or `repo-policy.json`.

---

### Task 1: Establish #192 governance, Draft PR, and RED presentation contract

**Files:**
- Modify governance authority: GitHub issue `#192` body only
- Create Draft PR from `feature/192-windows-installer-presentation` to `main`
- Create: `tests/core/WindowsInstallerPresentationContractTests.cs`
- Modify: `contracts/webassistant-contract-v0.3.json`
- Modify: `contracts/webassistant-conformance-v0.3.json`

**Interfaces:**
- Consumes: approved spec and current candidate v0.3.
- Produces: explicit candidate presentation requirement/vector and a RED test surface that subsequent tasks satisfy.

- [ ] **Step 1: Re-fetch live repository authority**

Verify immediately before writes:

```text
main SHA
webassist/VERSION
issue #192 state/body
branch head
accepted v0.2 status
candidate v0.3 status
```

If `main != bed517f8ec1110c62ed7c32e20976b0ad9967f5e`, reconcile the feature branch before proceeding and update the plan's VERSION expectation in the PR body, not by silently editing accepted contracts.

- [ ] **Step 2: Add a narrow GovernanceGrant to issue #192 body**

Append exactly this authority:

```yaml
repo-guard-grant:
  authorized_governance_paths:
    - contracts/webassistant-contract-v0.3.json
    - contracts/webassistant-conformance-v0.3.json
  allow_policy_relaxation: []
  allow_atomic_governance_cutover: true
```

State explicitly that no authorization is granted for accepted v0.2, `repo-policy.json`, scanner/runtime semantics, Linux packaging, or #190 metadata schema.

- [ ] **Step 3: Open Draft PR**

Use title:

```text
#192: Russian Windows installer presentation
```

First line:

```text
Closes #192
```

ChangeIntent must cover only the spec/plan, candidate pair, Windows installer presentation sources, generator, relevant tests/docs, `package.ps1`, and the eventual one-time VERSION change. `must_not_touch` must contain accepted v0.2, `repo-policy.json`, scanner/runtime source, Linux packaging and `product-metadata` schema.

- [ ] **Step 4: Write failing presentation contract tests**

Create `tests/core/WindowsInstallerPresentationContractTests.cs` with tests equivalent to:

```csharp
[Fact]
public void Candidate_RequiresRussianWindowsPresentationAndRepositoryOwnedIcon()
{
    var contract = ReadRequired("contracts/webassistant-contract-v0.3.json");
    var conformance = ReadRequired("contracts/webassistant-conformance-v0.3.json");

    Assert.Contains("WA-WIN-INSTALL-001", contract, StringComparison.Ordinal);
    Assert.Contains("рус", contract, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("WixStandardBootstrapperApplication", contract, StringComparison.Ordinal);
    Assert.Contains("repository-owned", contract, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("WA-C-WINDOWS-PRESENTATION-001", conformance, StringComparison.Ordinal);
}

[Fact]
public void WindowsBundle_UsesExplicitRussianLocalizationAndGeneratedBranding()
{
    var bundle = ReadRequired("webassist/build/windows/installer/Bundle.wxs");
    var project = ReadRequired("webassist/build/windows/installer/WebAssistant.Bundle.wixproj");

    Assert.Contains("LocalizationFile=\"$(var.LocalizationFile)\"", bundle, StringComparison.Ordinal);
    Assert.Contains("LogoFile=\"$(var.LogoFile)\"", bundle, StringComparison.Ordinal);
    Assert.Contains("IconSourceFile=\"$(var.BundleIconPath)\"", bundle, StringComparison.Ordinal);
    Assert.Contains("LocalizationFile=$(LocalizationFile)", project, StringComparison.Ordinal);
    Assert.Contains("LogoFile=$(LogoFile)", project, StringComparison.Ordinal);
    Assert.Contains("BundleIconPath=$(BundleIconPath)", project, StringComparison.Ordinal);
}

[Fact]
public void PresentationSources_AreRepositoryOwnedAndStableTechnicalIdsRemainUnchanged()
{
    Assert.True(File.Exists(ToFullPath("webassist/build/windows/installer/localization/ru-RU.wxl")));
    Assert.True(File.Exists(ToFullPath("webassist/build/windows/installer/branding/webassistant-icon.svg")));

    var bundle = ReadRequired("webassist/build/windows/installer/Bundle.wxs");
    Assert.Contains("Id=\"netkeep80.WebAssistant.Bundle\"", bundle, StringComparison.Ordinal);

    var package = ReadRequired("webassist/build/windows/installer/Package.wxs");
    Assert.Contains("Id=\"netkeep80.WebAssistant\"", package, StringComparison.Ordinal);
    Assert.Contains("Name=\"WebAssistant\"", package, StringComparison.Ordinal);
}
```

Reuse the same repository-root helper pattern as `InstallerArtifactContractTests.cs`.

- [ ] **Step 5: Run RED before candidate/production implementation**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter FullyQualifiedName~WindowsInstallerPresentationContractTests
```

Expected: FAIL because candidate v0.3 does not yet state presentation semantics and localization/SVG/WiX wiring do not exist.

Capture exact head SHA, run ID, job ID, pass/fail count in PR body or #192 comment.

- [ ] **Step 6: Update candidate v0.3 only**

Extend `WA-WIN-INSTALL-001` so its statement additionally requires:

```text
canonical Windows bundle uses repository-owned Russian WixStdBA localization as the default interactive presentation;
one repository-owned graphical identity is applied to the bundle executable and Installed Apps/ARP where Burn supports it;
presentation resources do not change technical service/executable/upgrade identifiers or VERSION authority.
```

In `contracts/webassistant-conformance-v0.3.json`:

1. add required repository paths:

```text
webassist/build/windows/installer/localization/ru-RU.wxl
webassist/build/windows/installer/branding/webassistant-icon.svg
webassist/build/windows/installer/branding/WebAssistant.IconGenerator/WebAssistant.IconGenerator.csproj
webassist/build/windows/installer/branding/WebAssistant.IconGenerator/IconGenerator.cs
webassist/build/windows/installer/branding/WebAssistant.IconGenerator/Program.cs
tests/core/WindowsInstallerPresentationContractTests.cs
tests/core/IconGeneratorTests.cs
```

2. add vector:

```json
{
  "id": "WA-C-WINDOWS-PRESENTATION-001",
  "kind": "positive",
  "assertion": "Canonical Windows Burn installer uses explicit repository-owned Russian WixStdBA localization and one repository-owned scanner+file graphical identity rendered reproducibly into the bundle EXE/ARP icon and standard installer logo without changing stable technical lifecycle identifiers.",
  "requirements": ["WA-WIN-INSTALL-001", "WA-ARTIFACT-001", "WA-DOC-001"],
  "evidence": [
    "tests/core/WindowsInstallerPresentationContractTests.cs",
    "tests/core/IconGeneratorTests.cs",
    "webassist/build/windows/installer/localization/ru-RU.wxl",
    "webassist/build/windows/installer/branding/webassistant-icon.svg",
    ".github/workflows/windows-service.yml"
  ]
}
```

Do not edit accepted v0.2.

- [ ] **Step 7: Commit candidate+RED transaction**

Commit message:

```text
#192: define Windows installer presentation contract
```

---

### Task 2: Add complete Russian WixStdBA localization

**Files:**
- Create: `webassist/build/windows/installer/localization/ru-RU.wxl`
- Modify: `tests/core/WindowsInstallerPresentationContractTests.cs`

**Interfaces:**
- Consumes: pinned WiX 7.0.0 `hyperlinkLicense` localization IDs.
- Produces: one explicit Russian `.wxl` that `Bundle.wxs` consumes later.

- [ ] **Step 1: Strengthen RED test to require the closed localization surface**

Add a test that parses `ru-RU.wxl` as XML and requires:

```text
Culture = ru-RU
Language = 1049
```

and every ID in this exact closed set:

```text
Caption
Title
CheckingForUpdatesLabel
UpdateButton
InstallHeader
InstallMessage
InstallMessageOptions
InstallVersion
ConfirmCancelMessage
ExecuteUpgradeRelatedBundleMessage
HelpHeader
HelpText
HelpCloseButton
InstallLicenseLinkText
InstallAcceptCheckbox
InstallOptionsButton
InstallInstallButton
InstallCancelButton
OptionsHeader
OptionsLocationLabel
OptionsPerUserScopeText
OptionsPerMachineScopeText
OptionsBrowseButton
OptionsOkButton
OptionsCancelButton
ProgressHeader
ProgressLabel
OverallProgressPackageText
ProgressCancelButton
ModifyHeader
ModifyRepairButton
ModifyUninstallButton
ModifyCancelButton
SuccessHeader
SuccessCacheHeader
SuccessInstallHeader
SuccessLayoutHeader
SuccessModifyHeader
SuccessRepairHeader
SuccessUninstallHeader
SuccessUnsafeUninstallHeader
SuccessLaunchButton
SuccessRestartText
SuccessUninstallRestartText
SuccessRestartButton
SuccessCloseButton
FailureHeader
FailureCacheHeader
FailureInstallHeader
FailureLayoutHeader
FailureModifyHeader
FailureRepairHeader
FailureUninstallHeader
FailureUnsafeUninstallHeader
FailureHyperlinkLogText
FailureRestartText
FailureRestartButton
FailureCloseButton
FilesInUseTitle
FilesInUseLabel
FilesInUseNetfxCloseRadioButton
FilesInUseCloseRadioButton
FilesInUseDontCloseRadioButton
FilesInUseRetryButton
FilesInUseIgnoreButton
FilesInUseExitButton
```

Also assert that user-visible values contain Cyrillic text and that product-facing strings use `[WixBundleName]` instead of hard-coded `WebAssistant` where a bundle-name variable is applicable.

- [ ] **Step 2: Run localization RED**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter FullyQualifiedName~WindowsInstallerPresentationContractTests
```

Expected: FAIL because `ru-RU.wxl` is absent.

- [ ] **Step 3: Create the Russian localization source**

Create `ru-RU.wxl` with header:

```xml
<WixLocalization
    Culture="ru-RU"
    Language="1049"
    xmlns="http://wixtoolset.org/schemas/v4/wxl">
```

Use these canonical Russian values for the main interaction surface:

```text
Caption = Установка [WixBundleName]
Title = [WixBundleName]
CheckingForUpdatesLabel = Проверка обновлений
UpdateButton = &Обновить до версии [WixStdBAUpdateAvailable]
InstallHeader = Установка
InstallMessage = Программа установки установит [WixBundleName] на этот компьютер. Нажмите «Установить» для продолжения или «Отмена» для выхода.
InstallMessageOptions = Программа установки установит [WixBundleName] на этот компьютер. Нажмите «Установить» для продолжения, «Параметры» для настройки или «Отмена» для выхода.
InstallVersion = Версия [WixBundleVersion]
ConfirmCancelMessage = Прервать установку?
ExecuteUpgradeRelatedBundleMessage = Предыдущая версия
HelpHeader = Справка установщика
HelpCloseButton = &Закрыть
InstallOptionsButton = &Параметры
InstallInstallButton = &Установить
InstallCancelButton = &Отмена
OptionsHeader = Параметры установки
OptionsLocationLabel = Каталог установки:
OptionsPerUserScopeText = Установить [WixBundleName] только для &меня
OptionsPerMachineScopeText = Установить [WixBundleName] для &всех пользователей
OptionsBrowseButton = &Обзор
OptionsOkButton = &ОК
OptionsCancelButton = &Отмена
ProgressHeader = Выполняется установка
ProgressLabel = Операция:
OverallProgressPackageText = Инициализация...
ProgressCancelButton = &Отмена
ModifyHeader = Обслуживание установки
ModifyRepairButton = &Восстановить
ModifyUninstallButton = &Удалить
ModifyCancelButton = &Отмена
SuccessHeader = Операция успешно завершена
SuccessInstallHeader = Установка успешно завершена
SuccessRepairHeader = Восстановление успешно завершено
SuccessUninstallHeader = Удаление успешно завершено
SuccessCloseButton = &Закрыть
FailureHeader = Операция не выполнена
FailureInstallHeader = Установка не выполнена
FailureRepairHeader = Восстановление не выполнено
FailureUninstallHeader = Удаление не выполнено
FailureHyperlinkLogText = Во время выполнения операции возникла ошибка. Исправьте причину и повторите попытку. Дополнительные сведения доступны в <a href="#">журнале установки</a>.
FailureCloseButton = &Закрыть
FilesInUseTitle = Используемые файлы
FilesInUseLabel = Следующие приложения используют файлы, которые необходимо обновить:
FilesInUseCloseRadioButton = Закрыть &приложения и попытаться запустить их снова.
FilesInUseDontCloseRadioButton = &Не закрывать приложения. Для завершения потребуется перезагрузка.
FilesInUseRetryButton = &Повторить
FilesInUseIgnoreButton = &Игнорировать
FilesInUseExitButton = &Выйти
```

Fill the remaining required IDs with direct Russian equivalents consistent with the same terminology; no English user-facing fallback strings are allowed inside this repository-owned `.wxl`. `HelpText` may preserve command-line switches (`/install`, `/repair`, `/uninstall`, `/layout`, `/passive`, `/quiet`, `/norestart`, `/log`) but explanations must be Russian.

- [ ] **Step 4: Run localization GREEN**

Run the same filtered test. Expected: localization-source assertions PASS; WiX wiring assertions may still fail until Task 4.

- [ ] **Step 5: Commit localization**

Commit message:

```text
#192: add Russian WixStdBA localization
```

---

### Task 3: Add canonical SVG and deterministic icon generator

**Files:**
- Create: `webassist/build/windows/installer/branding/webassistant-icon.svg`
- Create: `webassist/build/windows/installer/branding/WebAssistant.IconGenerator/WebAssistant.IconGenerator.csproj`
- Create: `webassist/build/windows/installer/branding/WebAssistant.IconGenerator/IconGenerator.cs`
- Create: `webassist/build/windows/installer/branding/WebAssistant.IconGenerator/Program.cs`
- Create: `tests/core/IconGeneratorTests.cs`
- Modify: `tests/core/WebAssistant.CoreTests.csproj`

**Interfaces:**
- Consumes: one SVG path plus output ICO/PNG paths.
- Produces: `IconGenerator.Generate(string svgPath, string icoPath, string logoPath)` and CLI `--input`, `--ico`, `--logo`.

- [ ] **Step 1: Add generator project reference to core tests**

Add:

```xml
<ProjectReference Include="../../webassist/build/windows/installer/branding/WebAssistant.IconGenerator/WebAssistant.IconGenerator.csproj" />
```

- [ ] **Step 2: Write renderer RED tests**

`tests/core/IconGeneratorTests.cs` must call the real generator and verify:

```csharp
[Fact]
public void Generator_ProducesDeterministicIcoAnd64PxLogo()
{
    // Generate twice into separate temp directories from canonical SVG.
    // Assert SHA-256 of ico1 == ico2 and png1 == png2.
    // Parse PNG IHDR and assert 64x64.
    // Parse ICO directory and assert exactly 16,32,48,256 entries.
}

[Fact]
public void Generator_FailsClosedForMissingOrInvalidSvg()
{
    Assert.ThrowsAny<Exception>(() => IconGenerator.Generate(missing, ico, png));
    Assert.ThrowsAny<Exception>(() => IconGenerator.Generate(invalidSvg, ico, png));
    Assert.False(File.Exists(ico));
    Assert.False(File.Exists(png));
}
```

ICO parser in tests must read ICONDIR/ICONDIRENTRY directly and map byte width/height value `0` to `256`.

- [ ] **Step 3: Run renderer RED**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter FullyQualifiedName~IconGeneratorTests
```

Expected: FAIL because generator project/source does not yet exist.

- [ ] **Step 4: Create the canonical SVG**

Use a 256x256 viewBox and only vector shapes; no text, fonts, external images, filters or remote resources. Initial repository-owned geometry is:

```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
  <rect width="256" height="256" rx="48" fill="#2457D6"/>
  <path d="M72 42h86l26 26v82H72z" fill="#FFFFFF"/>
  <path d="M158 42v30h30" fill="#B9D7FF"/>
  <path d="M52 132h152c11 0 20 9 20 20v48c0 11-9 20-20 20H52c-11 0-20-9-20-20v-48c0-11 9-20 20-20z" fill="#173B91"/>
  <path d="M65 119h126l13 23H52z" fill="#7EC8FF"/>
  <path d="M74 170h108v30H74z" fill="#FFFFFF"/>
  <circle cx="194" cy="154" r="6" fill="#7CFFB2"/>
</svg>
```

This explicitly encodes sheet + folded file corner + scanner body and remains legible without text.

- [ ] **Step 5: Create pinned generator project**

`WebAssistant.IconGenerator.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Svg.Skia" Version="5.2.3" />
    <PackageReference Include="SkiaSharp" Version="4.151.2" />
    <PackageReference Include="SkiaSharp.NativeAssets.Win32" Version="4.151.2" Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
    <PackageReference Include="SkiaSharp.NativeAssets.Linux.NoDependencies" Version="4.151.2" Condition="$([MSBuild]::IsOSPlatform('Linux'))" />
  </ItemGroup>
</Project>
```

No floating package versions.

- [ ] **Step 6: Implement `IconGenerator.Generate`**

Public interface:

```csharp
namespace WebAssistant.IconGenerator;

public static class IconGenerator
{
    public static void Generate(string svgPath, string icoPath, string logoPath);
}
```

Implementation rules:

1. validate all paths and ensure SVG exists;
2. load SVG with `Svg.Skia`;
3. reject null/empty picture or non-positive bounds;
4. render transparent square bitmaps at `16, 32, 48, 256` with high-quality antialiasing and fit preserving aspect ratio;
5. encode each frame as PNG bytes;
6. write ICO manually as deterministic ICONDIR + four ICONDIRENTRY records + PNG payloads in ascending size order;
7. encode a separate 64x64 PNG from the same SVG;
8. write to temporary sibling files first, validate outputs, then atomically move them to requested paths;
9. on any failure delete temporary/final partial outputs and throw.

ICO header algorithm:

```text
ICONDIR:
  reserved ushort = 0
  type ushort = 1
  count ushort = 4

ICONDIRENTRY per size:
  width byte = size == 256 ? 0 : size
  height byte = size == 256 ? 0 : size
  colorCount byte = 0
  reserved byte = 0
  planes ushort = 1
  bitCount ushort = 32
  bytesInRes uint = png.Length
  imageOffset uint = headerSize + entriesSize + sum(previous png lengths)
```

Use explicit little-endian writes via `BinaryWriter`.

- [ ] **Step 7: Implement CLI adapter**

`Program.cs` accepts exactly:

```text
--input <svg>
--ico <ico-output>
--logo <png-output>
```

Unknown/missing/duplicate arguments return non-zero and write a concise error to stderr. Successful generation returns `0`.

- [ ] **Step 8: Run renderer GREEN on current platform**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter FullyQualifiedName~IconGeneratorTests
```

Expected: PASS.

Also run twice manually:

```bash
dotnet run --project webassist/build/windows/installer/branding/WebAssistant.IconGenerator/WebAssistant.IconGenerator.csproj --configuration Release -- --input webassist/build/windows/installer/branding/webassistant-icon.svg --ico /tmp/webassistant-icon.ico --logo /tmp/webassistant-logo.png
```

Inspect hashes to confirm repeated executions are identical on the same toolchain/platform.

- [ ] **Step 9: Commit graphical authority + generator**

Commit message:

```text
#192: add reproducible WebAssistant icon generator
```

---

### Task 4: Wire localization and branding into WiX and canonical Windows producer

**Files:**
- Modify: `webassist/build/windows/installer/Bundle.wxs`
- Modify: `webassist/build/windows/installer/WebAssistant.Bundle.wixproj`
- Modify: `webassist/build/windows/package.ps1`
- Modify: `tests/core/WindowsInstallerPresentationContractTests.cs`
- Modify: `tests/core/InstallerArtifactContractTests.cs` only if the existing stable-ID assertions need additive presentation checks

**Interfaces:**
- Consumes: `ru-RU.wxl`, canonical SVG, generator CLI, #190 effective metadata.
- Produces: final Burn EXE containing Russian presentation resources and generated graphical identity before checksum freeze.

- [ ] **Step 1: Add producer/wiring RED assertions**

Require `package.ps1` to define and validate:

```text
$localizationPath
$iconSourcePath
$iconGeneratorProject
$bundleIconPath
$bundleLogoPath
```

Require it to run the generator before bundle build and pass:

```text
-p:LocalizationFile=<ru-RU.wxl>
-p:BundleIconPath=<generated ico>
-p:LogoFile=<generated png>
```

Require generated outputs to live under `$stagingRoot`, never source tree or final artifact directory.

- [ ] **Step 2: Run wiring RED**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter "FullyQualifiedName~WindowsInstallerPresentationContractTests|FullyQualifiedName~InstallerArtifactContractTests"
```

Expected: FAIL on missing producer/WiX wiring.

- [ ] **Step 3: Modify `Bundle.wxs` minimally**

Final relevant shape:

```xml
<Bundle
    Id="netkeep80.WebAssistant.Bundle"
    Name="$(var.ProductDisplayName)"
    Manufacturer="$(var.ProductCompanyName)"
    Version="$(var.ProductVersion)"
    IconSourceFile="$(var.BundleIconPath)"
    Compressed="yes">

  <BootstrapperApplication>
    <bal:WixStandardBootstrapperApplication
        Theme="hyperlinkLicense"
        LicenseUrl=""
        LocalizationFile="$(var.LocalizationFile)"
        LogoFile="$(var.LogoFile)" />
  </BootstrapperApplication>
```

Do not change Bundle Id, chain package IDs, preflight semantics or MSI visibility.

- [ ] **Step 4: Extend bundle project constants**

`WebAssistant.Bundle.wixproj` `DefineConstants` must append:

```text
LocalizationFile=$(LocalizationFile)
BundleIconPath=$(BundleIconPath)
LogoFile=$(LogoFile)
```

Preserve #190 constants unchanged.

- [ ] **Step 5: Wire generator in `package.ps1`**

Add source inputs:

```powershell
$localizationPath = Join-Path $installerRoot "localization/ru-RU.wxl"
$iconSourcePath = Join-Path $installerRoot "branding/webassistant-icon.svg"
$iconGeneratorProject = Join-Path $installerRoot "branding/WebAssistant.IconGenerator/WebAssistant.IconGenerator.csproj"
```

Include all three in required-path validation.

After `$stagingRoot` exists and before MSI/bundle build:

```powershell
$brandingOutput = Join-Path $stagingRoot "branding"
New-Item $brandingOutput -ItemType Directory -Force | Out-Null
$bundleIconPath = Join-Path $brandingOutput "webassistant-icon.ico"
$bundleLogoPath = Join-Path $brandingOutput "webassistant-logo.png"

& dotnet run `
    --project $iconGeneratorProject `
    --configuration Release `
    -- `
    --input $iconSourcePath `
    --ico $bundleIconPath `
    --logo $bundleLogoPath
if ($LASTEXITCODE -ne 0) {
    throw "Не удалось сформировать Windows presentation assets."
}
foreach ($presentationOutput in @($bundleIconPath, $bundleLogoPath)) {
    if (-not (Test-Path -LiteralPath $presentationOutput -PathType Leaf) -or
        (Get-Item -LiteralPath $presentationOutput).Length -le 0) {
        throw "Не сформирован обязательный presentation asset: $presentationOutput"
    }
}
```

Pass to bundle build:

```powershell
"-p:LocalizationFile=$localizationPath" `
"-p:BundleIconPath=$bundleIconPath" `
"-p:LogoFile=$bundleLogoPath"
```

No presentation operation occurs after copying the bundle to `$artifactPath` and before checksum/provenance verification.

- [ ] **Step 6: Run core GREEN before VERSION bump**

Run full core:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
```

Expected: all tests PASS while VERSION is still the baseline value.

- [ ] **Step 7: Run a Windows canonical producer checkpoint**

On Windows CI/manual canonical environment run only the repository producer:

```powershell
webassist\build\windows\package.bat
```

Expected:

```text
<effective installerBaseName>-win-x64-0.3.27.exe
.sha256
.provenance.json
```

This checkpoint proves functional buildability but is not final release/lifecycle evidence because VERSION has not yet advanced.

- [ ] **Step 8: Commit functional GREEN**

Commit message:

```text
#192: integrate Russian installer presentation
```

Record exact functional-GREEN head and core/producer evidence in PR body.

---

### Task 5: Documentation and one-time VERSION transition

**Files:**
- Modify: `webassist/README.md`
- Modify: `webassist/VERSION`

**Interfaces:**
- Consumes: functional GREEN presentation implementation.
- Produces: factual product documentation and the only VERSION transition for #192.

- [ ] **Step 1: Document presentation authority**

Add a concise Windows packaging subsection stating:

```text
ru-RU.wxl is the canonical default interactive installer localization;
webassistant-icon.svg is the only hand-authored graphical authority;
ICO 16/32/48/256 and WixStdBA PNG 64x64 are generated during canonical packaging;
no machine-local graphics tool is required;
product-metadata.json controls textual identity only and does not override graphical branding;
technical service/executable/upgrade IDs remain stable.
```

Do not document behavior that has not been implemented and tested.

- [ ] **Step 2: Re-run full core on docs head**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
```

Expected: PASS.

- [ ] **Step 3: Resolve the single next VERSION from live main/base**

If baseline is still `0.3.27`, set:

```text
0.3.28
```

If main has advanced, choose the next monotonic patch version over the actual base and document why in PR evidence.

- [ ] **Step 4: Change `webassist/VERSION` exactly once**

No other VERSION commit is allowed for this transaction.

Commit message when baseline remains current:

```text
#192: bump version to 0.3.28
```

- [ ] **Step 5: Verify repo-guard and core on VERSION head**

Expected:

```text
repo-guard = PASS
core = PASS
```

If either fails, fix implementation/tests/docs without another VERSION bump.

---

### Task 6: Final exact-head installer/lifecycle acceptance

**Files:**
- No planned source changes unless a failing acceptance test proves a defect.
- If a defect is found, return to RED -> GREEN on the same VERSION; do not bump again.

**Interfaces:**
- Consumes: final source head with the one VERSION transition.
- Produces: immutable exact-artifact acceptance evidence.

- [ ] **Step 1: Mark Draft PR Ready only after repo-guard/core are GREEN**

Do not change source head while heavy Ready CI is running.

- [ ] **Step 2: Require final Ready CI on exact head**

Final gate must show SUCCESS for:

```text
core / test
repo-guard
Windows Service acceptance / Build Windows installer artifact once
Windows Service acceptance / Windows WiX installer lifecycle
scanner final acceptance / Windows TWAIN direct SDK
scanner final acceptance / Linux SANE direct SDK (if repository classifier still requires it)
ci-fast
ci-required
```

The Windows lifecycle must exercise exact producer bytes and historical `v0.3.21 -> final candidate` live-worker upgrade.

- [ ] **Step 3: Verify artifact identity/evidence**

Check final artifact set:

```text
<effective basename>-win-x64-<final VERSION>.exe
<same>.sha256
<same>.provenance.json
```

Verify recorded SHA matches exact EXE bytes and provenance source SHA equals the final accepted PR head used by producer.

- [ ] **Step 4: Verify no technical identity drift**

From source/tests and lifecycle logs prove unchanged:

```text
WebAssistant.exe
Windows Service = WebAssistant
Bundle Id = netkeep80.WebAssistant.Bundle
Package Id = netkeep80.WebAssistant
UpgradePreflight order/tokens
```

- [ ] **Step 5: Update PR final evidence**

Record exact:

```text
final head SHA
VERSION
core pass count
repo-guard pass count
Ready CI run
Windows producer job
Windows lifecycle job
historical upgrade result
scanner acceptance jobs
ci-required job
```

---

### Task 7: Real Windows visual evidence, merge, and roadmap handoff

**Files:**
- No production source changes expected.
- Evidence is attached to issue/PR comments; screenshots are not build inputs.

**Interfaces:**
- Consumes: exact final installer from Task 6.
- Produces: human visual acceptance and completed #192 transaction.

- [ ] **Step 1: Install/test exact final EXE on real Windows**

Capture screenshots showing:

1. initial installer screen in Russian;
2. maintenance/upgrade interaction in Russian where reachable;
3. installer EXE icon in Explorer;
4. Installed Apps / Programs and Features icon;
5. representative downgrade/failure-facing result that is understandable in Russian.

Use the exact artifact SHA already accepted by CI; do not rebuild locally for screenshots.

- [ ] **Step 2: Human icon review**

Inspect at 16/32/48/256 sizes and confirm:

```text
scanner silhouette remains recognizable;
paper/file cue remains visible;
no text is required to understand the mark;
64x64 WixStdBA logo is not blurry/cropped;
Explorer and ARP use the same graphical identity.
```

If visual changes are requested, treat them as source changes: update SVG, re-run generator/core, re-run full exact-head Ready CI and keep the same VERSION unless repository policy explicitly requires a new transaction. Never patch accepted EXE bytes.

- [ ] **Step 3: Attach visual evidence to #192 and PR**

Document that screenshots correspond to exact artifact SHA and VERSION.

- [ ] **Step 4: Final diff audit**

Before merge confirm changed files do not include:

```text
contracts/webassistant-contract-v0.2.json
contracts/webassistant-conformance-v0.2.json
repo-policy.json
webassist/src/WebAssistant/Scanning/**
webassist/src/WebAssistant/Http/**
webassist/build/linux/**
webassist/build/common/product-metadata.defaults.json
webassist/build/common/ProductMetadataResolver/**
```

- [ ] **Step 5: Merge with expected-head guard**

Merge only if PR remains mergeable and head SHA equals the fully accepted exact head.

- [ ] **Step 6: Post-merge verify main**

Re-fetch:

```text
main SHA
webassist/VERSION
issue #192 closed/completed
```

Run/observe post-merge required CI if repository workflows trigger on push and record outcomes.

- [ ] **Step 7: Update roadmap #160 and distribution roadmap #154**

Record #192 DONE and the new current main/VERSION. Preserve the existing next-order rule from #160; do not silently start unrelated GitLab/filesystem work.

---

## Plan Self-Review

### Spec coverage

- Russian install/upgrade/maintenance/failure UI: Tasks 2, 4, 7.
- Repository-owned SVG and scanner+file visual concept: Task 3.
- Deterministic ICO 16/32/48/256 and PNG 64x64: Task 3.
- Bundle EXE + ARP icon: Task 4 plus real evidence Task 7.
- WixStdBA logo from same SVG authority: Tasks 3-4.
- Stable technical IDs/#191 behavior: Tasks 4, 6.
- #190 filename/version/provenance invariants: Tasks 4-6.
- Accepted v0.2/scanner/Linux exclusions: Global Constraints + Task 7 diff audit.
- One VERSION transition after functional GREEN: Tasks 4-5.
- Final acceptance after VERSION bump: Task 6.
- Real screenshot evidence: Task 7.

### Placeholder scan

No `TBD`, `TODO`, `implement later`, floating dependency versions, undefined output sizes, or unspecified final gates remain.

### Type/interface consistency

- Generator API is consistently `IconGenerator.Generate(string svgPath, string icoPath, string logoPath)`.
- CLI contract is consistently `--input`, `--ico`, `--logo`.
- Generated outputs are consistently `webassistant-icon.ico` and `webassistant-logo.png` under isolated staging.
- WiX properties are consistently `LocalizationFile`, `BundleIconPath`, `LogoFile`.
- Final logo size is consistently 64x64; ICO sizes are consistently 16/32/48/256.
