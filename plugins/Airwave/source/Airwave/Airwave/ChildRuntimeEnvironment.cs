using System.Diagnostics;

namespace Airwave;

internal static class ChildRuntimeEnvironment
{
    public static void Apply(ProcessStartInfo start)
    {
        // Game hosts can select an embedded .NET runtime containing only the core
        // framework. Standalone helpers must resolve their installed frameworks.
        foreach (var name in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86", "DOTNET_ROOT_ARM64", "DOTNET_ROOT(x86)", "DOTNET_MULTILEVEL_LOOKUP" })
            start.Environment.Remove(name);
    }
}
