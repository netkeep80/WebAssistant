# WebAssistant как Windows Service

## Канонический артефакт

Дистрибутив Windows собирается только через канонический сценарий:

```bat
build\windows\package.bat
```

Он создаёт пользовательский установочный артефакт:

```text
<installerBaseName>-win-x64-<VERSION>.exe
```

где `<VERSION>` равен точному содержимому файла `VERSION`, а `<installerBaseName>` берётся из фактических метаданных продукта. Публичное значение `installerBaseName` по умолчанию равно `WebAssistant`, поэтому без переопределения имя имеет вид:

```text
WebAssistant-win-x64-<VERSION>.exe
```

Рядом создаются `.sha256` и `.provenance.json` для итогового EXE.

Сценарий сначала собирает самодостаточный payload приложения `win-x64`, затем внутренний MSI и итоговый WiX 7 Burn bundle. Внутренний MSI — промежуточный результат сборки; устанавливать его вручную не требуется. Отображаемые метаданные, задаваемые при сборке, не меняют стабильные технические идентификаторы: исполняемый файл `WebAssistant.exe` и Windows Service `WebAssistant` остаются прежними.

## Persistent-конфигурация Windows

Рабочая конфигурация Windows находится вне installation root:

```text
%ProgramData%\WebAssistant\config\appsettings.json
```

Во время упаковки по-прежнему выбирается начальный seed:

```text
src/WebAssistant/appsettings.json существует
  -> используются точные байты этого файла

файл отсутствует
  -> используется build/common/default-appsettings.json
```

В migration release seed остаётся рядом с Program Files только как переходный bootstrap source. При upgrade установки до 0.3.68 включительно Burn `UpgradePreflight` получает legacy install root, завершает service/worker lifecycle и затем копирует legacy `appsettings*.json` в ProgramData **до запуска MSI**. Если persistent config уже существует, он не заменяется; отличающиеся legacy bytes сохраняются отдельным backup. На clean install runtime bootstrap использует package seed только при отсутствии persistent config. Поэтому install, upgrade, repair и reinstall существующий `%ProgramData%\WebAssistant\config\appsettings.json` не изменяют.

Отдельный файл:

```text
%ProgramData%\WebAssistant\config\appsettings.example.json
```

является installer-owned примером всей текущей configuration surface. Installer принудительно заменяет его свежей package-версией. Он не используется runtime как `appsettings.{Environment}.json`, даже при environment `example`.

Безопасный default seed содержит пустую карту `WebAssistant:FileSystem`; полный пример находится в `appsettings.example.json`. Полная структура и приоритет источников описаны в [`configuration.md`](configuration.md).

`WebAssistant:FileSystem:RootDirectory` не имеет специальной legacy-семантики. Если этот ключ присутствует с допустимым абсолютным путём, текущая multi-root модель трактует его как **обычный логический корень** с именем `RootDirectory`; `%ProgramData%\WebAssistant\data` сам по себе файловым root не становится.
## Установка

Запустите установщик от имени пользователя, который может подтвердить повышение прав UAC:

```text
<installerBaseName>-win-x64-<VERSION>.exe
```

При публичном значении по умолчанию это `WebAssistant-win-x64-<VERSION>.exe`.

Канонический установщик:

- запрашивает повышение прав;
- выполняет общесистемную установку в Program Files;
- устанавливает самодостаточное приложение, поэтому целевая рабочая станция не требует заранее установленного .NET Runtime, SDK, NuGet или инструментов сборки;
- регистрирует Windows Service `WebAssistant`;
- настраивает автоматический запуск службы;
- запускает службу после установки;
- регистрирует фактическую отображаемую идентичность продукта в Installed Apps / Programs and Features;
- записывает `DisplayVersion`, совпадающий с `VERSION` продукта.

После запуска служба использует persistent `%ProgramData%\WebAssistant\config\appsettings.json`; Program Files не является runtime authority конфигурации.

## Проверка

Состояние службы можно проверить стандартными средствами Windows, например PowerShell:

```powershell
Get-Service -Name WebAssistant
```

Точка проверки состояния доступна только через loopback:

```text
http://127.0.0.1:17654/v1/health
```

Пример проверки:

```powershell
Invoke-WebRequest http://127.0.0.1:17654/v1/health
```

В Installed Apps / Programs and Features должно присутствовать фактическое отображаемое имя приложения с ожидаемым `DisplayVersion`.

## Состояние времени выполнения

Общесистемное состояние расположено отдельно от Program Files:

```text
%ProgramData%\WebAssistant\config
%ProgramData%\WebAssistant\logs
%ProgramData%\WebAssistant\data
```

`%ProgramData%\WebAssistant\data` остаётся каталогом состояния жизненного цикла службы и сохраняется установщиком для совместимости. Он не является скрытым или автоматическим файловым корнем. Области полномочий файлового обмена определяются только явно настроенными элементами `WebAssistant:FileSystem`; браузерный API не может менять их физические пути.

Служба слушает только `127.0.0.1`; порт по умолчанию — `17654`.

## Удаление

Используйте стандартное удаление фактического продукта WebAssistant через Installed Apps / Programs and Features либо штатное удаление канонического bundle.

Удаление останавливает и удаляет Windows Service, регистрацию продукта и installation payload. `%ProgramData%\WebAssistant\config`, `logs` и `data` сохраняются. Поэтому последующая чистая установка автоматически использует прежний `appsettings.json`. Удаление конфигурации допускается только отдельным явным purge-действием; исторический `uninstall.ps1` предоставляет для этого `-PurgeConfig`.

Исторические сценарии репозитория `install.bat` / `install.ps1` не являются каноническим пользовательским способом установки версионированного дистрибутива Windows.
