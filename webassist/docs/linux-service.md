# WebAssistant как systemd-служба

## Canonical artifact

Поддерживаемая целевая среда Linux distribution:

```text
ALT Linux 10.1
```

Canonical package producer:

```bash
./build/linux/package.sh
```

создаёт:

```text
WebAssistant-linux-x64-<VERSION>.zip
```

где `<VERSION>` равен exact content файла `VERSION`. Рядом создаются `.sha256` и `.provenance.json` для final ZIP.

ZIP содержит self-contained `linux-x64` application, `VERSION`, package-owned `appsettings.json`, `install.sh`, `uninstall.sh` и `webassist.service`.

## Package-owned configuration

Configuration выбирается package-time:

```text
src/WebAssistant/appsettings.json существует
  -> producer копирует exact bytes этого файла

файл отсутствует
  -> producer копирует build/common/default-appsettings.json
```

После формирования ZIP `appsettings.json` является immutable package payload. `install.sh` не генерирует, не заменяет и не патчит configuration. Если packaged `appsettings.json` отсутствует, package считается повреждённым и installation завершается fail-closed.

## Установка на ALT Linux 10.1

Распакуйте:

```text
WebAssistant-linux-x64-<VERSION>.zip
```

и из распакованного каталога запустите:

```bash
sudo ./install.sh
```

Target workstation не требует заранее установленного .NET Runtime, .NET SDK, NuGet или compiler/build tools: application payload self-contained.

`install.sh` проверяет distro-owned runtime dependencies через RPM database. Текущий список включает `libicu74`, `libgtk+3`, `libsane` и `sane`.

Если все зависимости уже установлены, apt-rpm не изменяется. Если чего-то не хватает, installer выполняет штатные:

```text
apt-get update
apt-get install
```

Ошибка repository/package-manager state завершается явной диагностикой; системные RPM не vendoring'ятся внутрь WebAssistant ZIP.

Installer затем:

- создаёт system group/user `webassist`, если они отсутствуют;
- добавляет service identity в доступные scanner groups;
- устанавливает application в `/opt/webassist`;
- использует packaged `appsettings.json` без замены;
- создаёт runtime state directories;
- устанавливает `webassist.service`;
- выполняет systemd daemon reload;
- enables и starts `webassist.service`;
- fail-closed проверяет активность службы.

## Runtime state

```text
application: /opt/webassist
logs:        /var/log/webassistant
data:        /var/lib/webassistant
unit:        /etc/systemd/system/webassist.service
```

`/var/log/webassistant` и `/var/lib/webassistant` принадлежат service identity и сохраняются по умолчанию при uninstall.

Служба слушает только loopback `127.0.0.1`; default port — `17654`.

## Проверка

Состояние службы:

```bash
systemctl status webassist.service
```

Health:

```bash
curl http://127.0.0.1:17654/v1/health
```

Остановка/запуск/перезапуск выполняются стандартными systemd командами:

```bash
sudo systemctl stop webassist.service
sudo systemctl start webassist.service
sudo systemctl restart webassist.service
```

## Удаление

Из распакованного canonical package:

```bash
sudo ./uninstall.sh
```

По умолчанию удаляются unit, installed application и service identity, но сохраняются:

```text
/var/log/webassistant
/var/lib/webassistant
```

Для явного удаления runtime state используется:

```bash
sudo ./uninstall.sh --purge-data
```

## Evidence boundary

Canonical target остаётся ALT Linux 10.1. Automated execution в другой ALT environment может быть полезной regression evidence для package/systemd mechanics, но не заменяет target acceptance именно на ALT Linux 10.1.
