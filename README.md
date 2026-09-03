# WebAssistant

WebAssistant — локальная machine-wide служба для browser-facing операций с возможностями рабочей станции.

Текущий scanner module предоставляет versioned `/v1` API, raw PDF transport, явный выбор устройства и единый physical-acquisition lock. Product root находится в `webassist/` и остаётся автономно переносимым как самостоятельный проект.

Поддерживаемые платформы: Windows 10+ и ALT Linux. Runtime: .NET 10 / ASP.NET Core.

Runtime configuration загружается из JSON. CORS по умолчанию выключен и включается только явным allowlist. Для будущих filesystem operations предусмотрен configured root directory и отдельная path-security boundary; browser-facing filesystem routes в текущем baseline отсутствуют.

Продуктовая документация: [`webassist/README.md`](webassist/README.md) и [`webassist/docs/api.md`](webassist/docs/api.md).

Текущая accepted baseline pair:
- [`contracts/webassistant-contract-v0.1.json`](contracts/webassistant-contract-v0.1.json)
- [`contracts/webassistant-conformance-v0.1.json`](contracts/webassistant-conformance-v0.1.json)

## Repository governance

Repository policy задаётся в [`repo-policy.json`](repo-policy.json) и исполняется permanent [`.github/workflows/repo-guard.yml`](.github/workflows/repo-guard.yml) в blocking `check-pr` mode. Workflow закреплён за exact repo-guard commit; accepted contract/conformance pair, обязательные repository paths и автономность `webassist/` являются частью исполняемой границы.

Каждый обычный PR объявляет `ChangeIntent`. Канонический блок находится в [`.github/PULL_REQUEST_TEMPLATE.md`](.github/PULL_REQUEST_TEMPLATE.md); Issue form — в [`.github/ISSUE_TEMPLATE/change-intent.yml`](.github/ISSUE_TEMPLATE/change-intent.yml). Минимальные обязательные поля intent: `change_type`, `scope` и `anchors.affects`; budgets, `must_touch`, `must_not_touch` и `expected_effects` уточняют исполняемую форму изменения.

Изменение governance paths требует отдельного `GovernanceGrant` в связанной Issue. Grant в PR не считается доверенным источником. Broad root policy relaxation не является штатным способом разработки.
