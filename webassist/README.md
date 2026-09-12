# WebAssistant

`webassist` — автономный корень продукта WebAssistant. Его содержимое можно скопировать в корень отдельного репозитория и собирать, упаковывать и устанавливать без файлов уровнем выше.

## Версия продукта

Файл `VERSION` в корне продукта — единственный persisted source of truth для product version. Он содержит numeric SemVer `major.minor.patch` и переносится вместе с продуктом без зависимости от `.git`, tags или CI metadata.

Canonical package producers читают это значение и формируют versioned artifacts по effective installer basename:

```text
Windows: <installerBaseName>-win-x64-<VERSION>.exe
Linux:   <installerBaseName>-linux-x64-<VERSION>.zip
```

При отсутствии product metadata override public default `installerBaseName` равен `WebAssistant`, поэтому обычная GitHub/manual сборка по-прежнему даёт:

```text
Windows: WebAssistant-win-x64-<VERSION>.exe
Linux:   WebAssistant-linux-x64-<VERSION>.zip
```

`<VERSION>` всегда означает exact content файла `VERSION`. То же значение передаётся в application/package metadata и записывается в provenance. Product metadata не может задавать или переопределять version.

## Build-time product metadata

Windows и Linux используют один repository-owned `ProductMetadataResolver` и одну schema `webassistant-product-metadata/v1`. Сначала берутся public defaults из `build/common/product-metadata.defaults.json`, затем при наличии применяется optional override:

```text
src/WebAssistant/product-metadata.json
```

Файл находится рядом с runtime `src/WebAssistant/appsettings.json`, но имеет другую семантику: `appsettings.json` управляет runtime configuration, а `product-metadata.json` — только build-time product/display identity.

Допустимые поля override:

```json
{
  "schema": "webassistant-product-metadata/v1",
  "applicationName": "Custom Web Assistant",
  "installerBaseName": "CustomWebAssistant",
  "fileDescription": "Custom Web Assistant",
  "companyName": "Example Vendor",
  "copyright": "Copyright © Example Vendor"
}
```

Все поля кроме `schema` опциональны и наследуют public defaults. Неизвестные поля, другая schema, пустые/некорректные значения и непереносимый `installerBaseName` приводят к fail-closed build до создания canonical artifact. Поля `applicationName`, `fileDescription` и `companyName` дополнительно не могут содержать `;`, потому что эти значения передаются в WiX через semicolon-delimited `DefineConstants`; resolver отклоняет такой input до publish/package. Поля `version`, service name, executable name, install paths и WiX lifecycle identifiers в metadata contract отсутствуют намеренно.

Public GitHub repository специально игнорирует `webassist/src/WebAssistant/product-metadata.json` через development-root `.gitignore`. При copy-export содержимого `webassist` это правило не переносится: product-local `.gitignore` не запрещает этот path. Поэтому downstream GitLab repository может track-ить собственный `src/WebAssistant/product-metadata.json` и использовать другую product identity без изменения producer scripts. Различаются данные, а не packaging code.

Effective metadata применяются к .NET file/product metadata, Windows WiX/ARP display identity, Linux human-visible service description и basename canonical artifacts. Технические identifiers остаются стабильными: `WebAssistant.exe`, Windows Service `WebAssistant`, Linux unit `webassist.service`, install paths и WiX package/bundle lifecycle identities не переименовываются через branding override.

Provenance final artifact фиксирует `metadataMode` (`defaults` или `override`), effective `applicationName`, `installerBaseName`, `metadataInputSha256` и `effectiveMetadataSha256`. Metadata разрешаются до publish/package/checksum; post-build branding patching не используется.

## Что работает сейчас

WebAssistant устанавливается как machine-wide системная служба рабочей станции:

- Windows — Windows Service `WebAssistant`;
- ALT Linux — systemd service `webassist.service`.

Служба слушает только loopback. Default endpoint: `http://127.0.0.1:17654`. Публикация listener на LAN или `0.0.0.0` не является допустимой runtime configuration.

Текущий scanner module:

- перечисляет доступные сканеры;
- позволяет явно выбрать `scannerId`;
- поддерживает glass, feeder и duplex operations без скрытого fallback на другой source;
- выполняет не более одного physical acquisition одновременно;
- возвращает один raw `application/pdf`, содержащий все страницы acquisition;
- не использует Base64, JSON document envelope или ZIP/raster envelope как scanner document transport;
- после передачи PDF вызывающей стороне не хранит завершённый scan document как long-term scanner storage.

На Windows сначала используется WIA; переход на TWAIN происходит только если WIA не вернул ни одного устройства. На Linux используется direct SANE SDK path через NAPS2, без CLI orchestration.

Scanner operation ограничена acquisition → PDF. Она сама не выполняет edit/merge/split PDF, OCR/annotation/watermark/deskew или другую semantic document transformation, signing/encryption, business/backend upload и не требует business authentication или per-user business profile.

Описание REST API: [`docs/api.md`](docs/api.md).

## Runtime configuration и package-time ownership

Deployment configuration выбирается **package-time** одним из двух способов:

```text
src/WebAssistant/appsettings.json существует
  -> producer включает exact bytes этого файла

src/WebAssistant/appsettings.json отсутствует
  -> producer включает build/common/default-appsettings.json
```

После формирования canonical artifact `appsettings.json` является package-owned payload. Installer не генерирует, не заменяет и не патчит packaged configuration.

Repository default configuration выключает CORS и использует platform defaults для log/data roots. При включении CORS разрешены только явно заданные exact HTTP/HTTPS origins; wildcard `*` не допускается.

`FileSystem.RootDirectory` задаёт rooted filesystem boundary. В текущей версии browser-facing filesystem endpoints отсутствуют. Внутренний path resolver принимает только относительные пути внутри root и отвергает navigation segments, absolute paths и существующие symlink/reparse-point components.

WebAssistant пишет собственные технические события в суточные log files. В журналы не записываются PDF bytes, Base64 и содержимое страниц/документов. Current service сам не выполняет automatic retention/delete старых daily logs.

## Сборка canonical artifacts

Packaging scripts определяют product root относительно собственного расположения и не зависят от текущего рабочего каталога. Оба producer-а используют один effective product metadata contract, описанный выше.

### Windows

```bat
build\windows\package.bat
```

Результат по умолчанию находится в `artifacts/windows-x64/`:

```text
<installerBaseName>-win-x64-<VERSION>.exe
<installerBaseName>-win-x64-<VERSION>.exe.sha256
<installerBaseName>-win-x64-<VERSION>.exe.provenance.json
```

При public defaults `<installerBaseName>` равен `WebAssistant`. Producer собирает self-contained `win-x64` payload, внутренний MSI и финальный WiX 7 Burn EXE. Внутренний MSI является build intermediate; пользовательским installation artifact является только versioned EXE.

Интерактивный Windows installer по умолчанию использует repository-owned русскую локализацию `build/windows/installer/localization/ru-RU.wxl`. Единственный hand-authored источник графической identity — `build/windows/installer/branding/webassistant-icon.svg`. Во время canonical packaging repository-owned generator воспроизводимо строит из него ICO с кадрами 16×16, 32×32, 48×48 и 256×256 для bundle EXE / Installed Apps, а также PNG 64×64 для WixStandardBootstrapperApplication. Локальный графический редактор или внешний machine-local converter для сборки не требуется. `product-metadata.json` управляет только текстовой product/display identity; `WebAssistant.exe`, Windows Service `WebAssistant`, `netkeep80.WebAssistant.Bundle` и `netkeep80.WebAssistant` остаются стабильными technical lifecycle identities.

Для build machine требуется .NET SDK 10 и WiX toolchain, управляемый repository-owned installer projects. Target workstation заранее установленный .NET Runtime/SDK не требуется.

### Linux

```bash
./build/linux/package.sh
```

Результат по умолчанию находится в `artifacts/linux-x64/`:

```text
<installerBaseName>-linux-x64-<VERSION>.zip
<installerBaseName>-linux-x64-<VERSION>.zip.sha256
<installerBaseName>-linux-x64-<VERSION>.zip.provenance.json
```

При public defaults `<installerBaseName>` равен `WebAssistant`. Linux producer публикует self-contained `linux-x64` application и кладёт в ZIP `VERSION`, `install.sh`, `uninstall.sh`, `webassist.service` и package-owned `appsettings.json`.

Для build machine требуется .NET SDK 10. `package.sh` ищет его в следующем порядке:

1. `WEBASSISTANT_DOTNET_ROOT`;
2. `toolchain/dotnet/linux-x64/`;
3. system .NET SDK 10;
4. при разрешённом network bootstrap — официальный `dotnet-install.sh`.

Строго сетевой bootstrap можно запретить:

```bash
WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP=0 ./build/linux/package.sh
```

Это не является полной clean offline build guarantee: NuGet dependency closure для полностью offline build environment остаётся отдельной задачей.

## Установка

### Windows

Администратор запускает canonical artifact:

```text
<installerBaseName>-win-x64-<VERSION>.exe
```

Для public defaults это `WebAssistant-win-x64-<VERSION>.exe`. Installer запрашивает elevation, выполняет machine-wide установку в Program Files, регистрирует и автоматически запускает Windows Service `WebAssistant`, а также регистрирует effective product display identity в Installed Apps / Programs and Features. Подробности: [`docs/windows-service.md`](docs/windows-service.md).

### ALT Linux 10.1

Администратор распаковывает:

```text
<installerBaseName>-linux-x64-<VERSION>.zip
```

Для public defaults это `WebAssistant-linux-x64-<VERSION>.zip`. Из распакованного каталога запускается:

```bash
sudo ./install.sh
```

Installer использует packaged configuration без замены. Отсутствующие distro-owned runtime dependencies могут устанавливаться штатно через apt-rpm. Подробности: [`docs/linux-service.md`](docs/linux-service.md).

## Каноническая PDF-инструкция

Редактируемый источник пользовательской инструкции находится внутри автономного product root:

```text
docs/installation-guide.md
```

Post-freeze machine evidence передаётся отдельно через строгий manifest `docs/installation-guide/evidence.schema.json`. Manifest связывает каждую процедуру и screenshot с exact `sourceSha`, `VERSION`, именами installer artifacts и их SHA-256. Fixture evidence предназначен только для проверки механики и не может использоваться как final evidence.

Финальная инструкция строится только для exact frozen source SHA:

```bash
WEBASSISTANT_SOURCE_SHA=<exact-frozen-main-sha> \
  docs/installation-guide/build.sh \
  --mode final \
  --evidence <final-evidence.json> \
  --output artifacts/installation-guide
```

Результат:

```text
artifacts/installation-guide/WebAssistant-Installation-Guide.pdf
```

`final` mode fail-closed требует complete evidence именно для ALT Linux 10.1 и всех обязательных Windows/ALT capture slots. `verify.sh` повторно проверяет evidence identity, структуру и текст PDF и рендерит каждую страницу в raster image через pinned Poppler toolchain. Editable source и build/verify infrastructure являются repository authority; реальные post-freeze screenshots и generated PDF в repository не коммитятся.

Pinned document toolchain описан в `docs/installation-guide/toolchain.env` и не использует mutable `latest` identity.

## GitLab CI

В export root находится самостоятельный `.gitlab-ci.yml`. Текущая product-local GitLab surface содержит только Linux package orchestration и вызывает тот же canonical entrypoint:

```bash
./build/linux/package.sh artifacts/linux-x64
```

Downstream GitLab может commit-ить собственный `src/WebAssistant/product-metadata.json`: export-root `.gitignore` этот path не запрещает. Producer автоматически применит этот override через тот же `ProductMetadataResolver`; отдельная GitLab-specific branding/package implementation не требуется.

Файл `.gitlab-ci.yml` не определяет отдельную product packaging implementation. Конкретные runner/container/registry/network параметры будущей ALT Linux 10.1 build infrastructure должны задаваться downstream infrastructure только после их фактического определения; наличие `.gitlab-ci.yml` само по себе не является доказательством ALT Linux 10.1 target acceptance.

Windows distribution в GitLab не является частью текущей target architecture.

## Зависимость NAPS2 SDK

Исправленный SDK хранится под отдельной identity `WebAssistant.NAPS2.Sdk` в `vendor/nuget`. Сборка продукта не маскирует его под официальный `NAPS2.Sdk` той же версии. Provenance, fixed package identity и способ воспроизводимой пересборки описаны в `vendor/naps2/README.md`.