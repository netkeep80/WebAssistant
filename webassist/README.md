# WebAssistant

`webassist` — автономный корень продукта WebAssistant. Его содержимое можно копировать в корень отдельного репозитория и собирать, упаковывать и устанавливать без файлов уровнем выше.

## Что работает сейчас

WebAssistant устанавливается как общая системная служба рабочей станции:
- Windows — Windows Service `WebAssistant`;
- ALT Linux — systemd service `webassist.service`.

Служба слушает только loopback. Default endpoint: `http://127.0.0.1:17654`.

Текущий scanner module:
- перечисляет доступные сканеры;
- позволяет явно выбрать `scannerId`;
- поддерживает glass, feeder и duplex operations;
- выполняет не более одного physical acquisition одновременно;
- возвращает успешный результат как raw `application/pdf`.

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

`Cors.Enabled` по умолчанию `false`. При включении разрешены только явно заданные HTTP/HTTPS origins; `*` не допускается.

`FileSystem.RootDirectory` задаёт границу для будущих filesystem capabilities. В текущей версии browser-facing filesystem endpoints отсутствуют. Внутренний path resolver принимает только относительные пути внутри root и отвергает navigation segments, absolute paths и существующие symlink/reparse-point components.

## Сборка пакета

Требуется .NET SDK 10. Packaging scripts определяют собственное расположение и не зависят от текущего рабочего каталога.

Linux:

```bash
./build/linux/package.sh
```

Windows:

```bat
build\windows\package.bat
```

По умолчанию пакеты создаются в `artifacts/` внутри product root.

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

Исправленный SDK хранится под отдельной identity `WebAssistant.NAPS2.Sdk` в `vendor/nuget`. Сборка продукта не маскирует его под официальный `NAPS2.Sdk` той же версии. Provenance и способ воспроизводимой пересборки описаны в `vendor/naps2/README.md`.

Temporary policy probe: repo-policy.json
