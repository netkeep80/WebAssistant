## Краткое описание

<!-- Что меняется и зачем? -->

## Документация

- [ ] Изменённые человекочитаемые документация, требования, архитектурные описания и тексты политик написаны на русском языке в соответствии с [`webassist/docs/documentation-language-policy.md`](../webassist/docs/documentation-language-policy.md).
- [ ] Английский текст оставлен только для буквальных технических идентификаторов, собственных имён, команд, кодов, путей и машинных значений.

### Полный аудит документации

Используйте [`docs/documentation-governance.md`](../docs/documentation-governance.md). Проверка одного очевидного файла не считается полным аудитом.

- Проверены актуальные документы:
  - <!-- перечислите все current docs из затронутых строк карты -->
- Обновлены:
  - <!-- перечислите реально изменённые документы или "нет" -->
- Не затронуты (почему):
  - <!-- перечислите проверенные current docs, не требующие изменения, с кратким основанием -->
- [ ] Candidate contract/conformance проверены и синхронизированы, если изменение входит в их semantic surface.
- [ ] `accepted immutable` contract/conformance и историческое evidence не переписаны задним числом.

## Намерение изменения

<!-- PR объявляет только ChangeIntent. GovernanceGrant принимается только из связанной Issue. -->

```repo-guard-yaml
change_type: feature
scope:
  - webassist/**
budgets: {}
anchors:
  affects: []
  implements: []
  verifies: []
must_touch: []
must_not_touch: []
expected_effects:
  - Опишите ожидаемый эффект
```
