# Windows Installer Presentation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Сделать canonical Windows installer WebAssistant русскоязычным и снабдить его одной воспроизводимой repository-owned графической identity, не меняя scanner/runtime semantics, technical lifecycle IDs и #190 metadata/version authority.

**Architecture:** Сохраняется WiX Toolset 7.0.0 + `WixStandardBootstrapperApplication` с темой `hyperlinkLicense`. Русский UI задаётся explicit repository-owned `ru-RU.wxl`; один SVG является единственным hand-authored graphic source и детерминированно преобразуется repository-owned .NET 10 utility в multi-resolution ICO и PNG 64x64 до сборки Burn bundle. Bundle получает localization/logo/icon до final artifact checksum/provenance freeze.

**Tech Stack:** .NET 10, WiX Toolset 7.0.0, WixStandardBootstrapperApplication, xUnit 2.9.3, `Svg.Skia` 5.2.3, `SkiaSharp` 4.151.2, platform-specific SkiaSharp native assets 4.151.2.

**Spec:** `docs/superpowers/specs/2026-09-12-windows-installer-presentation-design.md`

## Global Constraints

- GitHub `main` is source of truth. Re-fetch live `main` immediately before implementation writes.
- Baseline at plan creation: `main = bed517f8ec1110c62ed7c32e20976b0ad9967f5e`, `webassist/VERSION = 0.3.27`.
- If `main` advances before implementation begins, reconcile the feature branch before code changes and recompute the one allowed next VERSION.
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

### Task 1: Governance, Draft PR, candidate delta and first RED

**Files:**
- Modify governance authority: issue `#192` body only
- Create Draft PR from `feature/192-windows-installer-presentation` to `main`
- Create: `tests/core/WindowsInstallerPresentationContractTests.cs`
- Modify: `contracts/webassistant-contract-v0.3.json`
- Modify: `contracts/webassistant-conformance-v0.3.json`

**Interfaces:**
- Consumes: approved spec and current unaccepted candidate v0.3.
- Produces: explicit candidate presentation requirement/vector and RED tests for the missing implementation.

- [ ] **Step 1: Re-fetch live authority**

Verify immediately before writes:

```text
main SHA
webassist/VERSION
issue #192 state/body
feature branch head
accepted v0.2 accepted=true
candidate v0.3 accepted=false
```

If `main` moved, reconcile branch before implementation and use the actual next monotonic patch VERSION later.

- [ ] **Step 2: Add exact GovernanceGrant to issue #192 body**

Append this fenced block exactly:

````markdown
```repo-guard-grant
authorized_governance_paths:
  - contracts/webassistant-contract-v0.3.json
  - contracts/webassistant-conformance-v0.3.json
allow_policy_relaxation: []
allow_atomic_governance_cutover: true
```
````

Immediately after it state:

```text
No authorization is granted for repo-policy.json, accepted v0.2, scanner/runtime semantics, Linux packaging, #190 product-metadata schema, or technical service/executable/upgrade identifiers.
```

- [ ] **Step 3: Open Draft PR**

Title:

```text
#192: Russian Windows installer presentation
```

First line:

```text
Closes #192
```

ChangeIntent scope must contain only spec/plan, candidate v0.3 pair, Windows installer presentation sources, generator, relevant tests/docs, `webassist/build/windows/package.ps1`, and eventual `webassist/VERSION`. `must_not_touch` must include accepted v0.2, `repo-policy.json`, `webassist/src/WebAssistant/Scanning/**`, `webassist/src/WebAssistant/Http/**`, `webassist/build/linux/**`, and `webassist/build/common/ProductMetadataResolver/**`.

- [ ] **Step 4: Write presentation RED tests**

Create `tests/core/WindowsInstallerPresentationContractTests.cs` using the same repository-root helper pattern as `InstallerArtifactContractTests.cs` and these assertions:

```csharp
[Fact]
public void Candidate_RequiresRussianWindowsPresentationAndRepositoryOwnedIcon()
{
    var contract = ReadRequired("contracts/webassistant-contract-v0.3.json");
    var conformance = ReadRequired("contracts/webassistant-conformance-v0.3.json");

    Assert.Contains("WA-WIN-INSTALL-001", contract, StringComparison.Ordinal);
    Assert.Contains("WixStandardBootstrapperApplication", contract, StringComparison.Ordinal);
    Assert.Contains("рус", contract, StringComparison.OrdinalIgnoreCase);
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
public void StableTechnicalInstallerIdsRemainUnchanged()
{
    var bundle = ReadRequired("webassist/build/windows/installer/Bundle.wxs");
    var package = ReadRequired("webassist/build/windows/installer/Package.wxs");

    Assert.Contains("Id=\"netkeep80.WebAssistant.Bundle\"", bundle, StringComparison.Ordinal);
    Assert.Contains("Id=\"netkeep80.WebAssistant\"", package, StringComparison.Ordinal);
    Assert.Contains("Name=\"WebAssistant\"", package, StringComparison.Ordinal);
    Assert.Contains("WebAssistant.exe", package, StringComparison.Ordinal);
}
```

- [ ] **Step 5: Run RED before candidate/production edits**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter FullyQualifiedName~WindowsInstallerPresentationContractTests
```

Expected: FAIL only on the new #192 assertions. Record exact head/run/job/pass-fail evidence.

- [ ] **Step 6: Update candidate v0.3**

Extend `WA-WIN-INSTALL-001` with this observable requirement:

```text
Canonical Windows Burn installer uses repository-owned Russian WixStandardBootstrapperApplication localization as its default interactive presentation and one repository-owned graphical identity for bundle EXE/Installed Apps plus the standard installer logo; presentation resources do not change VERSION authority, WebAssistant.exe, technical service name WebAssistant, or stable WiX lifecycle identities.
```

Add these required paths to `contracts/webassistant-conformance-v0.3.json`:

```text
webassist/build/windows/installer/localization/ru-RU.wxl
webassist/build/windows/installer/branding/webassistant-icon.svg
webassist/build/windows/installer/branding/WebAssistant.IconGenerator/WebAssistant.IconGenerator.csproj
webassist/build/windows/installer/branding/WebAssistant.IconGenerator/IconGenerator.cs
webassist/build/windows/installer/branding/WebAssistant.IconGenerator/Program.cs
tests/core/WindowsInstallerPresentationContractTests.cs
tests/core/IconGeneratorTests.cs
```

Add exactly this vector:

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

```bash
git commit -m "#192: define Windows installer presentation contract"
```

---

### Task 2: Complete repository-owned Russian WixStdBA localization

**Files:**
- Create: `webassist/build/windows/installer/localization/ru-RU.wxl`
- Modify: `tests/core/WindowsInstallerPresentationContractTests.cs`

**Interfaces:**
- Consumes: exact localization IDs from pinned WiX 7.0.0 `HyperlinkTheme.wxl`.
- Produces: one explicit Russian `.wxl`; no machine-locale fallback.

- [ ] **Step 1: Add localization RED test**

Parse the `.wxl` as XML and require:

```text
Culture = ru-RU
Language = 1049
```

Require exactly these 66 IDs to exist once each:

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

Additionally assert:

```text
Caption contains [WixBundleName]
InstallMessage contains [WixBundleName]
InstallMessageOptions contains [WixBundleName]
OptionsPerUserScopeText contains [WixBundleName]
OptionsPerMachineScopeText contains [WixBundleName]
no String Value contains literal WebAssistant
all values except Title contain at least one Cyrillic letter or a documented command-line switch string
```

- [ ] **Step 2: Run localization RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter FullyQualifiedName~WindowsInstallerPresentationContractTests
```

Expected: missing `ru-RU.wxl` failure.

- [ ] **Step 3: Create `ru-RU.wxl` with this complete value map**

Use XML namespace `http://wixtoolset.org/schemas/v4/wxl`, `Culture="ru-RU"`, `Language="1049"` and these exact logical values; XML-escape ampersands as `&amp;` and hyperlink markup as required by WiX localization syntax:

```text
Caption = Установка [WixBundleName]
Title = [WixBundleName]
CheckingForUpdatesLabel = Проверка обновлений
UpdateButton = &Обновить до версии [WixStdBAUpdateAvailable]
InstallHeader = Установка
InstallMessage = Программа установки установит [WixBundleName] на этот компьютер. Нажмите «Установить» для продолжения или «Отмена» для выхода.
InstallMessageOptions = Программа установки установит [WixBundleName] на этот компьютер. Нажмите «Установить» для продолжения, «Параметры» для настройки или «Отмена» для выхода.
InstallVersion = Версия [WixBundleVersion]
ConfirmCancelMessage = Прервать текущую операцию?
ExecuteUpgradeRelatedBundleMessage = Предыдущая версия
HelpHeader = Справка установщика
HelpText = /install | /repair | /uninstall | /layout [каталог] — установить, восстановить, удалить или создать локальную копию установочных файлов. /passive | /quiet — минимальный интерфейс или выполнение без интерфейса. /norestart — не перезагружать компьютер автоматически. /log файл — записать журнал в указанный файл.
HelpCloseButton = &Закрыть
InstallLicenseLinkText = Условия лицензии [WixBundleName]: <a href="#">открыть</a>.
InstallAcceptCheckbox = Я &принимаю условия лицензии
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
ProgressHeader = Выполняется операция
ProgressLabel = Операция:
OverallProgressPackageText = Инициализация...
ProgressCancelButton = &Отмена
ModifyHeader = Обслуживание установки
ModifyRepairButton = &Восстановить
ModifyUninstallButton = &Удалить
ModifyCancelButton = &Отмена
SuccessHeader = Операция успешно завершена
SuccessCacheHeader = Кэширование успешно завершено
SuccessInstallHeader = Установка успешно завершена
SuccessLayoutHeader = Создание локальной копии успешно завершено
SuccessModifyHeader = Изменение установки успешно завершено
SuccessRepairHeader = Восстановление успешно завершено
SuccessUninstallHeader = Удаление успешно завершено
SuccessUnsafeUninstallHeader = Удаление успешно завершено
SuccessLaunchButton = &Запустить
SuccessRestartText = Для использования программы необходимо перезагрузить компьютер.
SuccessUninstallRestartText = Для завершения удаления необходимо перезагрузить компьютер.
SuccessRestartButton = &Перезагрузить
SuccessCloseButton = &Закрыть
FailureHeader = Операция не выполнена
FailureCacheHeader = Не удалось выполнить кэширование
FailureInstallHeader = Установка не выполнена
FailureLayoutHeader = Не удалось создать локальную копию
FailureModifyHeader = Не удалось изменить установку
FailureRepairHeader = Восстановление не выполнено
FailureUninstallHeader = Удаление не выполнено
FailureUnsafeUninstallHeader = Удаление не выполнено
FailureHyperlinkLogText = Во время выполнения операции возникла ошибка. Исправьте причину и повторите попытку. Дополнительные сведения доступны в <a href="#">журнале установки</a>.
FailureRestartText = Для завершения отката необходимо перезагрузить компьютер.
FailureRestartButton = &Перезагрузить
FailureCloseButton = &Закрыть
FilesInUseTitle = Используемые файлы
FilesInUseLabel = Следующие приложения используют файлы, которые необходимо обновить:
FilesInUseNetfxCloseRadioButton = Закрыть &приложения.
FilesInUseCloseRadioButton = Закрыть &приложения и попытаться запустить их снова.
FilesInUseDontCloseRadioButton = &Не закрывать приложения. Для завершения потребуется перезагрузка.
FilesInUseRetryButton = &Повторить
FilesInUseIgnoreButton = &Игнорировать
FilesInUseExitButton = &Выйти
```

- [ ] **Step 4: Run localization GREEN**

Run the same filtered test. Localization assertions must PASS; WiX wiring assertions remain RED until Task 4.

- [ ] **Step 5: Commit localization**

```bash
git commit -m "#192: add Russian WixStdBA localization"
```

---

### Task 3: Canonical SVG and deterministic icon generator

**Files:**
- Create: `webassist/build/windows/installer/branding/webassistant-icon.svg`
- Create: `webassist/build/windows/installer/branding/WebAssistant.IconGenerator/WebAssistant.IconGenerator.csproj`
- Create: `webassist/build/windows/installer/branding/WebAssistant.IconGenerator/IconGenerator.cs`
- Create: `webassist/build/windows/installer/branding/WebAssistant.IconGenerator/Program.cs`
- Create: `tests/core/IconGeneratorTests.cs`
- Modify: `tests/core/WebAssistant.CoreTests.csproj`

**Interfaces:**
- Consumes: SVG source path plus requested ICO/PNG output paths.
- Produces: `IconGenerator.Generate(string svgPath, string icoPath, string logoPath)` and CLI `--input`, `--ico`, `--logo`.

- [ ] **Step 1: Add test project reference**

```xml
<ProjectReference Include="../../webassist/build/windows/installer/branding/WebAssistant.IconGenerator/WebAssistant.IconGenerator.csproj" />
```

- [ ] **Step 2: Write renderer RED tests**

The tests must:

```text
generate twice from canonical SVG into two temp directories;
SHA-256(ico1) == SHA-256(ico2);
SHA-256(png1) == SHA-256(png2);
PNG IHDR width == 64 and height == 64;
ICO count == 4 and sizes == 16,32,48,256;
missing SVG throws and leaves no outputs;
malformed SVG throws and leaves no outputs.
```

ICO parser reads ICONDIR/ICONDIRENTRY directly and interprets width/height byte `0` as 256.

- [ ] **Step 3: Run renderer RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter FullyQualifiedName~IconGeneratorTests
```

Expected: generator source/project missing.

- [ ] **Step 4: Create the canonical SVG exactly from this initial geometry**

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

No text, fonts, external images, filters or remote resources.

- [ ] **Step 5: Create pinned generator project**

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

- [ ] **Step 6: Implement `IconGenerator.Generate`**

Public API:

```csharp
namespace WebAssistant.IconGenerator;

public static class IconGenerator
{
    public static void Generate(string svgPath, string icoPath, string logoPath);
}
```

Rules:

```text
validate non-empty paths and SVG existence;
load SVG through Svg.Skia;
reject empty picture or non-positive bounds;
render transparent 16,32,48,256 square frames with aspect-fit and antialiasing;
encode each frame as PNG;
write ICO in ascending size order using deterministic ICONDIR + ICONDIRENTRY + PNG payloads;
render separate 64x64 PNG logo from the same SVG;
write temporary sibling files first, validate, then move to final names;
on any exception remove all temporary/final partial outputs and rethrow.
```

ICO binary layout:

```text
ICONDIR: reserved=0 ushort, type=1 ushort, count=4 ushort
entry width: 16|32|48|0(=256)
entry height: 16|32|48|0(=256)
colorCount=0 byte
reserved=0 byte
planes=1 ushort
bitCount=32 ushort
bytesInRes=PNG length uint
imageOffset=6 + 4*16 + sum(previous PNG lengths) uint
```

Use `BinaryWriter` little-endian writes.

- [ ] **Step 7: Implement CLI**

Accepted syntax only:

```text
--input <svg>
--ico <ico-output>
--logo <png-output>
```

Unknown, missing or duplicate options return non-zero and print one concise stderr line. Success returns `0`.

- [ ] **Step 8: Run renderer GREEN**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter FullyQualifiedName~IconGeneratorTests
```

Expected: PASS on Linux core and Windows environment with platform-specific native assets.

- [ ] **Step 9: Commit generator**

```bash
git commit -m "#192: add reproducible WebAssistant icon generator"
```

---

### Task 4: WiX + canonical Windows producer wiring

**Files:**
- Modify: `webassist/build/windows/installer/Bundle.wxs`
- Modify: `webassist/build/windows/installer/WebAssistant.Bundle.wixproj`
- Modify: `webassist/build/windows/package.ps1`
- Modify: `tests/core/WindowsInstallerPresentationContractTests.cs`

**Interfaces:**
- Consumes: `ru-RU.wxl`, canonical SVG, generator CLI and #190 effective textual metadata.
- Produces: Burn bundle containing Russian localization, generated icon and generated 64x64 logo before SHA/provenance freeze.

- [ ] **Step 1: Add wiring RED assertions**

Require `package.ps1` to define and validate:

```text
$localizationPath
$iconSourcePath
$iconGeneratorProject
$bundleIconPath
$bundleLogoPath
```

Require it to run generator before bundle build and pass all three MSBuild properties:

```text
LocalizationFile
BundleIconPath
LogoFile
```

Generated outputs must be under `$stagingRoot`.

- [ ] **Step 2: Run wiring RED**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release --filter "FullyQualifiedName~WindowsInstallerPresentationContractTests|FullyQualifiedName~InstallerArtifactContractTests"
```

Expected: missing wiring failures.

- [ ] **Step 3: Modify `Bundle.wxs` to this relevant shape**

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

No other bundle-chain semantic change.

- [ ] **Step 4: Extend `WebAssistant.Bundle.wixproj` constants**

Append to `DefineConstants`:

```text
LocalizationFile=$(LocalizationFile);BundleIconPath=$(BundleIconPath);LogoFile=$(LogoFile)
```

Preserve all #190 constants.

- [ ] **Step 5: Wire generator in `package.ps1`**

Source inputs:

```powershell
$localizationPath = Join-Path $installerRoot "localization/ru-RU.wxl"
$iconSourcePath = Join-Path $installerRoot "branding/webassistant-icon.svg"
$iconGeneratorProject = Join-Path $installerRoot "branding/WebAssistant.IconGenerator/WebAssistant.IconGenerator.csproj"
```

All three go into required-path validation.

After `$stagingRoot` creation:

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

Bundle build receives:

```powershell
"-p:LocalizationFile=$localizationPath" `
"-p:BundleIconPath=$bundleIconPath" `
"-p:LogoFile=$bundleLogoPath"
```

No presentation write occurs after `Copy-Item` creates `$artifactPath`.

- [ ] **Step 6: Run full functional GREEN while VERSION is unchanged**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
```

Expected: all core tests PASS at baseline VERSION.

- [ ] **Step 7: Run one Windows canonical producer checkpoint**

```powershell
webassist\build\windows\package.bat
```

Expected baseline-version EXE + `.sha256` + `.provenance.json`. This proves buildability only; it is not final acceptance.

- [ ] **Step 8: Commit functional GREEN**

```bash
git commit -m "#192: integrate Russian installer presentation"
```

Record exact functional-GREEN SHA and evidence in PR.

---

### Task 5: Documentation and the one VERSION transition

**Files:**
- Modify: `webassist/README.md`
- Modify: `webassist/VERSION`

**Interfaces:**
- Consumes: functional GREEN implementation.
- Produces: factual docs and exactly one monotonic VERSION transition.

- [ ] **Step 1: Document presentation authority**

Document all of these facts:

```text
ru-RU.wxl is canonical default interactive Windows installer localization;
webassistant-icon.svg is the only hand-authored graphic authority;
ICO sizes 16/32/48/256 and WixStdBA PNG 64x64 are generated during canonical packaging;
no machine-local graphics editor/converter is required;
product-metadata.json controls textual identity only;
WebAssistant.exe, service name and WiX lifecycle IDs remain technical stable identities.
```

- [ ] **Step 2: Re-run full core on docs head**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --configuration Release
```

Expected: PASS.

- [ ] **Step 3: Resolve next VERSION against current base**

If base remains `0.3.27`, next is exactly `0.3.28`. If base changed, next is the next patch over the actual current base.

- [ ] **Step 4: Change `webassist/VERSION` exactly once**

For unchanged baseline:

```text
0.3.27 -> 0.3.28
```

Commit:

```bash
git commit -m "#192: bump version to 0.3.28"
```

No second VERSION bump in this transaction.

- [ ] **Step 5: Verify core + repo-guard on VERSION head**

Both must PASS. Any defect after this point is fixed on the same VERSION.

---

### Task 6: Final exact-head Ready CI and lifecycle acceptance

**Files:**
- No planned source changes.

**Interfaces:**
- Consumes: final source head after the single VERSION bump.
- Produces: immutable exact-artifact evidence.

- [ ] **Step 1: Mark PR Ready only after core and repo-guard GREEN**

Do not change source head during heavy Ready CI.

- [ ] **Step 2: Require exact-head SUCCESS for**

```text
core / test
repo-guard
Windows Service acceptance / Build Windows installer artifact once
Windows Service acceptance / Windows WiX installer lifecycle
scanner final acceptance / Windows TWAIN direct SDK
scanner final acceptance / Linux SANE direct SDK when repository classifier requires it
ci-fast
ci-required
```

Windows lifecycle must consume exact producer bytes and reproduce historical `v0.3.21 -> final VERSION` live-worker upgrade.

- [ ] **Step 3: Verify exact artifact evidence**

Require:

```text
<effective basename>-win-x64-<final VERSION>.exe
<same>.sha256
<same>.provenance.json
```

Recorded hash must equal EXE bytes; provenance source SHA must equal exact producer source head.

- [ ] **Step 4: Verify technical identity invariants**

```text
WebAssistant.exe
Windows Service = WebAssistant
Bundle Id = netkeep80.WebAssistant.Bundle
Package Id = netkeep80.WebAssistant
UpgradePreflight order/tokens
```

- [ ] **Step 5: Record exact final evidence in PR**

Record head SHA, VERSION, core pass count, repo-guard pass count, Ready run ID, producer job, lifecycle job, historical upgrade result, scanner acceptance jobs and `ci-required` job.

---

### Task 7: Real Windows visual evidence, merge and roadmap handoff

**Files:**
- No production source change expected.
- Screenshots are issue/PR evidence only, never build inputs.

**Interfaces:**
- Consumes: exact final accepted installer bytes from Task 6.
- Produces: human visual acceptance and completed #192 transaction.

- [ ] **Step 1: Capture exact-artifact Windows screenshots**

Use the CI-accepted EXE without local rebuild and capture:

```text
initial installer UI in Russian;
maintenance/repair/uninstall UI in Russian;
installer EXE icon in Explorer;
Installed Apps / Programs and Features icon;
representative downgrade/failure result with understandable Russian user-facing text.
```

- [ ] **Step 2: Human icon review**

Confirm at 16/32/48/256 and WixStdBA 64x64:

```text
scanner body remains recognizable;
paper/file folded-corner cue remains visible;
no text is required;
logo is not cropped or blurred;
Explorer and ARP use the same identity.
```

A requested SVG visual change is a source change: rerun generator/core and full exact-head Ready CI on the same VERSION; never patch accepted EXE bytes.

- [ ] **Step 3: Attach screenshots with exact VERSION and SHA evidence to #192/PR**

- [ ] **Step 4: Final diff audit**

Changed files must not include:

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

- [ ] **Step 5: Merge using expected final head SHA**

Merge only while PR is mergeable and exact head equals the fully accepted head.

- [ ] **Step 6: Post-merge verify**

Re-fetch `main`, VERSION and #192 closed state; record post-merge CI if push workflows run.

- [ ] **Step 7: Update #160 and #154**

Record #192 DONE plus new `main`/VERSION; continue only with the already authorized roadmap order.

---

## Plan Self-Review

### Spec coverage

- Russian install/upgrade/maintenance/failure UI: Tasks 2, 4, 7.
- Repository-owned scanner+file SVG: Task 3.
- Deterministic ICO 16/32/48/256 + PNG 64x64: Task 3.
- Bundle EXE + ARP icon and standard WixStdBA logo: Task 4 + Task 7 evidence.
- #191 stable upgrade lifecycle: Tasks 4 and 6.
- #190 VERSION/filename/provenance invariants: Tasks 4-6.
- Candidate v0.3 updated, accepted v0.2 immutable: Task 1.
- Exactly one VERSION transition after functional GREEN: Task 5.
- Final lifecycle evidence after bump: Task 6.
- Real Windows screenshot evidence: Task 7.

### Placeholder scan

No `TBD`, `TODO`, `implement later`, unspecified localization strings, floating dependency versions, undefined image dimensions or undefined final gates remain.

### Type/interface consistency

- Generator API: `IconGenerator.Generate(string svgPath, string icoPath, string logoPath)`.
- CLI: `--input`, `--ico`, `--logo`.
- Generated names: `webassistant-icon.ico`, `webassistant-logo.png` under isolated staging.
- WiX properties: `LocalizationFile`, `BundleIconPath`, `LogoFile`.
- PNG: 64x64. ICO: 16/32/48/256.
