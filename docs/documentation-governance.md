# Управление актуальной документацией WebAssistant

Этот документ задаёт карту текущей документационной поверхности и правила полного аудита документации при изменениях WebAssistant.

## Карта актуальной документации

| Область | Актуальные документы | Основные источники поведения | Блокирующая связь |
| --- | --- | --- | --- |
| Обзор продукта и текущая capability surface | `README.md`, `webassist/README.md` | product code, build/package entrypoints, current accepted authority, candidate v0.3 | stale-doc tests + PR audit |
| REST API, scanner/filesystem/diagnostics/CORS | `webassist/docs/api.md` | `webassist/src/WebAssistant/Http/**`, scanner/filesystem application services | repo-guard cochange для известных API surfaces + tests |
| Runtime/package configuration | `webassist/docs/configuration.md`, `webassist/docs/appsettings.schema.json`, соответствующие разделы `webassist/README.md` | `WebAssistantRuntimeOptions.cs`, `FileSystemRootRegistry.cs`, `build/common/default-appsettings.json` | несколько независимых blocking cochange rules: одного обновлённого документа недостаточно |
| Windows installation/service | `webassist/docs/windows-service.md`, `webassist/docs/installation-guide.md` | Windows package/WiX/service implementation | targeted tests + document audit |
| Linux installation/service | `webassist/docs/linux-service.md`, `webassist/docs/installation-guide.md` | Linux package/install/systemd implementation | targeted tests + document audit |
| Scanner settings schema | `webassist/docs/scanner-settings.schema.json`, scanner settings section `webassist/docs/api.md` | scanner settings DTO/handlers/adapters | existing cochange + tests |
| Dependency provenance | `webassist/vendor/naps2/README.md` | repository-owned NAPS2 package/provenance | dependency tests + document audit |
| Язык документации | `webassist/docs/documentation-language-policy.md` | documentation policy | stale-language tests + PR audit |
| Candidate semantic authority | `contracts/webassistant-contract-v0.3.json`, `contracts/webassistant-conformance-v0.3.json` | semantic/runtime changes in candidate scope | governance grant + targeted cochange/evidence |
| Current accepted authority | пути из `contract_conformance.current` в `repo-policy.json`; сейчас v0.2.1 | accepted contract/conformance pair | immutable history; не переписывается обычным feature PR |

## Что не считается текущим описанием продукта

`docs/superpowers/**` содержит исторические планы и design records конкретных development transactions. Они сохраняют исторический контекст, но не являются нормативным описанием текущего продукта и не должны искусственно обновляться под каждое новое состояние.

Старые accepted contract/conformance пары также являются исторической authority своего момента времени. Их несовпадение с новой candidate semantics не является stale documentation: accepted документы не переписываются задним числом.

## Обязательный полный аудит

Для любого изменения поведения, API, runtime configuration, architecture, packaging/dependency policy, installer/service semantics, scanner/filesystem semantics, CI/acceptance или governance автор PR обязан:

1. определить все строки этой карты, которых касается изменение;
2. проверить каждый актуальный документ в этих строках, даже если Issue назвала только один файл;
3. обновить все документы, где старое утверждение стало неверным или неполным;
4. явно перечислить в PR проверенные документы, реально обновлённые документы и проверенные, но не затронутые документы с кратким основанием;
5. синхронизировать candidate contract/conformance, если изменение относится к их semantic surface;
6. не менять accepted immutable authority задним числом;
7. при изменении accepted authority выполнять отдельную promotion transaction.

Фраза «документ не был указан в Issue» не является основанием пропустить аудит.

## Что блокируется автоматически

Текущий закреплённый repo-guard поддерживает directed `cochange_rules` с `must_change_any`, но ещё не поддерживает semantic all-or-none groups. Поэтому там, где связь точная и критичная, требование «обновить все» выражается несколькими независимыми правилами с одинаковым `if_changed` и singleton `must_change_any`.

Например, изменение runtime configuration model одновременно требует:

- `webassist/docs/configuration.md`;
- `webassist/docs/appsettings.schema.json`;
- `webassist/README.md`;
- candidate `contracts/webassistant-contract-v0.3.json`;
- candidate `contracts/webassistant-conformance-v0.3.json`.

Обновление только одного из этих файлов не удовлетворяет остальные правила и блокируется repo-guard.

Для широких файлов, где один path содержит несколько независимых смыслов, принудительный touch всех возможных документов создавал бы ложные изменения. Там обязательность аудита обеспечивается PR checklist, текущей картой и stale-document tests. Если связь становится достаточно точной и повторяемой, её следует повышать до blocking cochange rule.

## Конфигурация

Каноническое описание JSON-конфигурации находится в `webassist/docs/configuration.md`, а machine-readable schema — в `webassist/docs/appsettings.schema.json`.

Любое изменение runtime configuration model, defaults, package-time selection, provider priority или validation должно рассматриваться как изменение этой пары и соответствующих обзорных/candidate документов.

## Accepted и candidate

`repo-policy.json` определяет current accepted contract/conformance. Accepted pair является неизменяемой исторической authority.

Candidate документы могут обновляться вместе с реализацией при наличии GovernanceGrant. Когда новая semantics должна стать current accepted authority, выполняется отдельная promotion transaction: создаётся/проверяется новая accepted pair, current pointer переводится на неё, а предыдущая accepted pair остаётся неизменной.

## Проверка самого механизма

`tests/core/DocumentationGovernanceTests.cs` проверяет:

- наличие карты и configuration reference/schema;
- соответствие schema безопасному default;
- наличие независимых blocking cochange obligations;
- отрицательный witness: изменение runtime model плюс только одного документа оставляет другие обязательства неудовлетворёнными;
- обязательный раздел полного аудита в PR template;
- отсутствие уже известных stale filesystem methods в product README.

Дополнительные предметные stale assertions остаются в `tests/core/CurrentStateDocumentationTests.cs` и специализированных contract tests.
