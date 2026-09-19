using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class ManagedRuntimePublicationRegression
{
    internal static Task RunAsync()
    {
        TestRollbackRestoresPreviousRuntime();
        TestSuccessfulSwapPublishesPreparedRuntime();
        return Task.CompletedTask;
    }

    private static void TestRollbackRestoresPreviousRuntime()
    {
        var root = NewRoot("rollback");
        try
        {
            var target = Path.Combine(root, "temurin-8-linux-x64");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "old-runtime.txt"), "old");
            File.WriteAllText(Path.Combine(target, "nexo-runtime.json"), "{\"major\":8}");

            var missingPrepared = Path.Combine(root, "missing-prepared-runtime");
            try
            {
                JavaRuntimeProvisionService.PublishRuntimeDirectory(
                    missingPrepared,
                    target);
                throw new Exception("Publication failure fixture unexpectedly succeeded.");
            }
            catch (DirectoryNotFoundException)
            {
            }

            Assert(Directory.Exists(target),
                "Previous runtime directory should be restored after publication failure.");
            Assert(File.ReadAllText(Path.Combine(target, "old-runtime.txt")) == "old",
                "Previous runtime contents should survive failed replacement.");
            Assert(File.Exists(Path.Combine(target, "nexo-runtime.json")),
                "Previous runtime manifest should survive failed replacement.");
            Assert(!Directory.EnumerateDirectories(
                    root,
                    "temurin-8-linux-x64.rollback-*",
                    SearchOption.TopDirectoryOnly).Any(),
                "Successful rollback should not leave a rollback directory behind.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TestSuccessfulSwapPublishesPreparedRuntime()
    {
        var root = NewRoot("success");
        try
        {
            var target = Path.Combine(root, "temurin-8-linux-x64");
            var prepared = Path.Combine(root, "prepared");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(prepared);
            File.WriteAllText(Path.Combine(target, "old-runtime.txt"), "old");
            File.WriteAllText(Path.Combine(target, "nexo-runtime.json"), "{\"major\":8,\"version\":\"old\"}");
            File.WriteAllText(Path.Combine(prepared, "new-runtime.txt"), "new");
            File.WriteAllText(Path.Combine(prepared, "nexo-runtime.json"), "{\"major\":8,\"version\":\"new\"}");

            JavaRuntimeProvisionService.PublishRuntimeDirectory(
                prepared,
                target);

            Assert(!Directory.Exists(prepared),
                "Prepared runtime should be moved into the canonical target.");
            Assert(File.Exists(Path.Combine(target, "new-runtime.txt")),
                "New runtime should be visible after successful publication.");
            Assert(!File.Exists(Path.Combine(target, "old-runtime.txt")),
                "Old runtime contents must not remain mixed into the replacement.");
            Assert(File.ReadAllText(Path.Combine(target, "nexo-runtime.json"))
                   .Contains("\"version\":\"new\"", StringComparison.Ordinal),
                "Published runtime and its complete manifest should appear together.");
            Assert(!Directory.EnumerateDirectories(
                    root,
                    "temurin-8-linux-x64.rollback-*",
                    SearchOption.TopDirectoryOnly).Any(),
                "Successful publication should clean its rollback directory.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string NewRoot(string suffix)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-runtime-publish-tests",
            suffix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
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
}
