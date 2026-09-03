# WebAssistant REST API

Текущая major version — `v1`. Machine endpoints доступны только с prefix `/v1`. Service panel доступна по `/`.

Default listener: `http://127.0.0.1:17654`. Listener привязан к loopback.

Все перечисленные machine endpoints относятся к текущему `/v1` baseline.

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

Успех: `200 OK`, `Content-Type: application/pdf`, PDF передаётся непосредственно в HTTP body. Если acquisition вернул несколько страниц, они формируют один многостраничный PDF.

Явно запрошенный source не заменяется скрытым fallback на другой source. Ошибка backend или пустой/нечитаемый результат возвращаются как `502`.

## Диагностика

`GET /v1/diag/info`

Возвращает безопасную runtime-информацию: version, OS, uptime, listen URL, API version и текущее состояние scan coordinator.

`GET /v1/diag/logs?date=YYYY-MM-DD`

Возвращает собственный суточный журнал WebAssistant как `text/plain`.

Ошибки:
- `400` — дата отсутствует или имеет неверный формат;
- `404` — журнал за дату отсутствует.

Endpoint принимает только дату, а не filename/path. PDF bytes, Base64, содержимое страниц и document body в технический журнал не записываются.

## CORS

CORS выключен по умолчанию. Для browser origin, отличающегося от origin service panel, его нужно явно добавить в JSON allowlist и установить `WebAssistant:Cors:Enabled=true`. Wildcard `*` запрещён.

## Filesystem capability

`WebAssistant:FileSystem:RootDirectory` уже является runtime boundary, но browser-facing filesystem routes в текущем API не опубликованы.
