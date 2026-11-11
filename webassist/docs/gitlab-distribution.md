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
   Если отсутствует доверенный known_hosts, он создаётся с типом **File**.
   Приватный клиентский ключ уже наследуется из групповых переменных.
4. Администратор заменяет заглушки через Settings → CI/CD → Variables
   целевого GitLab-проекта. Доступ к registry и runner задаётся извне.

| Переменная | Назначение |
|---|---|
| `CI_DISTRIBUTION_NAME` | Имя продукта и каталога, не `CI_PROJECT_NAME` |
| `CI_NETWORK_DRIVE_PATH` | Абсолютный путь к storage на SSH-хосте |
| `CI_PROMOTION_PROJECT_PATH` | Зарезервировано для будущей promotion-конфигурации |
| `CI_RELEASE_SSH_KNOWN_HOSTS` | Проверенный публичный SSH host key, тип **File**; временно, если нет группового `CI_SSH_KNOWN_HOSTS` |
| `CI_RELEASE_STORAGE_RENAME_VERIFIED` | Устанавливается в `1` только после физической проверки rename semantics |

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

Для проверки **подлинности SSH-сервера** нужен trusted `known_hosts`.
Скрипт сначала использует групповую `CI_SSH_KNOWN_HOSTS` (содержимое
или File variable), при её отсутствии — существующую project-scoped
`CI_RELEASE_SSH_KNOWN_HOSTS`. Если обе отсутствуют, публикация
завершается ошибкой. Публичный host key необходимо сверить по независимому
доверенному каналу. `StrictHostKeyChecking=yes` не отключается.

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
8. Публикует staging каталог без перезаписи другой версии.
   Если версия уже опубликована идентичными байтами — no-op;
   если байты отличаются — FAIL CLOSED.

В manifest `altlinux-x64` является ключом совместимости продукта,
`linux-x64` — техническим .NET RID. Все пути внутри manifest
относительные; инфраструктурные значения в нём отсутствуют.

Во внешнем storage относительный каталог релиза:
`shared/<product>-build/products/<product>/releases/<VERSION>/`.
GitLab Job Artifacts для installer не создаются.

**Внимание:** поддержка rename на сетевой ФС должна быть проверена
физически до установки `CI_RELEASE_STORAGE_RENAME_VERIFIED=1`.
В случае прерывания транзакции может остаться publication lock:
его следует исследовать вручную до очистки. Продуктовый job не
обновляет promotion descriptor, release index или channel pointers:
это отдельный downstream deployment этап.

## Локальная проверка

На Linux с bash, jq и coreutils:

    bash tests/distribution-release.test.sh

Тестируются сборка, верификация, custom installer basename,
повреждённые байты, лишние файлы, повторная идентичная публикация
и запрет перезаписи. Это не заменяет проверку реального GitLab
builder, сетевой ФС и целевой ALT Linux системы.
