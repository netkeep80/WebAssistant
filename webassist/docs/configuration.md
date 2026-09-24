# Конфигурация WebAssistant

Этот документ является каноническим человекочитаемым описанием runtime-конфигурации WebAssistant. Машиночитаемая проекция JSON-файла находится в `docs/appsettings.schema.json`.

## Где находится `appsettings.json`

WebAssistant не хранит environment-specific `src/WebAssistant/appsettings.json` в публичном репозитории. Канонические упаковщики выбирают конфигурацию во время формирования пакета:

1. если `src/WebAssistant/appsettings.json` существует, в payload попадают его точные байты;
2. иначе в payload копируется `build/common/default-appsettings.json`.

После формирования пакета установщик не генерирует и не исправляет `appsettings.json`.

Установленная конфигурация находится рядом с приложением:

- Windows: `%ProgramFiles%\WebAssistant\appsettings.json`;
- Linux: `/opt/webassist/appsettings.json`;
- при ручном запуске из publish-каталога — рядом с `WebAssistant`.

## Полная структура

Безопасная конфигурация репозитория по умолчанию:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "WebAssistant": "Information"
    }
  },
  "WebAssistant": {
    "Port": 17654,
    "LogDirectory": "",
    "Cors": {
      "Enabled": false,
      "AllowedOrigins": []
    },
    "FileSystem": {}
  }
}
```

### `Logging`

Это стандартная конфигурационная поверхность журналирования ASP.NET Core/.NET. WebAssistant поставляет значения `Information` для `Logging:LogLevel:Default` и `Logging:LogLevel:WebAssistant`, но не вводит собственную закрытую схему для всех возможных категорий и провайдеров .NET.

Практически используемые уровни: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`.

### `WebAssistant:Port`

Тип: integer.

Допустимый диапазон: 1024..65535.

Значение по умолчанию: 17654.

Адрес прослушивания не настраивается: WebAssistant всегда привязывается к loopback. Параметры `urls`, `http_ports`, `https_ports` и непустой `Kestrel:Endpoints` запрещены и приводят к отказу запуска.

### `WebAssistant:LogDirectory`

Тип: string.

Поле необязательно. Пустое или отсутствующее значение означает платформенный каталог по умолчанию:

- Windows: `%ProgramData%\WebAssistant\logs`;
- Linux: `/var/log/webassistant`;
- другие платформы: подкаталог `logs` рядом с приложением.

Непустое значение преобразуется в абсолютный путь через `Path.GetFullPath`.

### `WebAssistant:Cors:Enabled`

Тип: boolean.

Значение по умолчанию: `false`.

Если поле отсутствует или пустое в effective configuration, CORS выключен. Значение, которое нельзя разобрать как boolean, приводит к отказу запуска.

### `WebAssistant:Cors:AllowedOrigins`

Тип: array of string.

Используется только при `WebAssistant:Cors:Enabled=true`.

Каждый элемент должен быть точным origin с HTTP/HTTPS scheme, host и необязательным port. Запрещены wildcard `*`, user-info, path, query и fragment. Сравнение выполняется без учёта регистра после канонизации authority. Пустые элементы пропускаются.

### `WebAssistant:FileSystem`

Тип: object-map `logicalRootName -> physicalRootPath`.

Число logical roots не имеет hard-coded product maximum. Пустая или отсутствующая карта означает, что файловая подсистема не настроена.

Имя root должно:

- начинаться с ASCII letter/digit;
- далее содержать только ASCII letters/digits, `.`, `_`, `-`;
- не быть `.` или `..`;
- не дублировать другое имя только изменением регистра.

Значение root — непустой полностью квалифицированный физический путь. Каждый root создаёт отдельную нативную область полномочий; недоступность одного root не подменяет его другим.

Поле с именем `RootDirectory` не имеет специальной legacy-семантики. Если оно присутствует внутри `WebAssistant:FileSystem` и содержит допустимый абсолютный путь, текущая реализация трактует его как обычный logical root с публичным именем `RootDirectory`. Автоматической миграции старой однокорневой конфигурации нет; при миграции следует явно выбрать требуемое имя, например `archive`.

## Минимальная конфигурация

WebAssistant может запускаться без секции `WebAssistant`; тогда используются runtime defaults, а файловая подсистема остаётся не настроена. Для явного минимального package-файла:

```json
{
  "WebAssistant": {
    "FileSystem": {}
  }
}
```

## Пример полной конфигурации

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "WebAssistant": "Debug"
    }
  },
  "WebAssistant": {
    "Port": 17654,
    "LogDirectory": "D:/WebAssistant/logs",
    "Cors": {
      "Enabled": true,
      "AllowedOrigins": [
        "http://localhost:8080",
        "https://example.internal"
      ]
    },
    "FileSystem": {
      "archive": "D:/archive",
      "import": "//192.168.1.20/import"
    }
  }
}
```

На Linux физические пути обычно имеют POSIX-форму, например `/srv/archive`.

## Источники и приоритет

Приложение создаётся через стандартный `WebApplication.CreateBuilder(args)`, поэтому effective configuration формируется стандартными провайдерами ASP.NET Core. Для application configuration более поздний источник переопределяет более ранний:

1. `appsettings.json`;
2. `appsettings.{Environment}.json`, если такой файл дополнительно присутствует рядом с приложением;
3. environment variables;
4. command-line arguments.

Public WebAssistant project не задаёт `UserSecretsId`, поэтому user secrets не являются поддерживаемым repository-owned источником конфигурации.

Для environment variables вложенные ключи задаются через двойное подчёркивание, например `WebAssistant__Port=17655`. Для command line применяется стандартная форма ASP.NET Core, например `--WebAssistant:Port 17655`.

Независимо от источника применяются те же runtime-проверки диапазонов, origins, listener restrictions и filesystem roots. Package ownership означает неизменность файла установщиком, но не отменяет стандартный приоритет environment/command-line providers.

## Неизвестные и некорректные поля

WebAssistant вручную читает только принадлежащие ему известные ключи. Неизвестные ключи обычно остаются в общей `IConfiguration` и не создают нового поведения WebAssistant сами по себе.

Исключение — запрещённые способы изменения listener:

- непустые `urls`;
- непустые `http_ports`;
- непустые `https_ports`;
- непустой `Kestrel:Endpoints`.

Они приводят к отказу запуска.

Некорректные известные значения `Port`, `Cors` или filesystem map также завершают соответствующую загрузку конфигурации с закрытием при неопределённости, а не заменяются скрытым fallback.

## Совместимость и изменение схемы

У runtime configuration model нет скрытого migration engine и нет отдельного номера версии JSON-структуры. Изменение имени, типа, default, диапазона, приоритета provider или смысла поля является семантическим изменением и должно в одном transaction обновлять:

- runtime/binding;
- `build/common/default-appsettings.json`, если меняется default;
- этот документ;
- `docs/appsettings.schema.json`;
- актуальные обзорные документы, которых касается изменение;
- candidate contract/conformance, если изменение входит в их область.

Accepted immutable contract/conformance не переписываются задним числом.
