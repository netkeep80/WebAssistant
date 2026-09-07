# Tiered CI Feedback / Exact-Head Acceptance Design

Дата: 2026-09-07
Issue: #5

## Контекст

После #6 WebAssistant уже имеет repository-owned fail-closed change classifier, который по exact `base SHA -> head SHA` вычисляет применимые platform suites. Текущий `ci.yml` запускает classifier, core и все требуемые heavy suites в одном DAG, а `ci-required` агрегирует их в один stable product gate.

Это безопасно, но development feedback и merge acceptance остаются смешаны: draft head ждёт те же lifecycle/E2E jobs, которые нужны только для окончательного принятия exact merge candidate.

Отдельный governance blocker остаётся в #3/#25: `main` пока не защищён GitHub ruleset/branch protection. Поэтому workflow-механика Tier A/Tier B может быть реализована и доказана раньше, но #5 нельзя считать полностью принятым как merge-boundary guarantee, пока #3 не сделает `ci-required` и `repo-guard` реально обязательными на GitHub merge boundary.

## Цели

1. Каждый актуальный PR head быстро получает Tier A verdict о базовой корректности.
2. Draft PR не поднимает full lifecycle/E2E acceptance без явного manual full request.
3. Ready PR получает полный применимый Tier B на текущем exact head.
4. Любой новый commit на ready PR автоматически инвалидирует старое final evidence и запускает Tier B заново.
5. `ready_for_review` запускает Tier B без изменения source head.
6. Platform-critical изменения получают ранний OS-specific adapter smoke.
7. `ci-required` остаётся стабильным final product gate и никогда не превращает missing/skipped/failed/cancelled обязательный Tier B в success.
8. Permanent `repo-guard` остаётся отдельным governance trust boundary.
9. Manual full-run проверяет именно текущий head конкретного PR, а не произвольный SHA.

## Не-цели

- Не менять product/runtime semantics WebAssistant.
- Не менять accepted contract/conformance pair.
- Не убирать ни Windows Service lifecycle, ни ALT systemd lifecycle, ни virtual scanner HTTP E2E.
- Не оптимизировать restore/build/package reuse; это отдельный #7.
- Не вводить cache strategy; это отдельный #8.
- Не убирать post-merge/canary CI; это отдельный #9.
- Не считать workflow-only механику заменой GitHub ruleset из #3/#25.

## Выбранная архитектура

Сохраняется один top-level `.github/workflows/ci.yml` и существующий repository-owned `ci/change-plan.sh`.

Внутри одного PR DAG появляются две явные фазы:

```text
exact PR base/head
      |
      v
requirements / change-plan
      |
      +----------------------------+
      |                            |
      v                            v
Tier A                         Tier B
fast feedback                 final acceptance
      |                            |
      v                            v
ci-fast --------------------> ci-required
```

Tier A выполняется на каждом актуальном PR head. Tier B выполняется только когда текущий event требует final acceptance.

Это предпочтительнее двух независимых PR workflows, потому что один DAG сохраняет одну exact-head классификацию, одну concurrency group и один источник required-suite truth.

## Implementation surfaces

Архитектура намеренно ограничена существующим CI control plane:

- `.github/workflows/ci.yml` — event resolution, Tier A/Tier B orchestration, `ci-fast`, `ci-required`;
- `ci/change-plan.sh` — smoke outputs поверх уже принятой path classification;
- `.github/workflows/virtual-scanner.yml` — reusable `smoke|full` scanner mode;
- `.github/workflows/core.yml` — explicit exact `source_ref` checkout contract;
- `.github/workflows/linux-systemd.yml` — explicit exact `source_ref` checkout contract;
- `.github/workflows/windows-service.yml` — explicit exact `source_ref` checkout contract;
- `tests/core/**` — table-driven event/classifier/workflow contract tests;
- `webassist/VERSION` — обязательный persisted accepted-state marker.

Product source, installers, accepted contracts/conformance и runtime API не входят в scope #5.

## Event model

### Pull request events

Top-level workflow продолжает слушать:

- `opened`
- `synchronize`
- `reopened`
- `ready_for_review`

`requirements` вычисляет `final_required` из PR state/event:

- draft PR: `final_required=false`;
- ready/non-draft PR: `final_required=true`;
- `ready_for_review`: `final_required=true` на том же exact source head;
- `synchronize` ready PR: `final_required=true` для нового head;
- `synchronize` draft PR: `final_required=false`.

GitHub check evidence привязано к commit SHA, поэтому новый ready commit не может наследовать `ci-required` старого head.

### Manual full acceptance

Тот же `ci.yml` получает `workflow_dispatch` с обязательным input `pr_number`.

Manual run должен запускаться на ref PR branch. Resolver в `requirements`:

1. читает PR через GitHub API с workflow `GITHUB_TOKEN`;
2. получает current `base.sha`, `head.sha`, draft/state;
3. проверяет `GITHUB_SHA == current PR head.sha`;
4. при mismatch завершает requirements failure;
5. при совпадении принудительно выставляет `final_required=true`.

Workflow permissions для этого resolver ограничены `contents: read` и `pull-requests: read`; пользовательские PAT/secrets не вводятся.

Это гарантирует, что manual run/check suite прикреплён именно к текущему PR head. Передавать свободный `head_sha` как пользовательский input запрещено.

## Exact-head context

Для `pull_request` source of truth:

- base = `github.event.pull_request.base.sha`;
- head = `github.event.pull_request.head.sha`.

Для `workflow_dispatch` source of truth:

- PR metadata, прочитанная по `pr_number`;
- dispatch ref обязан указывать на тот же `head.sha`.

`requirements` экспортирует normalized outputs:

- `base_sha`
- `head_sha`
- `final_required`
- существующие classifier outputs (`core`, `linux_systemd`, `windows_service`, `virtual_linux`, `virtual_windows`, `virtual_scanner`, `full_cross_platform`)
- новые smoke outputs `smoke_linux`, `smoke_windows`.

Все reusable workflows, которые могут запускаться через manual dispatch, получают explicit `source_ref` input и checkout exact resolved head, а не event-default ref.

## Tier A — fast feedback

Tier A состоит из:

1. `requirements` / exact change classification;
2. permanent `repo-guard` как отдельный workflow/check;
3. core/unit/contract/structural tests;
4. OS-specific scanner adapter smoke для затронутой платформы.

### Core

Core остаётся текущим reusable `core.yml` и выполняет repository-owned test suite.

### Platform adapter smoke

Существующий reusable `virtual-scanner.yml` расширяется string input `mode` со строго допустимыми значениями `smoke` и `full`.

Reusable workflow обязан fail closed до platform tests, если mode отсутствует или имеет другое значение.

Smoke mode выполняет platform setup и только acquisition/adaptor test:

- Linux: SANE test backend + `Category=LinuxVirtualScanner`;
- Windows: official TWAIN sample source + `Category=WindowsVirtualScanner`.

Smoke mode НЕ выполняет:

- `Category=PlatformVirtualEndToEnd` HTTP E2E;
- ALT systemd lifecycle;
- Windows Service lifecycle.

Top-level `ci.yml` вызывает virtual-scanner reusable workflow дважды при необходимости:

- `scanner-smoke` — Tier A, mode=`smoke`;
- `scanner-final` — Tier B, mode=`full`.

На ready PR возможен осознанный повтор acquisition setup/test: Tier A должен дать ранний независимый verdict, а Tier B — полный exact-head evidence. Устранение повторной restore/setup работы относится к #7, не к #5.

## Tier B — exact final acceptance

Tier B выполняется только при `final_required=true`.

По classifier plan он включает применимые heavy evidence:

- ALT p11 systemd lifecycle;
- Windows Service lifecycle;
- Linux virtual SANE direct-SDK full HTTP E2E;
- Windows virtual TWAIN direct-SDK full HTTP E2E.

Не затронутая платформа остаётся корректно `skipped` в соответствии с #6.

## `ci-fast`

В top-level workflow добавляется aggregator `ci-fast`.

Он зависит от:

- requirements;
- core;
- scanner-smoke.

Fail-closed правила аналогичны существующему `ci-required`:

- required job -> только `success` допустим;
- non-required -> `success` или `skipped` допустим;
- `failure`, `cancelled`, unknown result -> failure;
- invalid/missing requirement flag -> failure.

`ci-fast` создаётся и должен GREEN на каждом актуальном PR head, включая draft.

Это development signal, не merge-readiness signal.

## `ci-required`

`ci-required` остаётся stable final product gate.

Он запускается только если `final_required=true` и зависит от:

- `ci-fast`;
- всех применимых Tier B jobs.

Он обязан fail closed, если:

- `ci-fast` не success;
- любой required Tier B job не success;
- requirements/classifier не success;
- requirement flag отсутствует/невалиден;
- результат неизвестен, failure или cancelled.

Draft PR без manual full acceptance не получает успешный `ci-required`; это намеренно. Draft сам по себе не merge candidate.

Manual full acceptance на exact draft head может создать `ci-required`, но перевод draft -> ready всё равно запускает новый Tier B event на том же head и тем самым явно доказывает ready transition.

## Concurrency и obsolete heads

Сохраняется PR-scoped concurrency:

```text
webassistant-pr-<PR number>
cancel-in-progress: true
```

Любой новый PR event отменяет obsolete run того же PR. Cancellation required Tier A/Tier B никогда не конвертируется агрегатором в success.

Для manual dispatch concurrency key также должен включать PR number, чтобы manual full и новый synchronize одного PR не могли одновременно выдавать competing acceptance для разных heads.

## Reusable workflow checkout contract

Чтобы `workflow_dispatch` был exact-head корректным, reusable workflows перестают полагаться только на event-default checkout.

Каждый вызываемый child workflow принимает explicit `source_ref` для `workflow_call`:

- PR top-level передаёт resolved `head_sha`;
- manual top-level передаёт verified current PR `head_sha`;
- standalone `push: main` child behavior использует `github.sha`, когда `source_ref` не задан или child запущен напрямую.

Child checkout обязан использовать explicit ref, когда input задан.

Это относится к:

- `core.yml`;
- `linux-systemd.yml`;
- `windows-service.yml`;
- `virtual-scanner.yml`.

Контракт покрывается structural tests, чтобы ни один manual Tier B child не начал случайно тестировать default branch вместо requested PR head.

## Classifier extension

`ci/change-plan.sh` остаётся единственным path/change-class source of truth.

Добавляются smoke outputs:

- `smoke_linux=true`, если изменение затрагивает Linux platform-critical surface;
- `smoke_windows=true`, если изменение затрагивает Windows platform-critical surface.

Для common/mixed/unknown/self-change full plan оба smoke outputs = true.

Docs-only может оставить оба false.

VERSION остаётся neutral co-change только рядом с содержательным path, как принято в #6.

## GitHub ruleset dependency

Полная acceptance #5 требует #3/#25.

После включения ruleset для `main`:

- direct push запрещён;
- PR required;
- `repo-guard` required отдельно;
- `ci-required` required;
- up-to-date/exact-head policy включена согласно #3.

`ci-fast` НЕ является required merge check: он входит в dependency `ci-required`, но не должен создавать второй стабильный merge-boundary contract.

До фактического ruleset workflow может доказать Tier semantics, но Issue #5 остаётся OPEN с внешним blocker #3.

## TDD / executable acceptance

Перед изменением production workflows добавляются RED tests, которые требуют:

1. draft PR semantics: `final_required=false`;
2. ready PR semantics: `final_required=true`;
3. ready_for_review не требует source commit для Tier B;
4. ready synchronize требует Tier B на новом head;
5. manual dispatch требует `pr_number`, `pull-requests: read`, PR-head ref match и fail-closed mismatch;
6. `ci-fast` существует и агрегирует requirements/core/smoke;
7. `ci-required` final-only и зависит от `ci-fast` + Tier B;
8. required failure/cancelled/skipped semantics остаются fail closed;
9. virtual-scanner принимает только `smoke|full`, invalid mode fail closed;
10. virtual-scanner smoke mode не запускает HTTP E2E;
11. full mode сохраняет HTTP E2E;
12. Linux-only smoke не запускает Windows smoke;
13. Windows-only smoke не запускает Linux smoke;
14. common/unknown/self-change включает оба smoke;
15. child workflows checkout exact `source_ref` для workflow_call;
16. push-main standalone semantics child workflows не ломаются.

GREEN implementation не должна ослаблять эти tests.

## Реальные GitHub acceptance probes

После merge workflow-механики нужны закрываемые без merge probes:

### Draft development probe

- открыть draft PR;
- Tier A GREEN;
- heavy Tier B jobs skipped/not started;
- `ci-required` не является GREEN final verdict.

### Draft -> ready probe

На том же source head:

- перевести draft в ready;
- доказать запуск Tier B без source commit;
- `ci-required` GREEN только после применимого Tier B.

### Ready synchronize invalidation probe

- после GREEN final добавить новый commit + обязательный VERSION transition;
- предыдущий run становится obsolete/cancelled;
- новый exact head обязан заново получить Tier A + Tier B + `ci-required`.

### Manual exact-head probe

- dispatch full workflow на PR branch с корректным `pr_number` -> full applicable evidence GREEN;
- отдельный negative probe с mismatched selected ref/PR -> requirements RED до platform jobs.

Все probe PR закрываются без merge.

## Метрики

Baseline из #97 используется как до-изменения:

- push -> first useful result p50 = 41 s;
- push -> `ci-required` p50 = 147.5 s;
- full head runner wall-time p50 ≈ 7.31 min.

После #5 измеряется минимум 10 representative development heads:

- p50/p95 push -> `ci-fast`;
- p50/p95 ready/synchronize -> `ci-required`;
- Tier A runner wall-time;
- Tier B runner wall-time;
- доля development/draft heads, не запускающих heavy Tier B.

Acceptance требует существенного улучшения development critical path относительно baseline first-useful/final-mixed model без удаления final evidence.

## Delivery sequencing

1. Design/spec commit — этот документ; product/contract semantics не меняются.
2. После review — implementation plan.
3. Создать узкий GovernanceGrant на изменяемые `.github/workflows/**`.
4. TDD RED transaction для workflow contract/tests.
5. GREEN implementation Tier A/Tier B.
6. Exact-head repo-guard + CI acceptance.
7. Fixed-head merge workflow mechanics.
8. Closed-unmerged real probes.
9. Измерить post-change metrics.
10. Если #3 ещё OPEN — записать workflow acceptance и оставить #5 OPEN blocked only by merge-boundary settings.
11. После фактического #3 ruleset — final merge-boundary probe и закрытие #5.

## Параллельная работа #135/#139

#135/#139 ведётся отдельным потоком и не входит в scope #5.

На момент design base `main` имеет VERSION `0.3.5`, а #139 зарезервировал accepted transition `0.3.5 -> 0.3.6`. Поэтому design/implementation stream #5 использует provisional next marker `0.3.7` и перед любым PR/merge обязан fresh-read current main. Если main изменится иначе, фактический VERSION выбирается заново как следующий разрешённый accepted state; #139 не cherry-pick'ается и не модифицируется из #5.

## Инварианты безопасности

- Unknown executable/product path всегда fail closed.
- `repo-guard` остаётся отдельным check.
- Accepted v0.2 contract/conformance не меняются.
- Tier A не заменяет Tier B.
- Successful Tier B старого SHA не является evidence нового SHA.
- Manual run не может доказывать SHA, отличный от текущего head указанного PR.
- Manual PR resolver использует только repository workflow token с read-only contents/pull-request permissions.
- Invalid virtual-scanner mode fail closed до platform evidence.
- Correctly skipped non-required jobs допустимы; skipped required jobs — failure.
- Direct merge protection считается доказанной только после фактического GitHub ruleset из #3/#25.
