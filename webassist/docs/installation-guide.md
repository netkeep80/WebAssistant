# Установка WebAssistant

Эта инструкция относится к WebAssistant **{{VERSION}}**, собранному из исходного commit `{{SOURCE_SHA}}`.

Она описывает установку двух канонических дистрибутивов:

- Windows: `{{WINDOWS_FILENAME}}`;
- ALT Linux 10.1: `{{LINUX_FILENAME}}`.

## Проверка полученных файлов

Перед установкой убедитесь, что имя файла и SHA-256 совпадают с метаданными выпуска.

| Платформа | Файл | SHA-256 |
| --- | --- | --- |
| Windows | `{{WINDOWS_FILENAME}}` | `{{WINDOWS_SHA256}}` |
| ALT Linux 10.1 | `{{LINUX_FILENAME}}` | `{{LINUX_SHA256}}` |

Конфигурация пакета выбирается **во время сборки**. После формирования установщика или пакета файл `appsettings.json` является частью содержимого пакета. Установщик не создаёт, не заменяет и не исправляет упакованную конфигурацию. Полная структура `appsettings.json`, значения по умолчанию, приоритет источников и различия платформ описаны в [`configuration.md`](configuration.md).

# Windows

## 1. Проверка идентичности установщика

Проверьте имя и SHA-256 файла `{{WINDOWS_FILENAME}}`.

{{WIN_ARTIFACT_IDENTITY}}

## 2. Установка

Запустите:

```text
{{WINDOWS_FILENAME}}
```

Подтвердите повышение прав UAC. Канонический установщик выполняет общесистемную установку в Program Files, устанавливает самодостаточное приложение, регистрирует Windows Service `WebAssistant`, настраивает автоматический запуск и запускает службу.

{{WIN_INSTALL_UAC}}

## 3. Installed Apps и версия

После установки WebAssistant должен присутствовать в Installed Apps / Programs and Features. `DisplayVersion` должен совпадать с `{{VERSION}}`.

{{WIN_INSTALLED_APPS}}

## 4. Проверка службы и точки состояния

В PowerShell:

```powershell
Get-Service -Name WebAssistant
Invoke-WebRequest http://127.0.0.1:17654/v1/health
```

Служба слушает только loopback. Адрес по умолчанию: `http://127.0.0.1:17654`.

{{WIN_SERVICE_HEALTH}}

Общесистемное состояние времени выполнения расположено отдельно от Program Files:

```text
%ProgramData%\WebAssistant\logs
%ProgramData%\WebAssistant\data
```

## 5. Удаление

Используйте стандартное удаление WebAssistant через Installed Apps / Programs and Features либо штатное удаление канонического bundle.

Удаление останавливает и удаляет Windows Service, регистрацию продукта и установленные файлы приложения. `%ProgramData%\WebAssistant\logs` и `%ProgramData%\WebAssistant\data` по текущей политике состояния сохраняются.

{{WIN_UNINSTALL}}

# ALT Linux 10.1

Подтверждённая целевая среда для этой инструкции: **{{ALT_OS_NAME}} {{ALT_OS_VERSION}}**.

## 1. Проверка идентичности пакета

Проверьте имя и SHA-256 файла `{{LINUX_FILENAME}}`.

{{ALT_ARTIFACT_IDENTITY}}

## 2. Установка

Распакуйте `{{LINUX_FILENAME}}`, перейдите в распакованный каталог и запустите:

```bash
sudo ./install.sh
```

Payload приложения самодостаточен: целевая рабочая станция не требует заранее установленного .NET Runtime, .NET SDK, NuGet или инструментов компиляции и сборки.

`install.sh` проверяет системные зависимости дистрибутива через базу RPM. Если их не хватает, используются штатные `apt-get update` и `apt-get install`. Текущий набор зависимостей включает `libicu74`, `libgtk+3`, `libsane` и `sane`.

Установщик устанавливает приложение в `/opt/webassist`, создаёт учётную запись службы и каталоги состояния времени выполнения, устанавливает `webassist.service`, выполняет перечитывание конфигурации systemd, включает и запускает службу и с закрытием при неопределённости проверяет её активность.

{{ALT_INSTALL}}

## 3. Проверка systemd и состояния HTTP

```bash
systemctl status webassist.service
curl http://127.0.0.1:17654/v1/health
```

Состояние времени выполнения:

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

Из распакованного канонического пакета:

```bash
sudo ./uninstall.sh
```

По умолчанию сохраняются:

```text
/var/log/webassistant
/var/lib/webassistant
```

Для явного удаления состояния времени выполнения:

```bash
sudo ./uninstall.sh --purge-data
```

{{ALT_UNINSTALL}}

# Диагностика

Если установка или запуск завершились ошибкой, сначала проверяйте состояние системной службы и `/v1/health`. Не заменяйте упакованный `appsettings.json` во время установки: конфигурация принадлежит уже сформированному пакету.

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
