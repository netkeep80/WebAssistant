namespace WebAssistant.Http;

internal static class LoopbackHostGuard
{
    internal static bool IsAllowed(HostString host, int configuredPort)
    {
        // DNS-алиас, разрешившийся в loopback, не является локальным origin.
        // Сравнивается HTTP Host, а не IP-адрес установленного TCP-соединения.
        var name = host.Host;
        if (!string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(name, "127.0.0.1", StringComparison.Ordinal))
        {
            return false;
        }

        // TestServer и некоторые программные HTTP-клиенты опускают порт
        // в Host. При явном порте он обязан совпадать с listener.
        return host.Port is null || host.Port.Value == configuredPort;
    }
}
