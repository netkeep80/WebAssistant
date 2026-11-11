# WebAssistant

`webassist` — автономный корень продукта WebAssistant. Его содержимое можно скопировать в корень отдельного репозитория и собирать, упаковывать и устанавливать без файлов уровнем выше.

## Версия продукта

Файл `VERSION` в корне продукта — единственный сохраняемый источник истины для версии продукта. Он содержит числовой SemVer `major.minor.patch` и переносится вместе с продуктом без зависимости от `.git`, тегов или метаданных CI.

Канонические упаковщики читают это значение и формируют версионированные артефакты по фактическому базовому имени установщика:

```text
Windows: <installerBaseName>-win-x64-<VERSION>.exe
Linux:   <installerBaseName>-linux-x64-<VERSION>.zip
```

При отсутствии переопределения метаданных продукта публичное значение `installerBaseName` по умолчанию равно `WebAssistant`, поэтому обычная сборка через GitHub или вручную по-прежнему даёт:

```text
Windows: WebAssistant-win-x64-<VERSION>.exe
Linux:   WebAssistant-linux-x64-<VERSION>.zip
```

`<VERSION>` всегда означает точное содержимое файла `VERSION`. То же значение передаётся в метаданные приложения и пакета и записывается в provenance. Метаданные продукта не могут задавать или переопределять версию.

## Метаданные продукта при сборке

Windows и Linux используют один принадлежащий репозиторию `ProductMetadataResolver` и одну схему `webassistant-product-metadata/v1`. Сначала берутся публичные значения по умолчанию из `build/common/product-metadata.defaults.json`, затем при наличии применяется необязательное переопределение:

```text
src/WebAssistant/product-metadata.json
```

`product-metadata.json` и необязательный зависящий от окружения `src/WebAssistant/appsettings.json` располагаются в одном исходном каталоге, но имеют разную семантику: первый влияет только на отображаемую идентичность продукта при сборке, а второй при наличии выбирается упаковщиком как configuration seed. На Windows installer помещает active config непосредственно в `%ProgramData%\WebAssistant\config\appsettings.json`; Program Files не содержит active config. На Linux seed остаётся packaged `appsettings.json` рядом с приложением. Стандартный приоритет источников описан в [`docs/configuration.md`](docs/configuration.md). На Windows bootstrap также принимает native path с одиночными обратными слешами в строковых значениях (`C:\\...`) и нормализует его в памяти/временной копии до стандартного JSON provider; исходный файл не переписывается.

Допустимые поля переопределения:

```json
{
  "schema": "webassistant-product-metadata/v1",
  "applicationName": "Custom Web Assistant",
  "installerBaseName": "CustomWebAssistant",
  "fileDescription": "Custom Web Assistant",
  "companyName": "Example Vendor",
  "copyright": "Copyright © Example Vendor"
}
```

Все поля кроме `schema` необязательны и наследуют публичные значения по умолчанию. Неизвестные поля, другая `schema`, пустые или некорректные значения и непереносимый `installerBaseName` приводят к остановке сборки до создания канонического артефакта. Поля `applicationName`, `fileDescription` и `companyName` дополнительно не могут содержать `;`, потому что эти значения передаются в WiX через разделённый точкой с запятой `DefineConstants`; `ProductMetadataResolver` отклоняет такие входные данные до публикации и упаковки. Поля `version`, имя службы, имя исполняемого файла, пути установки и идентификаторы жизненного цикла WiX намеренно отсутствуют в контракте метаданных.

Публичный GitHub-репозиторий специально игнорирует `webassist/src/WebAssistant/product-metadata.json` через `.gitignore` корня разработки. При копировании содержимого `webassist` это правило не переносится: локальный `.gitignore` продукта не запрещает этот путь. Поэтому downstream-репозиторий GitLab может хранить собственный `src/WebAssistant/product-metadata.json` и использовать другую идентичность продукта без изменения сценариев упаковки. Различаются данные, а не код упаковки.

Фактические метаданные применяются к метаданным файла и продукта .NET, отображаемой идентичности Windows WiX/ARP, человекочитаемому описанию службы Linux и базовому имени канонических артефактов. Технические идентификаторы остаются стабильными: `WebAssistant.exe`, Windows Service `WebAssistant`, Linux unit `webassist.service`, пути установки и идентификаторы жизненного цикла пакета и bundle WiX не переименовываются через переопределение брендинга.

Provenance итогового артефакта фиксирует `metadataMode` (`defaults` или `override`), фактические `applicationName`, `installerBaseName`, `metadataInputSha256` и `effectiveMetadataSha256`. Метаданные разрешаются до публикации, упаковки и вычисления контрольной суммы; исправление брендинга после сборки не используется.

## Что работает сейчас

WebAssistant устанавливается как общесистемная служба рабочей станции:

- Windows — Windows Service `WebAssistant`;
- ALT Linux — служба systemd `webassist.service`.

Служба слушает только loopback. Адрес по умолчанию: `http://127.0.0.1:17654`. Публикация слушателя в LAN или на `0.0.0.0` не является допустимой конфигурацией времени выполнения. Запросы к любым HTTP-страницам и API также ограничены точными именами `127.0.0.1`/`localhost` в заголовке `Host`; чужие DNS-алиасы и неверный явный порт отклоняются до чтения данных (`421 invalid_local_host`). Это защищает от попытки DNS rebinding, которую не предотвращает только loopback-сокет или CORS.

Текущий модуль сканирования:

- перечисляет доступные конечные точки сканеров и позволяет явно выбрать сохраняемый `scannerId`;
- в Windows WIA и TWAIN перечисляются независимо; успешные конечные точки обеих подсистем объединяются, а частичный сбой одной подсистемы не скрывает успешную часть;
- поддерживает `auto`, явно заданные `flatbed`, `feeder` и двустороннее сканирование через единый `POST /v1/scan`;
- явно заданный `flatbed` или `feeder` не имеет скрытого fallback на другой источник;
- для `auto` есть одна узкая восстановительная семантика: если был выбран `feeder`, он оказался пуст именно во время физической операции и доступен `flatbed`, WebAssistant один раз повторяет физическую операцию через `flatbed`;
- выполняет не более одной физической операции сканирования одновременно;
- возвращает один необработанный `application/pdf`, содержащий все страницы одной физической операции;
- не использует Base64, JSON-конверт документа или ZIP/растровый конверт как транспорт результата сканирования;
- после передачи PDF вызывающей стороне не хранит завершённый документ как долговременное хранилище сканирования.

В Linux используется прямой путь SANE SDK через NAPS2 без оркестрации CLI. Вызывающая сторона владеет предпочтениями сканирования и передаёт их в каждом запросе; WebAssistant не хранит изменяемый пользовательский профиль сканера.

Операция сканирования ограничена цепочкой «получение изображения → PDF». Она сама не выполняет редактирование, объединение или разделение PDF, OCR, аннотацию, водяные знаки, выравнивание, другую смысловую обработку документа, подписание, шифрование, загрузку в бизнес-систему и не требует бизнес-аутентификации или пользовательского бизнес-профиля.

### Файловый обмен

Возможность файлового обмена работает с набором именованных логических корней из `WebAssistant:FileSystem`. Каждый элемент конфигурации сопоставляет `logicalRootName` с физическим каталогом `physicalRootPath`; публичный API принимает пути вида `<logicalRootName>/<relativePathInsideRoot>`, не хранит состояние текущего каталога и никогда не публикует физический путь хоста.

Публичные маршруты файлового обмена:

```text
GET    /v1/filesystem/roots
GET    /v1/filesystem/list
GET    /v1/filesystem/file
GET    /v1/filesystem/files
POST   /v1/filesystem/find
POST   /v1/filesystem/file
POST   /v1/filesystem/file/delete
POST   /v1/filesystem/directory
POST   /v1/filesystem/directory/delete
POST   /v1/filesystem/move
POST   /v1/filesystem/directory/move
POST   /v1/filesystem/rename
```

Загрузка, `directory/move` и `rename` сохраняют атомарную семантику `no-replace`. Пакетный file move по умолчанию также `no-replace`, но явный `overwriteExisting=true` разрешает атомарную замену существующего обычного destination file одной native rename operation; `delete+move` и `copy+delete` запрещены. Перемещение файлов и каталогов может пересекать разные logical roots только когда нижележащая файловая система поддерживает настоящий нативный атомарный rename. `rename` сохраняет родительский каталог, а `move` — фактическое basename. Проход через symlink/junction/reparse и операции с hard-link alias завершаются с закрытием при неопределённости. Самостоятельные активные расширения файлов блокируются для загрузки, скачивания и перемещения согласно политике имени файла. Скачивание всегда имеет семантику передачи непрозрачного файла как `application/octet-stream` с вложением и не превращает настроенные каталоги в дерево статических веб-ресурсов.

Принадлежащая репозиторию страница `/filesystem.html` — обычный визуальный клиент того же публичного API файлового обмена без закрытых или тестовых привилегий. Сверху расположены кнопки выбора логического корня, а левая и правая панели работают независимо и имеют собственные каталог, хлебные крошки и сортировку. Каталог открывается по щелчку, `..` поднимает на уровень вверх, загрузка выполняется одной кнопкой сразу после выбора файла, а файлы скачиваются без встроенного просмотра.

Точные REST-схемы, коды состояний и ошибок, модель возможностей сканера и политика файлового обмена описаны в [`docs/api.md`](docs/api.md). Полная конфигурация времени выполнения и пакета, значения по умолчанию, приоритет источников и различия платформ описаны в [`docs/configuration.md`](docs/configuration.md), а машиночитаемая JSON-проекция — в [`docs/appsettings.schema.json`](docs/appsettings.schema.json).

## Конфигурация времени выполнения и владение при упаковке

Конфигурация развёртывания выбирается во время упаковки одним из двух способов:

```text
src/WebAssistant/appsettings.json существует
  -> упаковщик включает точные байты этого файла

src/WebAssistant/appsettings.json отсутствует
  -> упаковщик включает build/common/default-appsettings.json
```

На Windows package-time `appsettings.json` является только installer input. MSI устанавливает его непосредственно как `%ProgramData%\WebAssistant\config\appsettings.json`; новой копии `appsettings.json` в Program Files нет. При первом переходе со старой версии legacy bytes из Program Files копируются в ProgramData preflight-стадией до MSI, а существующий persistent config не заменяется install/upgrade/repair/reinstall. Обычный uninstall сохраняет его.

Windows installer также всегда обновляет `%ProgramData%\WebAssistant\config\appsettings.example.json` из repository-owned `build/common/appsettings.example.json`. Это полный справочный пример текущей версии, а не runtime provider. Минимальный безопасный `default-appsettings.json` остаётся отдельным seed.

Конфигурация репозитория по умолчанию выключает CORS, использует платформенный каталог журналов, задаёт уровень журналирования `Information` и содержит пустую карту `WebAssistant:FileSystem`. Пустая карта означает, что файловый обмен не настроен; скрытый или платформенный корень по умолчанию не создаётся. При включении CORS разрешены только явно заданные точные HTTP/HTTPS origins; wildcard `*` не допускается. Публичный API файловой системы использует только `GET` и `POST`; межсайтовые JSON-запросы проходят обычный браузерный preflight.

Уровень диагностики меняется стандартной конфигурацией .NET без пересборки WebAssistant:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "WebAssistant": "Debug"
    }
  }
}
```

`Information` предназначен для обычной эксплуатации: итог HTTP/scanner operation, существенные предупреждения и крупные timings. `Debug` предназначен для опытной эксплуатации и расследования: он добавляет `operationId`, causal `worker.acquire/release/exit`, exact worker PID/bitness, TWAIN native stage boundaries и x86 worker self-report фактически загруженных legacy `twain_32.dll` или modern `twaindsm.dll` и vendor DS modules. `Trace` оставляет включёнными наиболее подробные безопасные технические события и должен использоваться кратковременно. Для гарантированного применения изменённого persistent `appsettings.json` достаточно перезапустить службу; специальная диагностическая сборка не нужна.

В `Debug` и `Trace` `GET /v1/diag/info` даёт package/runtime inventory, а causal `operationId ↔ workerPid ↔ DSM/DS ↔ native stage` восстанавливается по собственному daily log WebAssistant без ручных PowerShell/cmd/shell-команд. Host-side worker snapshot не считается доказательством ownership конкретной операции и не заменяет x86 worker self-report. Обычный `Information` не публикует эти подробности.

Пример конфигурации нескольких корней:

```json
{
  "WebAssistant": {
    "FileSystem": {
      "archive": "D:/archive",
      "import": "//192.168.1.20/import"
    }
  }
}
```

На Windows physical root может содержать пробелы и круглые скобки. Для ручного `appsettings.json` рекомендуется `C:/Program Files (x86)/Example App`; native Windows path с обратными слешами также допустим, но каждый `\\` в исходном JSON должен быть записан как `\\\\`. Улучшение copy-paste без ручного экранирования отслеживается в #266.

Имя логического корня соответствует `[A-Za-z0-9][A-Za-z0-9._-]*`; `.` и `..` запрещены, а имена, различающиеся только регистром, считаются конфликтом конфигурации. Каждый корень получает отдельную нативную область полномочий. Недоступность одного физического каталога не отключает остальные корни, сканирование или `/v1/health`. Браузер и API не могут менять физические каталоги корней.

Для чтения и ZIP на Linux допускаются только обычные файлы. Открытие выполняется в неблокирующем режиме, а тип проверяется по уже открытому дескриптору: FIFO без писателя, сокеты и другие специальные объекты не могут удерживать поток в ожидании открытия; они отвергаются без передачи содержимого.

Ключ `WebAssistant:FileSystem:RootDirectory` с допустимым абсолютным путём задаёт обычный логический корень с публичным именем `RootDirectory`. При переходе с однокорневой конфигурации имя нужного корня выбирается явно, например `WebAssistant:FileSystem:archive = D:/archive`. Существующее имя сохраняется до изменения конфигурации.

WebAssistant пишет собственные технические события в суточные файлы журналов. Даже при `Debug` и `Trace` в журналы не записываются байты PDF, Base64, изображения или содержимое страниц и документов, произвольное содержимое пользовательских файлов, пароли, токены и секреты. Текущая служба сама не выполняет автоматическое удаление старых суточных журналов.

## Сборка канонических артефактов

Сценарии упаковки определяют корень продукта относительно собственного расположения и не зависят от текущего рабочего каталога. Оба упаковщика используют единый контракт фактических метаданных продукта, описанный выше.

### Windows

```bat
build\windows\package.bat
```

Результат по умолчанию находится в `artifacts/windows-x64/`:

```text
<installerBaseName>-win-x64-<VERSION>.exe
<installerBaseName>-win-x64-<VERSION>.exe.sha256
<installerBaseName>-win-x64-<VERSION>.exe.provenance.json
```

При публичных значениях по умолчанию `<installerBaseName>` равен `WebAssistant`. Упаковщик собирает самодостаточный `win-x64` payload, внутренний MSI и итоговый WiX 7 Burn EXE. Внутренний MSI является промежуточным результатом сборки; пользовательским установочным артефактом является только версионированный EXE.

Интерактивный установщик Windows по умолчанию использует принадлежащую репозиторию русскую локализацию `build/windows/installer/localization/ru-RU.wxl`. Единственный созданный вручную источник графической идентичности — `build/windows/installer/branding/webassistant-icon.svg`. Во время канонической упаковки принадлежащий репозиторию генератор воспроизводимо строит из него ICO с кадрами 16×16, 32×32, 48×48 и 256×256 для bundle EXE / Installed Apps, а также PNG 64×64 для `WixStandardBootstrapperApplication`. Локальный графический редактор или внешний конвертер, зависящий от машины, для сборки не требуется. `product-metadata.json` управляет только текстовой отображаемой идентичностью; `WebAssistant.exe`, Windows Service `WebAssistant`, `netkeep80.WebAssistant.Bundle` и `netkeep80.WebAssistant` остаются стабильными техническими идентификаторами жизненного цикла.

Для машины сборки требуется .NET SDK 10 и инструментарий WiX, управляемый проектами установщика в репозитории. Canonical `build\windows\package.bat` один раз разрешает конкретный `dotnet.exe`, содержащий SDK 10: сначала проверяет стандартный `%ProgramFiles%\\dotnet\\dotnet.exe`, затем доступные кандидаты из текущего `PATH`; если SDK 10 отсутствует, выполняет `winget install Microsoft.DotNet.SDK.10` и после установки снова проверяет стандартный каталог напрямую, не ожидая обновления `PATH` текущего процесса. Выбранный executable передаётся в `package.ps1` через `WEBASSISTANT_DOTNET_EXE` и остаётся единственным .NET SDK authority для всех `run/publish/build` и provenance внутри этой packaging transaction. На целевой рабочей станции заранее установленный .NET Runtime или SDK не требуется.

### Linux

```bash
./build/linux/package.sh
```

Результат по умолчанию находится в `artifacts/linux-x64/`:

```text
<installerBaseName>-linux-x64-<VERSION>.zip
<installerBaseName>-linux-x64-<VERSION>.zip.sha256
<installerBaseName>-linux-x64-<VERSION>.zip.provenance.json
```

При публичных значениях по умолчанию `<installerBaseName>` равен `WebAssistant`. Упаковщик Linux публикует самодостаточное приложение `linux-x64` и кладёт в ZIP `VERSION`, `install.sh`, `uninstall.sh`, `webassist.service` и принадлежащий пакету `appsettings.json`.

Для машины сборки требуется .NET SDK 10. `package.sh` ищет его в следующем порядке:

1. `WEBASSISTANT_DOTNET_ROOT`;
2. `toolchain/dotnet/linux-x64/`;
3. системный .NET SDK 10;
4. если разрешена сетевая загрузка — официальный `dotnet-install.sh`.

Строго сетевую загрузку можно запретить:

```bash
WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP=0 ./build/linux/package.sh
```

Это не является гарантией полностью автономной сборки без сети: замыкание зависимостей NuGet для полностью автономной среды сборки остаётся отдельной задачей.

## Установка

### Windows

Администратор запускает канонический артефакт:

```text
<installerBaseName>-win-x64-<VERSION>.exe
```

При публичных значениях по умолчанию это `WebAssistant-win-x64-<VERSION>.exe`. Установщик запрашивает повышение прав, выполняет общесистемную установку в Program Files, регистрирует и автоматически запускает Windows Service `WebAssistant`, а также регистрирует фактическую отображаемую идентичность продукта в Installed Apps / Programs and Features. Подробности: [`docs/windows-service.md`](docs/windows-service.md).

### ALT Linux 10.1

Администратор распаковывает:

```text
<installerBaseName>-linux-x64-<VERSION>.zip
```

При публичных значениях по умолчанию это `WebAssistant-linux-x64-<VERSION>.zip`. Из распакованного каталога запускается:

```bash
sudo ./install.sh
```

Установщик использует упакованную конфигурацию без замены. Отсутствующие системные зависимости дистрибутива могут устанавливаться штатно через apt-rpm. Подробности: [`docs/linux-service.md`](docs/linux-service.md).

## Каноническая PDF-инструкция

Редактируемый источник пользовательской инструкции находится внутри автономного корня продукта:

```text
docs/installation-guide.md
```

Машинные доказательства, полученные после заморозки исходного состояния, передаются отдельно через строгий манифест `docs/installation-guide/evidence.schema.json`. Манифест связывает каждую процедуру и снимок экрана с точными `sourceSha`, `VERSION`, именами установочных артефактов и их SHA-256. Фикстурные доказательства предназначены только для проверки механики и не могут использоваться как окончательные реальные доказательства.

Финальная инструкция строится только для точного замороженного SHA исходного состояния:

```bash
WEBASSISTANT_SOURCE_SHA=<exact-frozen-main-sha> \
  docs/installation-guide/build.sh \
  --mode final \
  --evidence <final-evidence.json> \
  --output artifacts/installation-guide
```

Результат:

```text
artifacts/installation-guide/WebAssistant-Installation-Guide.pdf
```

Режим `final` с закрытием при неопределённости требует полного набора доказательств именно для ALT Linux 10.1 и всех обязательных точек захвата Windows/ALT. `verify.sh` повторно проверяет идентичность доказательств, структуру и текст PDF и визуализирует каждую страницу в растровое изображение через закреплённый инструментарий Poppler. Редактируемый источник и инфраструктура сборки/проверки являются нормативными материалами репозитория; реальные снимки экрана после заморозки и сгенерированный PDF в репозиторий не коммитятся.

Закреплённый инструментарий документа описан в `docs/installation-guide/toolchain.env` и не использует изменяемый идентификатор `latest`.

## GitLab CI/CD: сборка для ALT Linux

Каталог `webassist/` переносится целиком в корень самостоятельного GitLab-репозитория. Его `.gitlab-ci.yml` не зависит от GitHub API, GitHub SHA и файлов выше корня продукта. `CI_COMMIT_SHA` в provenance означает **коммит самого GitLab-репозитория**, из которого выполнена сборка.

Первый этап — только `altlinux-x64` (дистрибутивная платформа); технический .NET RID — `linux-x64`. Windows GitLab job пока не предусмотрен.

### Порядок первичной настройки

1. Убедиться, что в GitLab registry доступен заранее собранный образ `${CI_DOCKER_HUB}/webassistant-builder:${WEBASSISTANT_BUILDER_TAG}`. В продуктовой pipeline закреплено имя `webassistant-builder` и тег `dotnet-10.0.403-v1` по умолчанию. В образе должны присутствовать .NET SDK 10, bash, zip, jq, coreutils, tar, SSH-клиент и другие инструменты, проверяемые job. SDK из сети при сборке не скачивается.
2. В Settings → CI/CD → Variables проверить доступность уже выданной **групповой** переменной `CI_GITLAB_API_ACCESS_TOKEN`. Её токен должен иметь права доступа к Project Variables API текущего проекта (в том числе создание переменных). Токен доступен через групповые переменные GitLab; его значение не выводится в лог.
3. На защищённой основной ветке открыть Build → Pipelines и запустить manual job `create-gitlab-variables`. Он через `CI_API_V4_URL` создаёт **только отсутствующие** project-scoped variables со значением `null`, `protected=true` и `expanded=true`. Имеющиеся значения не заменяются.
4. В Settings → CI/CD → Variables текущего GitLab-проекта отредактировать созданные заглушки по таблице ниже. Настроить доступ сборочного job к Docker runner и registry.
5. Один раз запустить manual job `prepare-release-storage` (также после смены диска или пути к storage). Он через существующее SSH-подключение создаёт только `shared/<CI_PROJECT_NAME>-build` с владельцем `CI_SSH_USER` и правами `750`, используя `sudo -n install -d` на SSH-хосте. Каталоги `CI_NETWORK_DRIVE_PATH` и `shared` должны уже существовать; для команды требуется заранее разрешённый беспарольный `sudo`, иначе задача завершится ошибкой. Пользовательские каталоги релизов вручную не создаются.
6. Выполнить `build-altlinux-release` на основной ветке. Сборка, создание manifest и публикация выполняются в одном job. Внешнее хранилище рассматривается как система с семантикой `copy → delete`; публикация выполняется как `copy → verify → delete staging`.

| Project variable | Что задать |
|---|---|
| `CI_NETWORK_DRIVE_PATH` | Абсолютный путь к storage **на SSH-хосте**, отдельная project variable |
| `CI_PROMOTION_PROJECT_PATH` | Путь отдельного promotion-контура (зарезервирован для следующего этапа) |

**SSH наследуется из групповых переменных**: `CI_SSH_HOST` — сервер, `CI_SSH_USER` — пользователь, `CI_SSH_ID_RSA` — приватный ключ, `CI_SSH_ID_RSA_SECRET` — парольная фраза, если ключ зашифрован. Сборка использует эти групповые значения непосредственно. Групповой `CI_SSH_HOST` должен указывать именно на хост, где смонтировано внешнее build storage, а `CI_SSH_USER` должен иметь права на публикацию.

SSH использует штатное хранилище `known_hosts` OpenSSH. Параметр `StrictHostKeyChecking=accept-new` автоматически принимает ключ нового сервера при первом соединении, но **отклоняет изменившийся ключ** при следующих подключениях. На одноразовом GitLab Runner запись `known_hosts` может не сохраняться между jobs — тогда первый контакт не удостоверяет сервер по заранее известному ключу. Для более строгой проверки можно централизованно подготовить стандартный `known_hosts` в окружении Runner без изменения продуктового CI.


`CI_NETWORK_DRIVE_PATH` настраивается именно на уровне проекта. Переменные `CI_DOCKER_HUB` и доступ к registry наследуются из GitLab-инфраструктуры.

### Что делает pipeline

Имя каталога продукта и идентификатор в manifest берутся из встроенной переменной GitLab `CI_PROJECT_NAME` (имя каталога проекта в URL). `create-gitlab-variables` и `prepare-release-storage` — отдельные ручные служебные задачи. `build-altlinux-release` сначала проверяет `CI_PROJECT_NAME`, `CI_NETWORK_DRIVE_PATH`, групповые `CI_SSH_HOST`, `CI_SSH_USER`, `CI_SSH_ID_RSA`, а также GitLab job identifiers. Пустые значения, заглушки `null`/`masked_null` и некорректные адреса отклоняются до тестов и упаковки. Проверка выполняется локально, без подключения к SSH-хосту; парольная фраза `CI_SSH_ID_RSA_SECRET` может отсутствовать. После проверки основная сборка вызывает единственный канонический producer `build/linux/package.sh` с `WEBASSISTANT_SOURCE_SHA=$CI_COMMIT_SHA` и `WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP=0`, проверяет ZIP, SHA-256 и provenance, создаёт относительный `manifest.json` и передаёт точные байты через SSH на внешний storage.

Итоговый путь на SSH-хосте относительно `CI_NETWORK_DRIVE_PATH`:

    shared/<product>-build/products/<product>/releases/<VERSION>/
      manifest.json
      altlinux-x64/
        <installerBaseName>-linux-x64-<VERSION>.zip
        <installerBaseName>-linux-x64-<VERSION>.zip.sha256
        <installerBaseName>-linux-x64-<VERSION>.zip.provenance.json

Релиз публикуется последовательностью **copy → verify → delete staging**: каталог версии создаётся, копируются точные файлы, затем проверяются структура и SHA-256, после чего удаляется временный каталог. Готовность релиза определяется успешным завершением job и проверкой байтов. До этого каталог версии может быть неполным; будущий promotion pipeline должен дополнительно проверить manifest и артефакты. При оборванной публикации частичная версия остаётся для ручного анализа.

Каждая версия immutable. Повторная публикация допускается только при **полном совпадении байтов**; любая разница даёт ошибку. Установщик публикуется непосредственно во внешнее хранилище. Deployment/promotion и переключение каналов релизов выполняются отдельным pipeline и **не** пересобирают продукт.

Переопределение `src/WebAssistant/product-metadata.json` (если есть во внешнем GitLab-репозитории) меняет отображаемую идентичность и basename установщика через общий `ProductMetadataResolver`; отдельного packager для GitLab нет.

Подробности протокола публикации, диагностики блокировки и локального тестирования — в [`docs/gitlab-distribution.md`](docs/gitlab-distribution.md).

## Зависимость NAPS2 SDK

Исправленный SDK хранится под отдельной идентичностью `WebAssistant.NAPS2.Sdk` в `vendor/nuget`. Сборка продукта не маскирует его под официальный `NAPS2.Sdk` той же версии. Происхождение, фиксированная идентичность пакета и способ воспроизводимой пересборки описаны в `vendor/naps2/README.md`.

