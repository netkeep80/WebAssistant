# WebAssistant REST API

Текущая major version — `v1`. Machine endpoints доступны только с prefix `/v1`; unversioned aliases, включая `/health` и `/scan`, отсутствуют. Service panel доступна по `/`.

Default listener: `http://127.0.0.1:17654`. Listener привязан только к loopback.

## Health

`GET /v1/health`

Успех: `200 OK`.

```json
{"status":"ok"}
```

## Сканеры

`GET /v1/scanners`

Успех: `200 OK` и JSON-массив объектов `{ "id", "name" }`.

Ошибки:
- `503` — scanner module недоступен;
- `502` — ошибка обнаружения устройств.

Backend semantics:
- Windows — WIA-first; TWAIN используется только если WIA не вернул ни одного устройства;
- Linux — direct SANE SDK path через NAPS2, без CLI orchestration.

## Сканирование

Operations:
- `POST /v1/scan` — glass/flatbed;
- `POST /v1/scan/feeder` — односторонний feeder;
- `POST /v1/scan/duplex` — duplex feeder.

Опциональный query parameter: `scannerId`.

Selection semantics:
- 0 сканеров без `scannerId` -> `503`;
- 1 сканер без `scannerId` -> автоматический выбор единственного устройства;
- 2+ сканеров без `scannerId` -> `409`, требуется явный выбор;
- пустой `scannerId` -> `400`;
- неизвестный `scannerId` -> `404` до acquisition.

На рабочей станции действует единый acquisition lock. Если сканирование уже выполняется, конкурирующий request получает `409` и второй physical acquisition не запускается.

Успех: `200 OK`, `Content-Type: application/pdf`, PDF передаётся непосредственно в HTTP body. Все страницы одного acquisition формируют один многостраничный PDF.

Base64, JSON document envelope и ZIP/raster envelope не используются как scanner document transport. После handoff PDF вызывающей стороне scanner operation заканчивается и не хранит завершённый документ как long-term scanner storage.

Явно запрошенный source не заменяется скрытым fallback на другой source. Ошибка backend или пустой/нечитаемый результат возвращаются как `502`.

Scanner operation сама не выполняет edit/merge/split PDF, OCR/annotation/watermark/deskew или другую semantic document transformation, signing/encryption, business/backend upload и не требует business authentication или per-user business profile. Это граница scanner capability; независимые capabilities WebAssistant имеют отдельную семантику.

## Диагностика

`GET /v1/diag/info`

Возвращает безопасную runtime-информацию: version, OS, uptime, listen URL, API version и текущее состояние scan coordinator.

`GET /v1/diag/logs?date=YYYY-MM-DD`

Возвращает собственный суточный журнал WebAssistant как `text/plain`.

Ошибки:
- `400` — дата отсутствует или имеет неверный формат;
- `404` — журнал за дату отсутствует.

Endpoint принимает только дату, а не filename/path. PDF bytes, Base64, содержимое страниц и document body в технический журнал не записываются. Current service сам не выполняет automatic retention/delete старых daily logs.

## CORS

CORS выключен по умолчанию. Для browser origin, отличающегося от origin service panel, его нужно явно добавить в JSON allowlist и установить `WebAssistant:Cors:Enabled=true`. Разрешены только exact HTTP/HTTPS origins; wildcard `*` запрещён.

## Filesystem capability

`WebAssistant:FileSystem:RootDirectory` является runtime boundary. Внутренний path resolver принимает только относительные пути внутри configured root и отвергает navigation segments, absolute paths и существующие symlink/reparse-point components. Browser-facing filesystem routes в текущем API не опубликованы.
