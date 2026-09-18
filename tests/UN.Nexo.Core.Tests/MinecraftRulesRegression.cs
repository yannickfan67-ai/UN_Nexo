using System.Runtime.CompilerServices;
using System.Text.Json;
using UN.Nexo.Core.Launching;

namespace UN.Nexo.Core.Tests;

internal static class MinecraftRulesRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        Allows("{}");
        Allows("{\"rules\":[{\"action\":\"allow\"}]}");
        Allows("{\"rules\":[{\"action\":\"allow\",\"os\":{\"version\":\".*\"}}]}");

        Rejects("[]");
        Rejects("{\"rules\":{}}");
        Rejects("{\"rules\":[123]}");
        Rejects("{\"rules\":[{}]}");
        Rejects("{\"rules\":[{\"action\":123}]}");
        Rejects("{\"rules\":[{\"action\":\"allow\",\"os\":[]}]}");
        Rejects("{\"rules\":[{\"action\":\"allow\",\"os\":{\"name\":123}}]}");
        Rejects("{\"rules\":[{\"action\":\"allow\",\"os\":{\"version\":\"[\"}}]}");
        Rejects("{\"rules\":[{\"action\":\"allow\",\"features\":[]}]}");
        Rejects("{\"rules\":[{\"action\":\"allow\",\"features\":{\"demo\":1}}]}");

        var tooManyRules = "{\"rules\":[" + string.Join(',', Enumerable.Repeat("{\"action\":\"allow\"}", 33)) + "]}";
        Rejects(tooManyRules);
    }

    private static bool Allows(string json)
    {
        using var document = JsonDocument.Parse(json);
        return MinecraftRules.Allows(document.RootElement);
    }

    private static void Rejects(string json)
    {
        try
        {
            Allows(json);
            throw new InvalidOperationException($"Expected malformed rule metadata to be rejected: {json}");
        }
        catch (InvalidDataException)
        {
        }
    }
}
