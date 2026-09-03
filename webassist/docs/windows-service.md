# WebAssistant как Windows Service

Windows package собирается через `webassist/build/windows/package.bat [output-directory]` или `package.ps1`. BAT entrypoint работает независимо от текущего каталога и при необходимости устанавливает .NET SDK 10 через `winget` перед сборкой.

Пакет содержит self-contained `win-x64` приложение и install/uninstall entrypoints. `install.ps1` размещает приложение в `%ProgramFiles%\WebAssistant` по умолчанию, создаёт Windows Service `WebAssistant` с автоматическим запуском и записывает JSON-конфигурацию.

Журналы сохраняются в `%ProgramData%\WebAssistant\logs`, data root — `%ProgramData%\WebAssistant\data`. Служба слушает только `127.0.0.1`; порт по умолчанию `17654`.

`uninstall.ps1` останавливает и удаляет службу и установленное приложение. Данные и журналы сохраняются, если явно не передан `-PurgeData`.
