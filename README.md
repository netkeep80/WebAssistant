# WebAssistant

WebAssistant — локальная machine-wide служба для browser-facing операций с возможностями рабочей станции.

Текущий scanner module предоставляет versioned `/v1` API, raw PDF transport, явный выбор устройства и единый physical-acquisition lock. Product root находится в `webassist/` и остаётся автономно переносимым как самостоятельный проект.

Поддерживаемые платформы: Windows 10+ и ALT Linux. Runtime: .NET 10 / ASP.NET Core.

Runtime configuration загружается из JSON. CORS по умолчанию выключен и включается только явным allowlist. Для будущих filesystem operations предусмотрен configured root directory и отдельная path-security boundary; browser-facing filesystem routes в текущем baseline отсутствуют.

Продуктовая документация: [`webassist/README.md`](webassist/README.md) и [`webassist/docs/api.md`](webassist/docs/api.md).

Текущая accepted baseline pair:
- [`contracts/webassistant-contract-v0.1.json`](contracts/webassistant-contract-v0.1.json)
- [`contracts/webassistant-conformance-v0.1.json`](contracts/webassistant-conformance-v0.1.json)
