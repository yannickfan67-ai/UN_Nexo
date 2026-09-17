using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class MavenArtifactPathRegression
{
    public static void Run()
    {
        var expected = Path.Combine("com", "example", "lib", "1.0", "lib-1.0.jar");
        if (MavenArtifactPath.FromCoordinate("com.example:lib:1.0") != expected)
            throw new Exception("Valid multi-component Maven group resolved incorrectly.");

        var singleExpected = Path.Combine("example", "lib", "1.0", "lib-1.0.jar");
        if (MavenArtifactPath.FromCoordinate("example:lib:1.0") != singleExpected)
            throw new Exception("Valid single-component Maven group resolved incorrectly.");

        foreach (var coordinate in new[]
                 {
                     ".com.example:lib:1.0",
                     "com..example:lib:1.0",
                     "com.example.:lib:1.0"
                 })
        {
            try
            {
                MavenArtifactPath.FromCoordinate(coordinate);
                throw new Exception($"Malformed Maven group was accepted: {coordinate}");
            }
            catch (InvalidDataException)
            {
                // Expected: empty group components must not alias a valid coordinate.
            }
        }
    }
}
