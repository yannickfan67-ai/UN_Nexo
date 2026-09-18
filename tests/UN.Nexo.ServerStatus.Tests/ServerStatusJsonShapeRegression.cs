using System.Reflection;
using System.Runtime.CompilerServices;
using UN.Nexo.Core.Services;

namespace UN.Nexo.ServerStatus.Tests;

internal static class ServerStatusJsonShapeRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        var parseStatus = typeof(MinecraftServerStatusService).GetMethod(
            "ParseStatus",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ParseStatus not found.");

        foreach (var json in new[] { "[]", "null", "\"hello\"", "123" })
        {
            try
            {
                _ = parseStatus.Invoke(null, new object[] { json, "test.invalid:25565" });
                throw new InvalidOperationException($"Non-object server status JSON was accepted: {json}");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException inner)
            {
                if (!inner.Message.Contains("root must be an object", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Malformed status response did not produce an actionable schema error.", inner);
            }
        }
    }
}
