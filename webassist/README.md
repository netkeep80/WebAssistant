# WebAssistant

`webassist` — автономный корень продукта WebAssistant. Его содержимое можно копировать в корень отдельного репозитория и собирать, упаковывать и устанавливать без файлов уровнем выше.

## Версия продукта

Файл `VERSION` в корне продукта — единственный persisted source of truth для product version. Он содержит numeric SemVer `major.minor.patch` и переносится вместе с каталогом продукта без зависимости от `.git`, tags или CI metadata.

Сборка использует это значение для assembly/product metadata. Диагностический `/v1/diag/info` возвращает ту же версию через assembly metadata. Linux и Windows packaging scripts читают `VERSION`, передают значение в build и копируют `VERSION` в корень готового package.

Повторная сборка одного и того же product snapshot поэтому сохраняет ту же product version.

## Что работает сейчас

WebAssistant устанавливается как общая системная служба рабочей станции:
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

Scanner backend зависит только от платформы рабочей станции. На Windows сначала используется WIA; переход на TWAIN происходит только если WIA не вернул ни одного устройства. На Linux используется direct SANE SDK path через NAPS2, без CLI orchestration.

Scanner operation ограничена acquisition → PDF. Она сама не выполняет edit/merge/split PDF, OCR/annotation/watermark/deskew или другую semantic document transformation, signing/encryption, business/backend upload и не требует business authentication или per-user business profile. Это граница scanner capability, а не глобальный запрет на независимые capabilities WebAssistant.

Описание REST API: [`docs/api.md`](docs/api.md).

## Runtime configuration

Пример находится в `src/WebAssistant/appsettings.json`:

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
    }
  }
}
```

`Cors.Enabled` по умолчанию `false`. При включении разрешены только явно заданные exact HTTP/HTTPS origins; `*` не допускается.

`FileSystem.RootDirectory` задаёт rooted filesystem boundary. В текущей версии browser-facing filesystem endpoints отсутствуют. Внутренний path resolver принимает только относительные пути внутри root и отвергает navigation segments, absolute paths и существующие symlink/reparse-point components.

WebAssistant пишет собственные технические события в суточные log files. В журналы не записываются PDF bytes, Base64 и содержимое страниц/документов. Current service сам не выполняет automatic retention/delete старых daily logs.

## Сборка пакета

Требуется .NET SDK 10. Packaging scripts определяют собственное расположение и не зависят от текущего рабочего каталога. Оба canonical entrypoint запускаются без обязательных аргументов.

Linux:

```bash
./build/linux/package.sh
```

Linux packaging не предполагает наличие пакета `dotnet-sdk-10.0` в системном package manager. SDK 10 разрешается в следующем порядке:

1. явный локальный SDK из `WEBASSISTANT_DOTNET_ROOT`;
2. локальный offline toolchain в `toolchain/dotnet/linux-x64/`, если он подготовлен рядом с product root;
3. уже установленный system .NET SDK 10;
4. если SDK 10 всё ещё не найден — автоматический официальный online bootstrap через `dotnet-install.sh`.

То есть обычный запуск:

```bash
./build/linux/package.sh
```

сам скачивает .NET SDK 10 в пользовательский cache, если подходящего SDK нет и доступна сеть. `apt-get`/`sudo` для получения .NET SDK не используются.

Наличие подходящего system SDK можно проверить заранее:

```bash
dotnet --list-sdks
```

Пример использования заранее подготовленного локального SDK:

```bash
WEBASSISTANT_DOTNET_ROOT=/opt/dotnet ./build/linux/package.sh
```

Если сборка должна быть строго без сетевого bootstrap, его можно явно отключить:

```bash
WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP=0 ./build/linux/package.sh
```

В этом режиме отсутствие local/bundled/system SDK 10 приводит к fail-closed без обращения к сети или package manager.

Каталог для автоматически скачиваемого SDK можно задать отдельно:

```bash
WEBASSISTANT_DOTNET_INSTALL_DIR=/opt/dotnet ./build/linux/package.sh
```

Наличие локального SDK само по себе ещё не означает полноценную clean offline build: для сборки с пустыми machine caches без сети также нужен полный локальный NuGet dependency closure с проверяемой целостностью. Такой offline bundle является отдельным build-environment артефактом, а не содержимым Git-репозитория.

Linux package публикуется как self-contained `linux-x64`, поэтому машине, на которой уже готовый package только устанавливается и запускается, .NET SDK не требуется.

Windows:

```bat
build\windows\package.bat
```

По умолчанию пакеты создаются в `artifacts/` внутри product root. При необходимости scripts принимают явный output path, но он не является обязательным для обычной сборки.

## GitLab CI

В корне продукта находится самостоятельный `.gitlab-ci.yml`. Он использует те же canonical package entrypoints, что и ручная сборка, и не требует внешних include-файлов.

Windows job требует переменную проекта/группы:

```text
WEBASSISTANT_WINDOWS_RUNNER_TAG
```

Её значение должно совпадать с tag доступного Windows runner. Сам tag в публичной конфигурации не фиксируется.

Package jobs выполняют:

```text
Windows: build\windows\package.bat artifacts\windows-x64
Linux:   build/linux/package.sh artifacts/linux-x64
```

Результаты публикуются как GitLab artifacts из `artifacts/windows-x64/` и `artifacts/linux-x64/`.

## Установка

После создания package запускайте installer из package directory с административными правами.

Linux:

```bash
sudo ./install.sh
```

Windows:

```bat
install.bat
```

Подробности lifecycle и расположения файлов:
- [`docs/linux-service.md`](docs/linux-service.md)
- [`docs/windows-service.md`](docs/windows-service.md)

## Зависимость NAPS2 SDK

Исправленный SDK хранится под отдельной identity `WebAssistant.NAPS2.Sdk` в `vendor/nuget`. Сборка продукта не маскирует его под официальный `NAPS2.Sdk` той же версии. Provenance, фиксированная package identity и способ воспроизводимой пересборки описаны в `vendor/naps2/README.md`.
