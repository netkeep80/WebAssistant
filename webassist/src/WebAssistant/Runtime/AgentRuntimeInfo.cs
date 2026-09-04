using System.Reflection;

namespace WebAssistant.Runtime;

internal sealed class AgentRuntimeInfo
{
    internal AgentRuntimeInfo()
    {
        StartedAt = DateTimeOffset.Now;
        Version = ResolveVersion();
    }

    internal DateTimeOffset StartedAt { get; }

    internal string Version { get; }

    private static string ResolveVersion()
    {
        var assembly = typeof(AgentRuntimeInfo).Assembly;
        return assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}
