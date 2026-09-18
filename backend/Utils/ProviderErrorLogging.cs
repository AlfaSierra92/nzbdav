using Serilog;

namespace NzbWebDAV.Utils;

public static class ProviderErrorLogging
{
    // Read once per process; unknown values preserve the existing logging behavior.
    private static readonly string Mode =
        EnvironmentUtil.GetEnvironmentVariable("PROVIDER_ERROR_LOG_MODE")?.Trim().ToLowerInvariant() ?? "all";

    public static void Warning(bool willRetry, Exception? exception, string messageTemplate, params object?[] values)
    {
        if (Mode == "off" || (Mode == "final" && willRetry)) return;
        Log.Warning(exception, messageTemplate, values);
    }
}
