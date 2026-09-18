using System.Runtime.CompilerServices;

namespace NzbWebDAV.Tests.Fakes;

/// <summary>
/// BlobStore and DavDatabaseContext resolve CONFIG_PATH once (static init), so it must
/// point at a scratch directory before any test touches them.
/// </summary>
public static class TestConfig
{
    public static string ConfigPath { get; private set; } = null!;

    [ModuleInitializer]
    public static void Initialize()
    {
        ConfigPath = Path.Combine(Path.GetTempPath(), $"nzbdav-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ConfigPath);
        Environment.SetEnvironmentVariable("CONFIG_PATH", ConfigPath);
    }
}
