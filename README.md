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

Repository policy задаётся в [`repo-policy.json`](repo-policy.json) и исполняется `repo-guard` в blocking mode. Accepted contract/conformance pair, обязательные repository paths и автономность `webassist/` являются частью этой исполняемой границы.

Permanent merge-readiness check находится в [`.github/workflows/repo-guard.yml`](.github/workflows/repo-guard.yml). Workflow запускает exact-pinned `netkeep80/repo-guard@063169d658b2915392f42544fed260d07380a4cd` в `mode: check-pr` и `enforcement: blocking`; governance failure или cancelled run не являются допустимым merge-ready состоянием.

Каждый обычный PR объявляет `ChangeIntent`. Канонический блок находится в [`.github/PULL_REQUEST_TEMPLATE.md`](.github/PULL_REQUEST_TEMPLATE.md); Issue form — в [`.github/ISSUE_TEMPLATE/change-intent.yml`](.github/ISSUE_TEMPLATE/change-intent.yml). Минимальные обязательные поля intent: `change_type`, `scope` и `anchors.affects`; budgets, `must_touch`, `must_not_touch` и `expected_effects` уточняют исполняемую форму изменения.

Изменение governance paths требует отдельного `GovernanceGrant` в связанной Issue. Grant в PR не считается доверенным источником. Broad root policy relaxation не является штатным способом разработки.
