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

Успех: `200 OK` и нормализованный JSON envelope:

```json
{
  "scanners": [
    {
      "scannerId": "wa1-wia-...",
      "name": "Scanner name",
      "backend": "wia",
      "sources": {
        "flatbed": true,
        "feeder": true,
        "duplex": true
      }
    }
  ],
  "warnings": [
    {
      "backend": "twain",
      "code": "enumerationFailed"
    }
  ]
}
```

`scannerId` — opaque persistent identifier WebAssistant. Он детерминированно выводится из backend и exact native identity устройства, не зависит от порядка перечисления и предназначен для сохранения вызывающим приложением между restart/reboot. Один физический сканер, доступный через разные backend, например WIA и TWAIN, является разными scanner endpoints и получает разные `scannerId`.

WebAssistant не хранит выбранный `scannerId` или mutable scanner profile. При каждом discovery/acquisition endpoint разрешается заново из текущего backend state.

Backend semantics:

- Windows — WIA и TWAIN перечисляются независимо; успешные endpoints обоих backend входят в общий `scanners`;
- сбой одного Windows backend не скрывает endpoints второго: успешная часть возвращается вместе с `warnings`;
- exact duplicate native identity внутри одного backend считается неоднозначной и не получает искусственный index/suffix; такой endpoint отклоняется fail-closed;
- Linux — direct SANE SDK path через NAPS2, без CLI orchestration.

Текущие warning codes:

- `enumerationFailed` — backend не удалось перечислить;
- `ambiguousNativeIdentity` — внутри backend обнаружена неоднозначная native identity.

Если хотя бы один применимый backend успешно перечислен, `GET /v1/scanners` возвращает `200`, в том числе когда список устройств пуст. Если discovery в целом недоступен, возвращается `503`. Непредвиденная ошибка discovery boundary возвращается как `502`.

Native identity и внутреннее состояние наличия бумаги наружу не публикуются.

## Сканирование

Единственный acquisition endpoint:

`POST /v1/scan`

Request обязан иметь `Content-Type: application/json` и содержать `scannerId`:

```json
{
  "scannerId": "wa1-wia-...",
  "source": "auto",
  "settings": {
    "duplex": false
  }
}
```

Поля:

- `scannerId` — обязательный непустой stable identifier из `GET /v1/scanners`;
- `source` — опционально, только exact lowercase `auto`, `flatbed` или `feeder`; при отсутствии используется `auto`;
- `settings.duplex` — опциональный boolean; при отсутствии используется `false`.

Source-specific routes `/v1/scan/feeder` и `/v1/scan/duplex` отсутствуют. `scannerId` не передаётся через query parameter и автоматический выбор scanner endpoint по количеству найденных устройств не выполняется.

### Автовыбор источника

Автовыбор принадлежит WebAssistant, а не caller и не NAPS2 `PaperSource.Auto`.

Для устройства с одновременно доступными flatbed и feeder используется tri-state состояние бумаги:

- `PRESENT` → feeder;
- `ABSENT` → flatbed;
- `UNKNOWN` → flatbed.

`UNKNOWN` никогда не трактуется как `PRESENT`.

Если доступен только feeder, `auto` использует feeder. Если доступен только flatbed, `auto` использует flatbed. Если подходящего source нет, запрос отклоняется до physical acquisition.

Состояние бумаги является snapshot capability, а не гарантией успешной последующей подачи. Если `auto` выбрал feeder для endpoint с доступным flatbed, но реальная попытка acquisition завершилась именно `DeviceFeederEmptyException`, WebAssistant один раз повторяет acquisition со стекла. Другие ошибки feeder не вызывают такого fallback.

Явно заданный `flatbed` или `feeder` не имеет скрытого fallback на другой source. В частности, `source=feeder` при пустом ADF возвращает ошибку acquisition и не переключается на flatbed.

### Duplex

`duplex` отделён от `source`:

- `source=auto` + `duplex=true` → `400`;
- `source=flatbed` + `duplex=true` → `400`;
- `source=feeder` + `duplex=false` → simplex feeder;
- `source=feeder` + `duplex=true` → duplex feeder, только если endpoint объявляет `sources.duplex=true`; иначе `422`.

Неподдерживаемый явно запрошенный flatbed/feeder также возвращает `422` до acquisition.

### Ошибки acquisition

- `400` — отсутствующий/пустой/синтаксически неверный `scannerId`, malformed JSON, неверный `source` или недопустимое сочетание `source`/`duplex`;
- `404` — синтаксически корректный `scannerId` отсутствует после успешного перечисления указанного им backend;
- `409` — другой physical scanner acquisition уже выполняется;
- `422` — запрошенный source или duplex не поддерживается endpoint;
- `502` — ошибка scanner acquisition либо scanner backend не вернул читаемый PDF;
- `503` — scanner module/discovery недоступен либо backend, на который указывает корректный `scannerId`, в данный момент не удалось перечислить.

На рабочей станции действует единый acquisition lock: одновременно выполняется не более одного physical scanner acquisition.

Успех: `200 OK`, `Content-Type: application/pdf`. PDF передаётся непосредственно в HTTP body; все страницы одного acquisition формируют один многостраничный PDF.

Base64, JSON document envelope и ZIP/raster envelope не используются как scanner document transport. После handoff PDF вызывающей стороне scanner operation заканчивается и не хранит завершённый документ как long-term scanner storage.

Scanner operation сама не выполняет edit/merge/split PDF, OCR/annotation/watermark/deskew или другую semantic document transformation, signing/encryption, business/backend upload и не требует business authentication или per-user business profile. Это граница scanner capability; независимые capabilities WebAssistant имеют отдельную семантику.

Дополнительные scanner settings, включая DPI, color mode и paper size, относятся к отдельному развитию API и не входят в текущий scanner request contract.

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
