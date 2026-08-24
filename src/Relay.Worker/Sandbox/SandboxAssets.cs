using System.Reflection;

namespace Relay.Worker.Sandbox;

/// <summary>Extracts bundled helper scripts so sandboxes can mount or execute them.</summary>
public static class SandboxAssets
{
    public static string EnsureExtracted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "relay-assets");
        Directory.CreateDirectory(dir);
        Extract(dir, "Relay.Worker.Resources.mock-agent.sh", "mock-agent.sh");
        Extract(dir, "Relay.Worker.Resources.mock-tests.sh", "mock-tests.sh");
        if (!OperatingSystem.IsWindows())
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                File.SetUnixFileMode(f,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return dir;
    }

    private static void Extract(string dir, string resourceName, string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {resourceName}");
        var target = Path.Combine(dir, fileName);
        using var file = File.Create(target);
        stream.CopyTo(file);
    }
}
