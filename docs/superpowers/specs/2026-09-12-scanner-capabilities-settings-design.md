# Scanner capabilities/settings — design for #164

Дата: 2026-09-12

Issue: #164

Baseline: `main=7f1ecac30d5b33bbc96d381df6e443e80a47a75f`, `webassist/VERSION=0.3.25`.

## Цель

Расширить scanner API WebAssistant нормализованными acquisition-native настройками `dpi`, `colorMode`, `paperSize` поверх уже принятой модели `scannerId + source + settings.duplex`, не раскрывая WIA/TWAIN/SANE/NAPS2 типы наружу и не сохраняя mutable scanner profile внутри WebAssistant.

Одновременно диагностическая панель становится canonical browser-level acceptance client этого публичного API.

## Жёсткие границы

- accepted `webassistant-contract/v0.2` и `webassistant-conformance/v0.2` не меняются;
- `repo-policy.json` не меняется;
- текущая работа меняет только существующий unaccepted candidate v0.3;
- `source=auto` semantics из #163 сохраняются: PRESENT -> feeder, ABSENT/UNKNOWN -> flatbed для dual-source endpoint, explicit source без скрытого fallback;
- caller хранит выбранный `scannerId` и пользовательские настройки; WebAssistant остаётся stateless;
- scanner operation по-прежнему возвращает raw multipage `application/pdf` и не включает OCR/deskew/watermark/semantic post-processing;
- VERSION меняется один раз только после функционального GREEN.

## Публичные normalized fields

Первый обязательный набор:

```text
duplex     boolean
 dpi        integer
colorMode  color | grayscale | blackAndWhite
paperSize  letter | legal | a5 | a4 | a3 | b5 | b4
```

`duplex` уже существует и сохраняет текущую top-level/source семантику: duplex допустим только при `source=feeder`.

`paperSize` использует только well-known `PageSize`, уже определённые exact pinned NAPS2 SDK. Никакой scanner endpoint не обязан поддерживать все значения.

## Source-specific capability model

Pinned NAPS2 `ScanCaps` предоставляет `FlatbedCaps`, `FeederCaps`, `DuplexCaps`; внутри каждого `PerSourceCaps` доступны `DpiCaps`, `BitDepthCaps`, `PageSizeCaps`. Поэтому публичные capabilities нельзя честно flatten'ить в один scanner-wide набор.

WebAssistant строит собственную normalized projection по режимам public request:

```text
auto + simplex
flatbed + simplex
feeder + simplex
feeder + duplex
```

Поддерживаемые concrete modes зависят от endpoint capabilities.

### Auto mode

Для `source=auto` concrete source выбирается во время acquisition по #163. Поэтому setting считается поддержанным для `auto` только если он допустим для каждого concrete source, который auto реально может выбрать у этого endpoint.

Пример:

```text
flatbed dpi = [100,300,600]
feeder  dpi = [200,300]
auto    dpi = [300]
```

Это запрещает конфигурацию, которая корректна до помещения бумаги в ADF, но становится недопустимой после выбора feeder.

Для feeder-only или flatbed-only endpoint auto projection совпадает с единственным concrete source.

## Canonical machine-readable schema

Repository-owned schema является каноническим публичным определением normalized field vocabulary, типов, enum/units, preferred default и validation semantics.

Путь:

`webassist/docs/scanner-settings.schema.json`

Публичный endpoint:

`GET /v1/scanner-settings/schema`

Он возвращает exact canonical schema content как JSON.

Schema не содержит scanner-specific supported values. Scanner-specific данные возвращает отдельный endpoint.

## Scanner-specific settings endpoint

`GET /v1/scanners/{scannerId}/settings`

Response содержит:

- `scannerId`;
- `modes` — normalized projection по доступным public request modes;
- для каждого mode — `source`, `duplex`, `settings`;
- для `dpi` — `supported`, допустимые `values`, `unit=dpi`, `default`;
- для `colorMode` — `supported`, допустимые `values`, `default`;
- для `paperSize` — `supported`, допустимые `values`, `default`.

Endpoint описывает capabilities snapshot и не меняет scanner state.

Если `scannerId` синтаксически неверен -> `400`; если backend доступен, но endpoint не найден -> `404`; если backend/discovery недоступен -> `503`; unexpected capability discovery failure -> `502`.

## Defaults

Preferred WebAssistant defaults сохраняют текущее фактическое поведение pinned NAPS2 там, где оно допустимо:

```text
dpi        100
colorMode  color
paperSize  letter
duplex     false
```

Для concrete mode effective default вычисляется детерминированно:

1. preferred default, если он входит в supported projection;
2. иначе первый supported value в canonical order.

Canonical order:

```text
colorMode: color, grayscale, blackAndWhite
paperSize: letter, legal, a5, a4, a3, b5, b4
dpi: ascending numeric
```

`GET /settings` возвращает уже concrete effective default; `POST /scan` использует ту же функцию.

## POST /v1/scan

Request расширяется без изменения существующих полей:

```json
{
  "scannerId": "wa1-wia-...",
  "source": "flatbed",
  "settings": {
    "duplex": false,
    "dpi": 300,
    "colorMode": "grayscale",
    "paperSize": "a4"
  }
}
```

Все новые поля optional. Omitted field получает concrete effective default для выбранного request mode.

Validation происходит до physical acquisition:

- malformed/unknown normalized value -> `400`;
- syntactically valid normalized value, unsupported выбранным mode -> `422`;
- no second acquisition starts for invalid settings.

Adapter boundary получает уже нормализованные effective settings и преобразует их в NAPS2 `ScanOptions`:

- `Dpi`;
- `BitDepth.Color|Grayscale|BlackAndWhite`;
- well-known `PageSize`;
- existing mapped `PaperSource`.

NAPS2 types выше adapter boundary не публикуются.

## Internal model

Добавляются focused production units:

- `ScannerSettings.cs` — normalized values/effective settings;
- `ScannerCapabilities.cs` — source-specific normalized capability records;
- `ScannerCapabilityProjection.cs` — conversion/projection/default/intersection logic;
- `ScannerSettingsEndpointHandlers.cs` — schema + selected scanner settings HTTP response;
- adapters сохраняют `ScanCaps` как normalized capabilities в `ScannerDevice` и принимают effective settings при acquisition.

`ScannerDevice` не хранит raw NAPS2 objects.

## Diagnostic/service panel

Панель `/` входит в acceptance #164.

Scanner UI:

```text
Scanner: [ endpoint v ] [Информация о сканере]
Mode:    [ Auto/Стекло/ADF/ADF duplex v]
DPI:     [ capability-driven v]
Цвет:    [ capability-driven v]
Бумага:  [ capability-driven v]
[Сканировать]
```

После смены scanner или mode панель получает canonical schema + `GET /v1/scanners/{scannerId}/settings` и рендерит controls только из этих данных. Она не знает backend/model-specific rules.

`Информация о сканере` показывает read-only endpoint identity (`scannerId`, name, backend, source support) и capability projection.

Mode-specific отдельные Scan buttons удаляются.

Browser/service-panel tests должны доказать:

```text
panel -> scanner enumeration -> endpoint selection -> information -> mode/settings controls -> POST /v1/scan -> PDF
```

Direct unit/API/adapter tests остаются обязательными.

## Classification exact pinned dependency surface

Для первого набора:

```text
dpi        NORMALIZED_SUPPORTED      via PerSourceCaps.DpiCaps / ScanOptions.Dpi
colorMode  NORMALIZED_SUPPORTED      via BitDepthCaps / ScanOptions.BitDepth
paperSize  NORMALIZED_SUPPORTED      via PageSizeCaps / ScanOptions.PageSize
duplex     NORMALIZED_SUPPORTED      already implemented through source policy
```

Arbitrary crop rectangle, OCR, deskew, watermark, signing и прочий post-processing не включаются.

Нового NAPS2 fork delta для #164 не требуется при отсутствии нового доказанного dependency gap.

## Acceptance

- canonical schema существует и возвращается через versioned API;
- scanner settings endpoint возвращает source/mode-specific projection;
- auto projection безопасна для любого concrete source, который auto может выбрать;
- dpi/colorMode/paperSize применяются adapter'ами к physical acquisition;
- invalid -> 400, unsupported -> 422 до acquisition;
- omitted settings используют единый deterministic default policy;
- diagnostic panel использует schema + endpoint projection и поддерживает mode/settings/info;
- browser panel acceptance проверяет новый путь;
- docs/api.md описывает фактический contract;
- candidate contract/conformance отражают observable delta;
- accepted v0.2 и repo-policy неизменны;
- functional tests GREEN до VERSION transition; VERSION повышается ровно один раз затем final full CI/repo-guard GREEN.
