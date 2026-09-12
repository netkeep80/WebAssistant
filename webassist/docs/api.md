# WebAssistant REST API

Текущая major version — `v1`. Machine endpoints доступны только с prefix `/v1`; unversioned aliases, включая `/health` и `/scan`, отсутствуют. Service panel доступна по `/`.

Default listener: `http://127.0.0.1:17654`. Listener привязан только к loopback.

Windows runtime lifecycle не меняет HTTP contract: scanner adapter остаётся lazy, а если scanner runtime был материализован, остановка службы завершает shutdown принадлежащих WebAssistant `NAPS2.Worker` до завершения service stop.

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

## Нормализованные настройки сканера

Canonical machine-readable schema:

`GET /v1/scanner-settings/schema`

Успех: `200 OK`, `Content-Type: application/json`. Endpoint возвращает repository-owned schema `webassist/docs/scanner-settings.schema.json`. Это канонический список portable scanner settings, их типов, enum vocabulary, units и preferred defaults; backend-native WIA/TWAIN/SANE объекты наружу не публикуются.

Первый обязательный normalized набор:

| Поле | Тип | Значения / units | Preferred default |
| --- | --- | --- | --- |
| `settings.duplex` | boolean | `true`, `false` | `false` |
| `settings.dpi` | integer | положительное целое, unit `dpi` | `100` |
| `settings.colorMode` | string | `color`, `grayscale`, `blackAndWhite` | `color` |
| `settings.paperSize` | string | `letter`, `legal`, `a5`, `a4`, `a3`, `b5`, `b4` | `letter` |

Preferred default не означает, что каждое устройство обязано его поддерживать. Для concrete scanner/mode WebAssistant выбирает preferred default, если он поддерживается; иначе детерминированно выбирает первое поддерживаемое значение в canonical order. Если backend не предоставил доказуемый набор значений для optional setting, WebAssistant не выдумывает capability.

### Возможности выбранного scanner endpoint

`GET /v1/scanners/{scannerId}/settings`

Endpoint возвращает read-only snapshot нормализованных возможностей выбранного scanner endpoint. Он не читает и не меняет пользовательские preferences.

Пример:

```json
{
  "scannerId": "wa1-wia-...",
  "modes": [
    {
      "mode": "auto",
      "source": "auto",
      "duplex": false,
      "settings": {
        "dpi": {
          "supported": true,
          "values": [300],
          "unit": "dpi",
          "default": 300
        },
        "colorMode": {
          "supported": true,
          "values": ["grayscale"],
          "default": "grayscale"
        },
        "paperSize": {
          "supported": true,
          "values": ["a4"],
          "default": "a4"
        }
      }
    },
    {
      "mode": "flatbed",
      "source": "flatbed",
      "duplex": false,
      "settings": {
        "dpi": {
          "supported": true,
          "values": [100, 300, 600],
          "unit": "dpi",
          "default": 100
        },
        "colorMode": {
          "supported": true,
          "values": ["color", "grayscale"],
          "default": "color"
        },
        "paperSize": {
          "supported": true,
          "values": ["letter", "a4"],
          "default": "letter"
        }
      }
    }
  ]
}
```

Public mode vocabulary:

- `auto` → request `source=auto`, `duplex=false`;
- `flatbed` → request `source=flatbed`, `duplex=false`;
- `feeder` → request `source=feeder`, `duplex=false`;
- `feederDuplex` → request `source=feeder`, `duplex=true`.

Modes, которых concrete endpoint не поддерживает, отсутствуют.

Capabilities являются source-specific. Поэтому значения flatbed, feeder и duplex могут различаться. Для dual-source `auto` WebAssistant публикует только пересечение значений, которые допустимы для каждого concrete source, который auto реально может выбрать. Например flatbed `[100,300,600]` и feeder `[200,300]` дают auto `[300]`. Это гарантирует, что настройка, выбранная до проверки наличия бумаги, останется допустимой после выбора feeder или flatbed.

Ошибки endpoint:

- `400` — синтаксически неверный `scannerId`;
- `404` — корректный `scannerId` отсутствует после успешного перечисления его backend;
- `503` — scanner module/discovery либо backend указанного scannerId недоступен;
- `502` — непредвиденная ошибка capability discovery boundary.

## Сканирование

Единственный acquisition endpoint:

`POST /v1/scan`

Request обязан иметь `Content-Type: application/json` и содержать `scannerId`:

```json
{
  "scannerId": "wa1-wia-...",
  "source": "auto",
  "settings": {
    "duplex": false,
    "dpi": 300,
    "colorMode": "grayscale",
    "paperSize": "a4"
  }
}
```

Поля:

- `scannerId` — обязательный непустой stable identifier из `GET /v1/scanners`;
- `source` — опционально, только exact lowercase `auto`, `flatbed` или `feeder`; при отсутствии используется `auto`;
- `settings.duplex` — опциональный boolean; при отсутствии используется `false`;
- `settings.dpi` — опциональный положительный integer DPI;
- `settings.colorMode` — опционально, exact `color`, `grayscale` или `blackAndWhite`;
- `settings.paperSize` — опционально, exact `letter`, `legal`, `a5`, `a4`, `a3`, `b5` или `b4`.

Если `dpi`, `colorMode` или `paperSize` не переданы, применяется effective default выбранного public mode из той же capability/default policy, которая используется `GET /v1/scanners/{scannerId}/settings`. Если backend не предоставил доказуемые normalized values для optional setting и caller его не передал, WebAssistant сохраняет backend default вместо изобретения значения.

Caller обязан сохранять пользовательские scanner preferences у себя и передавать их в каждом acquisition request. WebAssistant не хранит mutable per-user/per-scanner profile.

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

### Валидация settings

Валидация выполняется до physical acquisition:

- неизвестное имя `colorMode` или `paperSize`, неположительный `dpi` и другая syntactic/schema ошибка → `400`;
- syntactically valid normalized value, которого нет в capability projection выбранного mode → `422`;
- invalid/unsupported request не запускает scanner acquisition.

### Ошибки acquisition

- `400` — отсутствующий/пустой/синтаксически неверный `scannerId`, malformed JSON, неверный `source`, неверное сочетание `source`/`duplex` либо malformed normalized setting;
- `404` — синтаксически корректный `scannerId` отсутствует после успешного перечисления указанного им backend;
- `409` — другой physical scanner acquisition уже выполняется;
- `422` — запрошенный source, duplex или valid normalized setting не поддерживается выбранным endpoint/mode;
- `502` — ошибка scanner acquisition либо scanner backend не вернул читаемый PDF;
- `503` — scanner module/discovery недоступен либо backend, на который указывает корректный `scannerId`, в данный момент не удалось перечислить.

На рабочей станции действует единый acquisition lock: одновременно выполняется не более одного physical scanner acquisition.

Успех: `200 OK`, `Content-Type: application/pdf`. PDF передаётся непосредственно в HTTP body; все страницы одного acquisition формируют один многостраничный PDF.

Base64, JSON document envelope и ZIP/raster envelope не используются как scanner document transport. После handoff PDF вызывающей стороне scanner operation заканчивается и не хранит завершённый документ как long-term scanner storage.

Scanner operation сама не выполняет edit/merge/split PDF, OCR/annotation/watermark/deskew или другую semantic document transformation, signing/encryption, business/backend upload и не требует business authentication или per-user business profile. Это граница scanner capability; независимые capabilities WebAssistant имеют отдельную семантику.

## Диагностическая панель

Service panel `/` является browser-level клиентом того же публичного scanner API. Для scanner controls она загружает canonical `GET /v1/scanner-settings/schema` и capability projection выбранного `scannerId`, после чего формирует доступные mode/settings controls без WIA/TWAIN/SANE и scanner-model-specific правил.

Панель предоставляет:

- выбор scanner endpoint;
- одну кнопку `Информация о сканере` с read-only discovery/capability данными;
- один mode selector (`Авто`, `Стекло`, `ADF`, `ADF duplex`) из реально доступных modes;
- capability-driven controls для accepted normalized settings;
- одну action `Сканировать`;
- просмотр/открытие/сохранение полученного PDF;
- runtime status и собственный журнал WebAssistant.

Панель не хранит пользовательские scanner preferences как профиль.

## Диагностика API

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
