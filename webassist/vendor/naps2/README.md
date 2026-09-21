# Происхождение исправленного NAPS2 SDK

WebAssistant хранит принадлежащий репозиторию пакет NAPS2 SDK, потому что опубликованный `NAPS2.Sdk 1.3.0` предшествует исправлениям и данным о жизненном цикле и возможностях, которые нужны прямым адаптерам сканеров продукта.

Текущий зафиксированный пакет:

- идентификатор: `WebAssistant.NAPS2.Sdk`;
- версия: `1.3.0-webassistant.4.450cba65`;
- файл: `../nuget/WebAssistant.NAPS2.Sdk.1.3.0-webassistant.4.450cba65.nupkg`;
- SHA-256: `34bf8c94b851dcabad12f6cb50abc504a14010b44b3d6db592efec6ad310e0fc`;
- исходный репозиторий: `https://github.com/cyanfish/naps2`;
- точный исходный commit: `450cba65aaffe6387041050a573051a64cd80fe9`;
- исходная база включает исправление `Sane: Fix handling of fixed-point WordList options`;
- изменение SDK, принадлежащее WebAssistant: добавлено nullable-поле `PaperSourceCaps.FeederHasPaper`;
- отображение WIA: при наличии feeder читается состояние готовности; нечитаемое состояние представляется как `null`;
- отображение TWAIN: при поддержке читается `CAP_FEEDERLOADED`; неподдерживаемое или нечитаемое состояние представляется как `null`;
- жизненный цикл worker: идемпотентная общая задача остановки с ограниченным по времени штатным завершением и ограниченным по времени принудительным завершением;
- жизненный цикл фабрики: отслеживаются все принадлежащие продукту worker и незавершённые запуски, публикация после начала остановки запрещается, владение завершается через `ShutdownAsync()`;
- жизненный цикл контекста сканирования: предоставляется общий барьер `ShutdownAsync()`, а `Dispose` ожидает его завершения.

Пакет `.4` собирается из указанного точного публичного commit upstream с теми же исходными изменениями состояния feeder и жизненного цикла worker, что `.3`. Дополнительно для корректной замены versioned DLL при Windows MSI upgrade он сохраняет CLR `AssemblyVersion=8.3.0.0`, но получает монотонный `FileVersion=8.3.0.4`. Политика выбора сканера WebAssistant внутри NAPS2 не реализуется; выбор источника `auto` остаётся ответственностью WebAssistant.

Пакет `.3` остаётся неизменяемым предшественником `.4`. Его закреплённый SHA-256 — `e8abde3b7bd7e756eea714883c6e6ed79c6bb5f5052cd630b3dc763e45a50915`. Пакет `.2` также остаётся неизменяемым; его SHA-256 — `2dbc6e96cf0d46a554318f3224561861e669dd09b60fc618319c53fed10dcc9f`. Старые поколения никогда не пересобираются и не перезаписываются.

## Пересборка

Запускайте из каталога `webassist` на машине с Git, Python 3 и .NET SDK 10:

```bash
./vendor/naps2/rebuild-fixed-sdk.sh
```

Сценарий пересборки получает точный commit upstream, применяет изменения исходников с закрытием при несовпадении ожидаемой структуры, выполняет только сборку проекта `net10.0` с отключённым созданием пакета, а затем запускает `dotnet pack --no-build`. Сборка и упаковка используют стабильное отображение путей компилятора и исключают отладочные пути, чтобы случайные временные каталоги checkout не меняли сборку SDK.

После `dotnet pack` сценарий канонически переписывает `.nupkg`: записи сортируются, временные метки и атрибуты ZIP фиксируются, дополнительные поля и комментарии удаляются, для записей используется `ZIP_STORED`. Полученный пакет воспроизводится байт-в-байт при повторных сборках из тех же точных входных данных. Канонический пакет `.4` имеет размер 986644 байта и SHA-256 `34bf8c94b851dcabad12f6cb50abc504a14010b44b3d6db592efec6ad310e0fc`.

## Source-aligned Win32 worker

Для 32-битного TWAIN worker WebAssistant больше не использует stock-пакет `NAPS2.Sdk.Worker.Win32 1.3.0`. Repository-owned worker собирается из **того же exact checkout и той же patched source tree**, что и текущий `WebAssistant.NAPS2.Sdk`.

Текущий пакет worker:

- идентификатор: `WebAssistant.NAPS2.Sdk.Worker.Win32`;
- версия: `1.3.0-webassistant.1.450cba65`;
- файл: `../nuget/WebAssistant.NAPS2.Sdk.Worker.Win32.1.3.0-webassistant.1.450cba65.nupkg`;
- SHA-256: `18993608e478661100df88f2ea80fcd87c88719d05b0b8f6230da18b582ed691`;
- исходный commit: `450cba65aaffe6387041050a573051a64cd80fe9`;
- источник executable: `NAPS2.Sdk.Worker.Build/NAPS2.Sdk.Worker.Build.csproj`;
- package wrapper: `NAPS2.Sdk.Worker.Win32/NAPS2.Sdk.Worker.Win32.csproj`;
- публикуемый файл: `contentFiles/NAPS2.Worker.exe`;
- `FileVersion=8.3.0.1`.

Сначала к одному pinned checkout применяются изменения WebAssistant для SDK/TWAIN и жизненного цикла worker, затем из этой же source tree собираются и SDK, и x86 worker. Поэтому код `LocalTwainController` внутри `NAPS2.Worker.exe` source-aligned с repository-owned SDK, а не взят из отдельного stock NuGet build.

Имя файла targets внутри NuGet также принадлежит repository-owned package: `build/WebAssistant.NAPS2.Sdk.Worker.Win32.targets`. Это сохраняет автоматический NuGet import после изменения PackageId.

Это изменение устраняет source/runtime skew. Оно само по себе не считается доказательством исправления физического Canon TWAIN crash `0xc0000005`; для него остаётся отдельный физический DSM/capability acceptance.

