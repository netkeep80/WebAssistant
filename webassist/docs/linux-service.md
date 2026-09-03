# WebAssistant как systemd-служба

Linux package собирается командой `webassist/build/linux/package.sh [output-directory]`. Скрипт определяет product root относительно собственного расположения, поэтому текущий рабочий каталог не важен.

Пакет содержит self-contained `linux-x64` приложение, `install.sh`, `uninstall.sh` и `webassist.service`.

`install.sh` запускается от root. Он устанавливает системные runtime-зависимости .NET/NAPS2 integration (`libicu74`, GTK3 и SANE), создаёт системного пользователя `webassist`, добавляет его в доступные scanner groups, размещает приложение в `/opt/webassist`, журналы в `/var/log/webassist`, data root в `/var/lib/webassist`, записывает JSON-конфигурацию и включает `webassist.service`.

Служба слушает только `127.0.0.1`; порт по умолчанию `17654`. Проверка: `curl http://127.0.0.1:17654/v1/health`.

`uninstall.sh` удаляет unit, приложение и service identity, но по умолчанию сохраняет `/var/log/webassist` и `/var/lib/webassist`. `uninstall.sh --purge-data` удаляет также журналы и данные.
