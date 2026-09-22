# Происхождение исправленного NAPS2 SDK

WebAssistant хранит принадлежащие репозиторию пакеты NAPS2 SDK и Win32 worker, потому что опубликованный `NAPS2.Sdk 1.3.0` предшествует исправлениям и диагностическим данным, которые нужны прямым адаптерам сканеров продукта.

Текущий зафиксированный SDK:

- идентификатор: `WebAssistant.NAPS2.Sdk`;
- версия: `1.3.0-webassistant.6.450cba65`;
- файл: `../nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.6.450cba65.nupkg`;
- размер: `__SDK_SIZE__` байт;
- SHA-256: `__SDK_SHA256__`;
- исходный репозиторий: `https://github.com/cyanfish/naps2`;
- точный исходный commit: `450cba65aaffe6387041050a573051a64cd80fe9`;
- `AssemblyVersion=8.3.0.0`;
- `FileVersion=8.3.0.6`.

Исходная база включает исправление `Sane: Fix handling of fixed-point WordList options`. Изменения WebAssistant поверх pinned upstream:

- nullable-поле `PaperSourceCaps.FeederHasPaper`;
- чтение состояния feeder для WIA и TWAIN с `null` при недоступном или неопределённом состоянии;
- ограниченный и идемпотентный lifecycle worker;
- общий `ShutdownAsync()` для worker factory и scanning context;
- причинная диагностика `worker.acquire / worker.release / worker.exit`, включая явное `outcome=forcedKill` для принудительного завершения;
- worker self-report TWAIN runtime через служебные строки `WA_DIAG|` в stderr;
- фиксация requested/resolved/effective DSM;
- фиксация реально загруженных `twaindsm.dll`, legacy `twain_32.dll` и vendor DS modules непосредственно из worker-процесса;
- для доступных модулей фиксируются path, version и SHA-256;
- `GetCaps` разбит на наблюдаемые native boundaries `dsOpen`, `feederSetFalse`, `feederGetFalse`, `flatbedCaps`, `feederSetTrue`, `feederGetTrue`, `feederCaps`, `duplex`, `metadata`.

Worker self-report нужен в том числе для x86 TWAIN worker: 64-битный host-side `Process.Modules` не считается достаточным доказательством фактически загруженного DSM/DS в 32-битном процессе.

Пакет `.5` сохраняет CLR identity `AssemblyVersion=8.3.0.0`, но получает монотонный `FileVersion=8.3.0.6`. Политика выбора scanner/source внутри NAPS2 не реализуется: `source=auto` остаётся ответственностью WebAssistant.

Неизменяемые предшественники SDK:

- `.5`: SHA-256 `d2f53f57535f892df023c2e7cffeb4ad091d107e1192ada0d532be51d48fb88f`;
- `.4`: SHA-256 `34bf8c94b851dcabad12f6cb50abc504a14010b44b3d6db592efec6ad310e0fc`;
- `.3`: SHA-256 `e8abde3b7bd7e756eea714883c6e6ed79c6bb5f5052cd630b3dc763e45a50915`.

Старые поколения никогда не пересобираются и не перезаписываются.

## Пересборка

Запускайте из каталога `webassist` на машине с Git, Python 3 и .NET SDK 10:

```bash
./vendor/naps2/rebuild-fixed-sdk.sh
```

Сценарий:

1. получает точный upstream commit;
2. применяет изменения с закрытием при несовпадении ожидаемой структуры исходников;
3. собирает SDK и x86 worker из одного patched source tree;
4. выполняет `dotnet pack`;
5. канонически переписывает `.nupkg`: записи сортируются, временные метки и атрибуты ZIP фиксируются, дополнительные поля и комментарии удаляются, используется `ZIP_STORED`.

Канонический SDK `.6` имеет размер `__SDK_SIZE__` байт и SHA-256 `__SDK_SHA256__`.

## Source-aligned Win32 worker

Текущий repository-owned worker:

- идентификатор: `WebAssistant.NAPS2.Sdk.Worker.Win32`;
- версия: `1.3.0-webassistant.2.450cba65`;
- файл: `../nuget/WebAssistant.NAPS2.Sdk.Worker.Win32.1.3.0-webassistant.2.450cba65.nupkg`;
- размер: `22717179` байт;
- SHA-256: `dab042ae1a25a2d963dfe96e111bd3d1fba3148547a22a319907f6d270d4fa15`;
- исходный commit: `450cba65aaffe6387041050a573051a64cd80fe9`;
- источник executable: `NAPS2.Sdk.Worker.Build/NAPS2.Sdk.Worker.Build.csproj`;
- package wrapper: `NAPS2.Sdk.Worker.Win32/NAPS2.Sdk.Worker.Win32.csproj`;
- публикуемый файл: `contentFiles/NAPS2.Worker.exe`;
- `FileVersion=8.3.0.2`.

Предыдущий worker `.1` остаётся неизменяемым; его SHA-256 — `18993608e478661100df88f2ea80fcd87c88719d05b0b8f6230da18b582ed691`.

SDK и worker собираются из одного exact checkout и одной patched source tree. Поэтому код `LocalTwainController` внутри `NAPS2.Worker.exe` source-aligned с текущим repository-owned SDK.

Имя targets внутри NuGet принадлежит repository-owned package: `build/WebAssistant.NAPS2.Sdk.Worker.Win32.targets`. Это сохраняет автоматический NuGet import после изменения PackageId.

## Граница диагностики

Диагностика `#241` не реализует timeout/watchdog/kill/recovery `#240`. Она только делает lifecycle и native TWAIN boundary наблюдаемыми.

Технические события не содержат PDF bytes, Base64, raster/document contents или secrets. Worker self-report ограничен runtime identity, lifecycle, DSM/DS metadata и именованными native stages.

Source alignment и observability сами по себе не считаются доказательством исправления физического Canon/Samsung TWAIN hang/crash. Для принятия требуется отдельный физический прогон с одним встроенным Debug trace, который позволяет без PowerShell/cmd установить exact `operationId → workerPid → DSM/DS → последний native stage → release/exit`.
