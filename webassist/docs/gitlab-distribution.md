# GitLab CI: ALT Linux immutable release

Этот документ описывает только переносимый контракт продукта. Все имена
инфраструктуры, адреса и значения CI/CD-переменных задаются **во внешнем
GitLab-проекте** после копирования корня продукта.

## Подготовка

1. Docker runner должен иметь доступ к образу
   `${CI_DOCKER_HUB}/webassistant-builder:${WEBASSISTANT_BUILDER_TAG}`.
   Образ готовится отдельно: .NET SDK 10, bash, jq, zip, coreutils,
   tar, ssh-client и необходимые средства NuGet. CI не скачивает SDK.
2. Используется **уже существующая** групповая переменная
   `CI_GITLAB_API_ACCESS_TOKEN` с GitLab API credential, у которого есть
   права чтения/создания CI/CD variables выбранного проекта.
   Дополнительный project access token не требуется; основной build job
   этот API-токен не использует.
3. На защищённой основной ветке запускает manual job
   `create-gitlab-variables`: создаются только отсутствующие project
   variables со значением `null`, `protected=true`, `expanded=true`.
   SSH-параметры наследуются из групповых переменных; отдельные project
   variables для SSH и `known_hosts` не создаются.
4. Администратор заменяет заглушки через Settings → CI/CD → Variables
   целевого GitLab-проекта. Доступ к registry и runner задаётся извне.

| Переменная | Назначение |
|---|---|
| `CI_DISTRIBUTION_NAME` | Имя продукта и каталога, не `CI_PROJECT_NAME` |
| `CI_NETWORK_DRIVE_PATH` | Абсолютный путь к storage на SSH-хосте |
| `CI_PROMOTION_PROJECT_PATH` | Зарезервировано для будущей promotion-конфигурации |

Имя builder image зафиксировано в продуктовой pipeline: `webassistant-builder`.
Оно не строится из `CI_DISTRIBUTION_NAME`, и отдельная переменная для имени
образа не создаётся. Тег по умолчанию — `dotnet-10.0.403-v1`.

**Групповые SSH-переменные**: `CI_SSH_HOST` — адрес принимающего SSH-хоста,
`CI_SSH_USER` — пользователь, `CI_SSH_ID_RSA` — приватный ключ,
`CI_SSH_ID_RSA_SECRET` — парольная фраза к зашифрованному ключу.
Publication script использует `ssh-agent` и `SSH_ASKPASS` для
неинтерактивной загрузки ключа. Дублировать эти значения в project variables
не требуется. Групповой хост должен иметь подключённый внешний storage,
а пользователь — права записи.

Проверка SSH host key использует штатное хранилище OpenSSH
`known_hosts` и `StrictHostKeyChecking=accept-new`. Это не требует
отдельной project variable. Новый ключ принимается при первом
подключении, но последующее изменение сохранённого ключа отклоняется.
На одноразовом runner доверие первого подключения (TOFU) не является
криптографическим доказательством личности сервера; для строгой
проверки можно подготовить доверенные ключи стандартными средствами
на самом runner.

`CI_DOCKER_HUB` и доступ к registry задаются извне. Переменная
`CI_GITLAB_API_ACCESS_TOKEN` наследуется из настроенных GitLab group
variables; bootstrap-задача не создаёт и не изменяет этот credential.

## Сборка и публикация

Job `build-altlinux-release` запускается на основной ветке:

1. Проверяет `VERSION`, `CI_DISTRIBUTION_NAME` и SDK 10.
2. Запускает тесты immutable release.
3. Вызывает только canonical `build/linux/package.sh`,
   передавая `WEBASSISTANT_SOURCE_SHA=$CI_COMMIT_SHA`;
   online SDK bootstrap запрещён.
4. Находит ровно один ZIP с учётом допустимого custom installer basename.
5. Проверяет ZIP, `.sha256`, `.provenance.json` и формирует
   `products/<product>/releases/<VERSION>/manifest.json`.
6. Проверяет SHA-256, размер, source revision и относительные пути.
7. Передаёт ровно те же bytes через SSH tar stream; на принимающей стороне
   повторно проверяет SHA каждого из четырёх файлов.
8. На storage-host копирует точные файлы из временного каталога
   в новый каталог `releases/<VERSION>/`, повторно проверяет структуру,
   все SHA-256 и полное совпадение с staging, затем удаляет staging
   (`copy → verify → delete`). Файловое переименование **не считается
   атомарной операцией**. При уже опубликованной идентичной версии
   выполняется no-op; при любых отличиях — FAIL CLOSED.

В manifest `altlinux-x64` является ключом совместимости продукта,
`linux-x64` — техническим .NET RID. Все пути внутри manifest
относительные; инфраструктурные значения в нём отсутствуют.

Во внешнем storage относительный каталог релиза:
`shared/<product>-build/products/<product>/releases/<VERSION>/`.
GitLab Job Artifacts для installer не создаются.

**Важная семантика публикации:** наличие каталога
`releases/<VERSION>/` не доказывает завершения копирования. При
`copy → delete` конечный каталог может временно содержать лишь
часть файлов; если копирование оборвалось, частичный каталог остаётся.
Потребитель (будущий promotion pipeline) должен ждать успешного
завершения `build-altlinux-release` и **самостоятельно проверять
manifest и SHA-256 каждого артефакта**, а не доверять существованию
каталога. Повторная публикация с отличающимися/неполными байтами
отклоняется; удаление частичного каталога требует ручного анализа.

При прерывании транзакции может остаться `.publish-lock` — прежде
чем удалять его, нужно проверить staging и конечный каталог.
Обновление promotion descriptor, release index и channel pointers
остаётся отдельным этапом, и channel pointer публикуется последним.

## Локальная проверка

На Linux с bash, jq и coreutils:

    bash tests/distribution-release.test.sh

Тестируются сборка, верификация, custom installer basename,
повреждённые байты, лишние файлы, повторная идентичная публикация
и запрет перезаписи. Это не заменяет проверку реального GitLab
builder, сетевой ФС и целевой ALT Linux системы.
