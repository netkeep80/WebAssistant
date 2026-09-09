# WebAssistant как Windows Service

## Canonical artifact

Windows distribution собирается только через canonical producer:

```bat
build\windows\package.bat
```

Он создаёт пользовательский installation artifact:

```text
WebAssistant-win-x64-<VERSION>.exe
```

где `<VERSION>` равен exact content файла `VERSION`. Рядом создаются `.sha256` и `.provenance.json` для final EXE.

Producer сначала собирает self-contained `win-x64` application payload, затем внутренний MSI и финальный WiX 7 Burn bundle. Внутренний MSI — build intermediate; устанавливать вручную его не требуется.

## Package-owned configuration

Configuration выбирается package-time:

```text
src/WebAssistant/appsettings.json существует
  -> в payload попадают exact bytes этого файла

файл отсутствует
  -> в payload копируется build/common/default-appsettings.json
```

После формирования EXE `appsettings.json` принадлежит package. Installer не создаёт default configuration, не заменяет packaged config и не патчит его во время install/reinstall.

Публичный product root поэтому может не содержать environment-specific `src/WebAssistant/appsettings.json`: canonical producer в таком случае использует безопасный repository default.

## Установка

Запустите от имени пользователя, который может подтвердить UAC elevation:

```text
WebAssistant-win-x64-<VERSION>.exe
```

Canonical installer:

- запрашивает elevation;
- выполняет machine-wide installation в Program Files;
- устанавливает self-contained application, поэтому target workstation не требует заранее установленного .NET Runtime/SDK/NuGet/build tools;
- регистрирует Windows Service `WebAssistant`;
- задаёт automatic service start;
- запускает service после установки;
- регистрирует WebAssistant в Installed Apps / Programs and Features;
- записывает DisplayVersion, совпадающий с product `VERSION`.

Installed application использует package-owned `appsettings.json` без installer-side regeneration.

## Проверка

Состояние службы можно проверить стандартными Windows средствами, например PowerShell:

```powershell
Get-Service -Name WebAssistant
```

Health endpoint доступен только через loopback:

```text
http://127.0.0.1:17654/v1/health
```

Пример проверки:

```powershell
Invoke-WebRequest http://127.0.0.1:17654/v1/health
```

В Installed Apps / Programs and Features должен присутствовать `WebAssistant` с ожидаемым DisplayVersion.

## Runtime state

Machine-wide state расположен отдельно от Program Files:

```text
%ProgramData%\WebAssistant\logs
%ProgramData%\WebAssistant\data
```

Служба слушает только `127.0.0.1`; default port — `17654`.

## Удаление

Используйте стандартное удаление WebAssistant через Installed Apps / Programs and Features либо штатный uninstall canonical bundle.

Uninstall останавливает и удаляет Windows Service, product registration и установленные application files. `%ProgramData%\WebAssistant\logs` и `%ProgramData%\WebAssistant\data` по текущей state policy сохраняются.

Исторические repository scripts `install.bat` / `install.ps1` не являются canonical end-user installation path для versioned Windows distribution artifact.
