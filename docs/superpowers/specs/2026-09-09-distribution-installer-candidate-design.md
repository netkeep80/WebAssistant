# Distribution installer candidate — design

## Статус

Этот design фиксирует одобренную архитектуру текущей distribution/install/release работы.

Implementation plan: `docs/superpowers/plans/2026-09-09-distribution-installers-implementation.md`.

База:

```text
main = 8c03a2499c071d68ad78a0133afbece47c1ec199
webassist/VERSION = 0.3.10
accepted contract = webassistant-contract/v0.2
accepted conformance = webassistant-conformance/v0.2
```

Accepted v0.2 остаётся неизменяемой исторической authority. Текущая работа создаёт отдельный distribution-only semantic candidate. Scanner/API redesign #161–#164 отложен и не входит в эту транзакцию.

Canonical tracker owner: #165. Distribution authority: #154. Semantic gate: #160 Transaction A.

## Цель

Свести все способы сборки к двум repository-owned canonical producer implementations и получить два настоящих versioned installer artifacts, которые собираются один раз, тестируются как exact immutable bytes и этими же bytes публикуются в release.

Главный invariant:

```text
5 execution scenarios
        ↓
2 canonical producers
        ↓
2 versioned canonical installer artifacts
        ↓
exact immutable artifact acceptance
        ↓
same exact bytes in release
```

И:

```text
manual Windows == GitHub Windows
manual ALT == GitHub ALT == future GitLab ALT
```

Равенство означает одинаковые artifact format, filename/VERSION semantics, config ownership, install/uninstall semantics и target runtime boundary. CI может отличаться только оркестрацией окружения, transport/cache и publication.

## Scope

В текущую transaction входят:

- distribution-only candidate contract/conformance;
- Windows canonical installer #155;
- ALT Linux 10.1 canonical installer #156;
- exact artifact reuse #7;
- installer-based final acceptance #5;
- installation PDF #158;
- GitHub Release #157.

Не входят:

- scanner identity/capability research #161–#164;
- GitLab builder/container infrastructure #159;
- clean offline build #94;
- branch protection/ruleset hardening #3/#25.

Текущая accepted scanner/API semantics v0.2 сохраняется без изменений на всём протяжении installer transaction.

## Candidate semantic delta

Distribution candidate должен добавить к preserved v0.2 semantics следующие нормативные требования.

### Canonical producers

Windows:

```text
webassist/build/windows/package.bat
  -> WebAssistant-win-x64-<VERSION>.exe
```

ALT Linux 10.1:

```text
webassist/build/linux/package.sh
  -> WebAssistant-linux-x64-<VERSION>.zip
```

`<VERSION>` — exact content `webassist/VERSION`.

CI-specific product publish/package implementations запрещены.

### Version identity

Для каждого installer:

```text
filename VERSION
== webassist/VERSION
== application metadata VERSION
== installer/package metadata VERSION
== release/tag VERSION
```

Mismatch приводит к fail-closed до install/release.

### Configuration ownership

Package-time rule одинаков для обеих ОС:

```text
src/WebAssistant/appsettings.json exists
  -> include exact file

absent
  -> canonical package producer generates safe default
```

После формирования final installer config является частью immutable package payload. Installer не генерирует другой config и не изменяет packaged config.

Это означает исправление текущего Linux gap: `install.sh` не должен безусловно переписывать `appsettings.json`. Если canonical ZIP не содержит config, installation fails as malformed package.

### Target self-containment

На target workstation не требуются:

- .NET Runtime;
- .NET SDK;
- NuGet;
- compiler/build tools.

Это не означает offline build environment. #94 остаётся deferred.

Для ALT Linux допустимы distro-owned runtime dependencies через штатный apt-rpm, включая SANE/backends, GTK, ICU и аналогичные системные пакеты. Эти RPM не vendoring'ятся внутрь ZIP только ради offline installation.

## Windows installer

Canonical artifact:

```text
WebAssistant-win-x64-<VERSION>.exe
```

Approved implementation direction: WiX-based Windows installer/bundle с использованием стандартных Windows Installer primitives для machine-wide lifecycle.

Обязательное observable behavior:

- один user-facing EXE;
- elevation/UAC;
- machine-wide install в Program Files;
- self-contained WebAssistant payload;
- Windows Service registration;
- automatic service start;
- registration in Installed Apps / Programs and Features;
- `DisplayVersion == webassist/VERSION`;
- standard uninstall removes service, registration and product files;
- current accepted health/service/scanner smoke работает после установки.

Canonical producer остаётся `package.bat`; WiX project/tool invocation является внутренней реализацией этого producer, а не отдельным третьим build path.

## ALT Linux 10.1 installer

Canonical artifact:

```text
WebAssistant-linux-x64-<VERSION>.zip
```

ZIP после распаковки содержит минимум:

```text
self-contained application
appsettings.json
VERSION
install.sh
uninstall.sh
systemd unit
necessary product files
```

User flow:

```text
download ZIP
-> unpack
-> read instructions
-> sudo ./install.sh
-> systemd service enabled/started
```

`install.sh` является consumer already-packaged config, не config producer.

Supported target для текущей work — ALT Linux 10.1. Старый p11 lifecycle evidence остаётся historical regression evidence, но не заменяет target acceptance.

## Exact artifact identity

Каждый producer выдаёт final installer и evidence identity:

```text
artifact
SHA-256
provenance manifest
```

Минимум provenance:

```text
artifact filename
webassist/VERSION
source commit SHA
platform/RID
SHA-256
byte size
.NET SDK/toolchain version used for build
config mode: source-appsettings | generated-default
canonical package entrypoint
```

Checksums/provenance могут публиковаться рядом с user-facing assets, но главное требование — они однозначно идентифицируют bytes, прошедшие acceptance.

## Producer/consumer CI boundary

Producer jobs владеют build/package:

```text
package.bat / package.sh
-> final installer
-> hash/provenance
-> immutable workflow artifact transport
```

Consumer lifecycle jobs:

```text
download artifact
-> verify VERSION/hash
-> install exact artifact
-> service/systemd/health/current accepted scanner smoke
-> uninstall
```

Consumer jobs НЕ выполняют:

```text
dotnet publish
package.bat
package.sh
payload mutation after checksum
```

Config-present semantics проверяются отдельным isolated product-root fixture, который до canonical producer получает synthetic harmless `src/WebAssistant/appsettings.json` и затем вызывает тот же producer. Release artifact public tree после build не модифицируется.

## Windows acceptance

Acceptance идёт через canonical EXE, а не через прямой вызов внутренних install scripts.

Минимум проверяется:

```text
installer accepts unattended CI mode
machine-wide installation succeeds
service exists
start mode = Automatic
service state = Running
/v1/health = 200
listener remains loopback-only
Installed Apps/ARP entry exists
DisplayVersion == VERSION
current accepted scanner/API smoke works
standard uninstall succeeds
service/registration removed
```

## ALT acceptance

Acceptance идёт от exact ZIP:

```text
verify hash
-> unzip
-> validate required payload
-> sudo ./install.sh
-> systemd enabled/active
-> /v1/health = 200
-> loopback-only
-> current accepted SANE/API smoke
-> restart lifecycle
-> uninstall
```

Automated container/VM acceptance не выдаётся за physical reboot evidence #12.

## GitHub Release

Для accepted `main` с новой `webassist/VERSION` release flow:

```text
build Windows once
build ALT once
-> verify identities
-> install/test exact bytes
-> build installation PDF
-> publish same installer bytes
```

Canonical user-facing assets:

```text
WebAssistant-win-x64-<VERSION>.exe
WebAssistant-linux-x64-<VERSION>.zip
WebAssistant-Installation-Guide.pdf
```

Дополнительно разрешены checksum/provenance assets с version-correlated filenames.

Repeated run semantics:

```text
release absent -> create only after full GREEN
release exists + exact hashes match -> idempotent success/no-op
release exists + hashes differ -> HARD FAIL
```

Released VERSION не перезаписывается другими bytes.

## Installation guide

`WebAssistant-Installation-Guide.pdf` строится воспроизводимо из repository-owned editable source.

Финальные screenshots делаются только после стабилизации real installer UX.

Windows coverage: EXE, UAC, installation result, Windows Service, health, Installed Apps, uninstall.

ALT coverage: ZIP, unpack, terminal, sudo, optional apt-rpm dependencies, successful install, systemctl status, health, uninstall.

## Contract/conformance transaction

Accepted v0.2 files не редактируются.

Новая candidate pair должна:

- preserve all current v0.2 scanner/API/network/diagnostic/filesystem/dependency semantics;
- add distribution requirements выше;
- add conformance vectors for canonical producers, version equality, config modes, self-contained targets, Windows EXE lifecycle, ALT ZIP lifecycle, immutable artifact reuse and same-bytes release;
- remain `candidate`/not accepted until implementation evidence is complete;
- use bounded GovernanceGrant for governance-controlled paths without broad policy relaxation.

Candidate schema/version identity выбирается непосредственно перед governance write: сначала определяется exact candidate filename pair, затем в linked Issue фиксируется GovernanceGrant на эти exact paths, и только после этого создаются candidate contract/conformance files. Design не резервирует номер заранее.

Promotion в `current` выполняется отдельным atomic governance step только после accepted evidence и отдельного exact-path authorization.

## Failure semantics

- semantic candidate RED -> implementation не начинается;
- producer failure -> installer artifact отсутствует;
- identity/hash mismatch -> install/release blocked;
- lifecycle failure -> release blocked;
- release artifact rebuild после acceptance запрещён;
- scanner/API deferred work не используется как причина расширить installer transaction;
- если installer implementation выявит реальный новый semantic delta, transaction останавливается и возвращается к contract candidate, а не расширяется молча.

## Verification discipline

Для каждого PR:

- fresh `main`;
- exact PR head;
- no hidden direct-main assumptions;
- applicable core/CI/repo-guard GREEN;
- unresolved blockers absent;
- fixed expected head at merge;
- post-merge reread.

Branch protection #3/#25 остаётся вне scope; до отдельного решения применяется fixed-head discipline.

## Execution order

```text
#165 design/spec
-> choose exact distribution candidate pair identity
-> GovernanceGrant for exact new candidate contract/conformance paths
-> create distribution-only candidate contract/conformance
-> #155 Windows versioned EXE
-> #156 ALT Linux 10.1 versioned ZIP
-> #7 exact immutable artifact transport/reuse
-> #5 final installer acceptance
-> #158 final documentation/PDF
-> #157 accepted-main GitHub Release
-> separate accepted/current promotion transaction
-> #159 only after real GitLab cluster config

LATER:
#161–#164 scanner/API redesign
```
