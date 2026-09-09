# Установка WebAssistant

Эта инструкция относится к WebAssistant **{{VERSION}}**, собранному из source commit `{{SOURCE_SHA}}`.

Она описывает установку двух canonical distribution artifacts:

- Windows: `{{WINDOWS_FILENAME}}`;
- ALT Linux 10.1: `{{LINUX_FILENAME}}`.

## Проверка полученных файлов

Перед установкой убедитесь, что имя файла и SHA-256 совпадают с release metadata.

| Платформа | Файл | SHA-256 |
| --- | --- | --- |
| Windows | `{{WINDOWS_FILENAME}}` | `{{WINDOWS_SHA256}}` |
| ALT Linux 10.1 | `{{LINUX_FILENAME}}` | `{{LINUX_SHA256}}` |

Package configuration выбирается **во время сборки**. После формирования installer/package файл `appsettings.json` является частью package payload. Installer не создаёт, не заменяет и не патчит packaged configuration.

# Windows

## 1. Проверка installer identity

Проверьте имя и SHA-256 файла `{{WINDOWS_FILENAME}}`.

{{WIN_ARTIFACT_IDENTITY}}

## 2. Установка

Запустите:

```text
{{WINDOWS_FILENAME}}
```

Подтвердите UAC elevation. Canonical installer выполняет machine-wide installation в Program Files, устанавливает self-contained application, регистрирует Windows Service `WebAssistant`, настраивает automatic start и запускает службу.

{{WIN_INSTALL_UAC}}

## 3. Installed Apps и версия

После установки `WebAssistant` должен присутствовать в Installed Apps / Programs and Features. `DisplayVersion` должен совпадать с `{{VERSION}}`.

{{WIN_INSTALLED_APPS}}

## 4. Проверка службы и health endpoint

В PowerShell:

```powershell
Get-Service -Name WebAssistant
Invoke-WebRequest http://127.0.0.1:17654/v1/health
```

Служба слушает только loopback. Default endpoint: `http://127.0.0.1:17654`.

{{WIN_SERVICE_HEALTH}}

Machine-wide runtime state расположен отдельно от Program Files:

```text
%ProgramData%\WebAssistant\logs
%ProgramData%\WebAssistant\data
```

## 5. Удаление

Используйте стандартное удаление WebAssistant через Installed Apps / Programs and Features либо штатный uninstall canonical bundle.

Uninstall останавливает и удаляет Windows Service, product registration и installed application files. `%ProgramData%\WebAssistant\logs` и `%ProgramData%\WebAssistant\data` по текущей state policy сохраняются.

{{WIN_UNINSTALL}}

# ALT Linux 10.1

Подтверждённая target environment для этой инструкции: **{{ALT_OS_NAME}} {{ALT_OS_VERSION}}**.

## 1. Проверка package identity

Проверьте имя и SHA-256 файла `{{LINUX_FILENAME}}`.

{{ALT_ARTIFACT_IDENTITY}}

## 2. Установка

Распакуйте `{{LINUX_FILENAME}}`, перейдите в распакованный каталог и запустите:

```bash
sudo ./install.sh
```

Application payload self-contained: target workstation не требует заранее установленного .NET Runtime, .NET SDK, NuGet или compiler/build tools.

`install.sh` проверяет distro-owned dependencies через RPM database. Если их не хватает, используются штатные `apt-get update` и `apt-get install`. Текущий dependency set включает `libicu74`, `libgtk+3`, `libsane` и `sane`.

Installer устанавливает application в `/opt/webassist`, создаёт service identity и runtime directories, устанавливает `webassist.service`, выполняет systemd daemon reload, enable/start и fail-closed проверку активности службы.

{{ALT_INSTALL}}

## 3. Проверка systemd и health

```bash
systemctl status webassist.service
curl http://127.0.0.1:17654/v1/health
```

Runtime state:

```text
application: /opt/webassist
logs:        /var/log/webassistant
data:        /var/lib/webassistant
unit:        /etc/systemd/system/webassist.service
```

{{ALT_SYSTEMD_HEALTH}}

## 4. Перезапуск службы

```bash
sudo systemctl restart webassist.service
systemctl status webassist.service
curl http://127.0.0.1:17654/v1/health
```

{{ALT_RESTART}}

## 5. Удаление

Из распакованного canonical package:

```bash
sudo ./uninstall.sh
```

По умолчанию сохраняются:

```text
/var/log/webassistant
/var/lib/webassistant
```

Для явного удаления runtime state:

```bash
sudo ./uninstall.sh --purge-data
```

{{ALT_UNINSTALL}}

# Диагностика

Если установка или запуск завершились ошибкой, сначала проверяйте system service state и `/v1/health`. Не заменяйте packaged `appsettings.json` во время установки: configuration принадлежит уже сформированному package.

Windows:

```powershell
Get-Service -Name WebAssistant
Invoke-WebRequest http://127.0.0.1:17654/v1/health
```

ALT Linux 10.1:

```bash
systemctl status webassist.service
journalctl -u webassist.service
curl http://127.0.0.1:17654/v1/health
```
