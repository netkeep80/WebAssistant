namespace WebAssistant.Scanning;

internal enum RequestedScanSource
{
    Auto,
    Flatbed,
    Feeder
}

internal enum FeederPaperState
{
    Present,
    Absent,
    Unknown
}

internal static class ScanSourcePolicy
{
    internal static ScanSource Resolve(
        RequestedScanSource requestedSource,
        bool duplex,
        bool supportsFlatbed,
        bool supportsFeeder,
        bool supportsDuplex,
        FeederPaperState feederPaperState)
    {
        if (duplex && requestedSource is RequestedScanSource.Auto or RequestedScanSource.Flatbed)
        {
            throw new ArgumentException(
                "Двустороннее сканирование допустимо только при явном source=feeder.",
                nameof(duplex));
        }

        return requestedSource switch
        {
            RequestedScanSource.Auto => ResolveAuto(
                supportsFlatbed,
                supportsFeeder,
                feederPaperState),
            RequestedScanSource.Flatbed => supportsFlatbed
                ? ScanSource.Glass
                : throw new NotSupportedException("Планшетный источник сканера не поддерживается."),
            RequestedScanSource.Feeder => ResolveFeeder(
                duplex,
                supportsFeeder,
                supportsDuplex),
            _ => throw new ArgumentOutOfRangeException(
                nameof(requestedSource),
                requestedSource,
                "Неизвестный запрошенный источник сканирования.")
        };
    }

    private static ScanSource ResolveAuto(
        bool supportsFlatbed,
        bool supportsFeeder,
        FeederPaperState feederPaperState)
    {
        if (supportsFlatbed && supportsFeeder)
        {
            return feederPaperState switch
            {
                FeederPaperState.Present => ScanSource.Feeder,
                FeederPaperState.Absent => ScanSource.Glass,
                FeederPaperState.Unknown => ScanSource.Glass,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(feederPaperState),
                    feederPaperState,
                    "Неизвестное состояние бумаги в лотке.")
            };
        }

        if (supportsFeeder)
        {
            return ScanSource.Feeder;
        }

        if (supportsFlatbed)
        {
            return ScanSource.Glass;
        }

        throw new NotSupportedException("Сканер не предоставляет поддерживаемый источник сканирования.");
    }

    private static ScanSource ResolveFeeder(
        bool duplex,
        bool supportsFeeder,
        bool supportsDuplex)
    {
        if (!supportsFeeder)
        {
            throw new NotSupportedException("Лоток сканера не поддерживается.");
        }

        if (!duplex)
        {
            return ScanSource.Feeder;
        }

        if (!supportsDuplex)
        {
            throw new NotSupportedException("Двустороннее сканирование из лотка не поддерживается.");
        }

        return ScanSource.Duplex;
    }
}
