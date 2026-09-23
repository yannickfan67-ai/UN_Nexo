using System.Text.Json;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class VersionMetadataResolverRegression
{
    internal static async Task RunAsync()
    {
        await TestValidStringIdAsync();
        await TestWrongTypedIdsAsync();
        await TestWrongTypedInheritanceAsync();
        await TestWrongTypedLibraryNameAsync();
        await TestOversizedMetadataRejectedAsync();
        await TestLinkedVersionMetadataRejectedAsync();
        await TestNormalInheritanceAsync();
    }

    private static async Task TestValidStringIdAsync()
    {
        var root = NewRoot("valid-id");
        try
        {
            await WriteVersionAsync(root, "test", """
                {"id":"test","libraries":[]}
                """);
            using var resolved = await new MinecraftVersionMetadataResolver()
                .ResolveAsync(root, "test");
            Assert(
                resolved.Document.RootElement.GetProperty("id").GetString() == "test",
                "Valid string id should resolve normally.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestWrongTypedIdsAsync()
    {
        var fixtures = new[]
        {
            ("numeric", """{"id":123,"libraries":[]}"""),
            ("null", """{"id":null,"libraries":[]}"""),
            ("object", """{"id":{},"libraries":[]}"""),
            ("array", """{"id":[],"libraries":[]}"""),
            ("boolean", """{"id":true,"libraries":[]}""")
        };

        foreach (var fixture in fixtures)
        {
            var root = NewRoot("bad-id-" + fixture.Item1);
            try
            {
                await WriteVersionAsync(root, "test", fixture.Item2);
                await ExpectInvalidDataAsync(
                    () => new MinecraftVersionMetadataResolver().ResolveAsync(root, "test"),
                    "id",
                    $"Wrong-typed id fixture '{fixture.Item1}'");
            }
            finally
            {
                TryDelete(root);
            }
        }
    }

    private static async Task TestWrongTypedInheritanceAsync()
    {
        var values = new[]
        {
            "123",
            "{}",
            "[]",
            "true"
        };

        foreach (var value in values)
        {
            var root = NewRoot("bad-inherits");
            try
            {
                await WriteVersionAsync(root, "test",
                    "{\"id\":\"test\",\"inheritsFrom\":" + value + ",\"libraries\":[]}");
                await ExpectInvalidDataAsync(
                    () => new MinecraftVersionMetadataResolver().ResolveAsync(root, "test"),
                    "inheritsFrom",
                    "Wrong-typed inheritsFrom");
            }
            finally
            {
                TryDelete(root);
            }
        }
    }

    private static async Task TestWrongTypedLibraryNameAsync()
    {
        var root = NewRoot("bad-library-name");
        try
        {
            await WriteVersionAsync(root, "base", """
                {
                  "id":"base",
                  "libraries":[
                    {"name":"com.example:base:1.0"}
                  ]
                }
                """);
            await WriteVersionAsync(root, "child", """
                {
                  "id":"child",
                  "inheritsFrom":"base",
                  "libraries":[
                    {"name":123}
                  ]
                }
                """);

            await ExpectInvalidDataAsync(
                () => new MinecraftVersionMetadataResolver().ResolveAsync(root, "child"),
                "name",
                "Wrong-typed library name");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestOversizedMetadataRejectedAsync()
    {
        var root = NewRoot("oversized");
        try
        {
            var versionRoot = Path.Combine(root, "versions", "huge");
            Directory.CreateDirectory(versionRoot);
            var path = Path.Combine(versionRoot, "huge.json");
            await using (var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                stream.SetLength(
                    MinecraftVersionMetadataResolver.MaxVersionMetadataBytes
                    + 1);
            }

            await ExpectRejectedAsync(
                () => new MinecraftVersionMetadataResolver()
                    .ResolveAsync(root, "huge"),
                "oversized launch metadata");

            var childRoot = Path.Combine(root, "versions", "child");
            Directory.CreateDirectory(childRoot);
            await File.WriteAllTextAsync(
                Path.Combine(childRoot, "child.json"),
                "{\"id\":\"child\",\"inheritsFrom\":\"parent\",\"libraries\":[]}");
            var parentRoot = Path.Combine(root, "versions", "parent");
            Directory.CreateDirectory(parentRoot);
            var parentPath = Path.Combine(parentRoot, "parent.json");
            await using (var stream = new FileStream(
                parentPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                stream.SetLength(
                    MinecraftVersionMetadataResolver.MaxVersionMetadataBytes
                    + 1);
            }

            await ExpectRejectedAsync(
                () => new MinecraftVersionMetadataResolver()
                    .ResolveAsync(root, "child"),
                "oversized inherited parent");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestLinkedVersionMetadataRejectedAsync()
    {
        var root = NewRoot("linked");
        var outside = NewRoot("linked-outside");
        try
        {
            Directory.CreateDirectory(
                Path.Combine(root, "versions"));
            var outsideVersion =
                Path.Combine(outside, "test");
            Directory.CreateDirectory(outsideVersion);
            var outsideMetadata =
                Path.Combine(outsideVersion, "test.json");
            const string marker =
                "{\"id\":\"test\",\"mainClass\":\"external.Marker\",\"libraries\":[]}";
            await File.WriteAllTextAsync(
                outsideMetadata,
                marker);

            var linkedVersion =
                Path.Combine(root, "versions", "test");
            try
            {
                Directory.CreateSymbolicLink(
                    linkedVersion,
                    outsideVersion);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException
                or IOException
                or PlatformNotSupportedException
                or NotSupportedException)
            {
                Console.WriteLine(
                    "SKIP linked version metadata fixture: "
                    + ex.GetType().Name);
                return;
            }

            await ExpectRejectedAsync(
                () => new MinecraftVersionMetadataResolver()
                    .ResolveAsync(root, "test"),
                "linked version directory");

            Assert(
                await File.ReadAllTextAsync(outsideMetadata) == marker,
                "Rejected linked version metadata must not modify external bytes.");
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    private static async Task ExpectRejectedAsync(
        Func<Task<ResolvedMinecraftVersion>> action,
        string label)
    {
        try
        {
            using var _ = await action();
            throw new Exception(label + " should be rejected.");
        }
        catch (InvalidDataException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task TestNormalInheritanceAsync()
    {
        var root = NewRoot("normal-inheritance");
        try
        {
            await WriteVersionAsync(root, "base", """
                {
                  "id":"base",
                  "mainClass":"base.Main",
                  "downloads":{"client":{}},
                  "libraries":[
                    {"name":"com.example:shared:1.0"},
                    {"name":"com.example:classified:1.0:natives-linux"},
                    {"name":"com.example:extension:1.0@zip"},
                    {"name":"com.example:base-only:1.0"}
                  ],
                  "arguments":{"game":["--base"]}
                }
                """);
            await WriteVersionAsync(root, "child", """
                {
                  "id":"child",
                  "inheritsFrom":"base",
                  "mainClass":"child.Main",
                  "libraries":[
                    {"name":"com.example:shared:2.0"},
                    {"name":"com.example:classified:2.0:natives-linux"},
                    {"name":"com.example:classified:2.0:natives-windows"},
                    {"name":"com.example:extension:2.0"},
                    {"name":"com.example:child-only:1.0"}
                  ],
                  "arguments":{"game":["--child"]}
                }
                """);

            using var resolved = await new MinecraftVersionMetadataResolver()
                .ResolveAsync(root, "child");
            var metadata = resolved.Document.RootElement;
            Assert(
                metadata.GetProperty("mainClass").GetString() == "child.Main",
                "Child scalar metadata should override parent metadata.");
            Assert(
                resolved.ClientVersionId == "base",
                "Inherited profile without a client download should use the parent client version.");
            Assert(
                metadata.GetProperty("arguments").GetProperty("game").GetArrayLength() == 2,
                "Parent and child game arguments should remain merged.");
            var libraries = metadata.GetProperty("libraries")
                .EnumerateArray()
                .Select(item => item.GetProperty("name").GetString())
                .Where(name => name is not null)
                .Cast<string>()
                .ToArray();
            Assert(
                libraries.Length == 7,
                "Inheritance should replace same Maven identities while preserving distinct artifacts/classifiers/extensions.");
            Assert(
                libraries.Contains("com.example:shared:2.0", StringComparer.Ordinal)
                && !libraries.Contains("com.example:shared:1.0", StringComparer.Ordinal),
                "Child Maven version should replace the inherited version of the same artifact.");
            Assert(
                libraries.Contains("com.example:classified:2.0:natives-linux", StringComparer.Ordinal)
                && !libraries.Contains("com.example:classified:1.0:natives-linux", StringComparer.Ordinal),
                "Child version should replace the same classifier identity.");
            Assert(
                libraries.Contains("com.example:classified:2.0:natives-windows", StringComparer.Ordinal),
                "A distinct classifier must remain a separate inherited library identity.");
            Assert(
                libraries.Contains("com.example:extension:1.0@zip", StringComparer.Ordinal)
                && libraries.Contains("com.example:extension:2.0", StringComparer.Ordinal),
                "Different Maven extensions must not collapse into one identity.");
            Assert(
                libraries.Contains("com.example:base-only:1.0", StringComparer.Ordinal)
                && libraries.Contains("com.example:child-only:1.0", StringComparer.Ordinal),
                "Unrelated parent and child artifacts should both remain present.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task WriteVersionAsync(
        string gameRoot,
        string id,
        string json)
    {
        var directory = Path.Combine(gameRoot, "versions", id);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, id + ".json"),
            json);
    }

    private static async Task ExpectInvalidDataAsync(
        Func<Task<ResolvedMinecraftVersion>> action,
        string expectedField,
        string label)
    {
        try
        {
            using var _ = await action();
            throw new Exception(label + " should be rejected.");
        }
        catch (InvalidDataException ex)
        {
            Assert(
                ex.Message.Contains(expectedField, StringComparison.OrdinalIgnoreCase),
                label + " should identify the malformed field.");
        }
    }

    private static string NewRoot(string suffix)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-resolver-tests",
            suffix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
