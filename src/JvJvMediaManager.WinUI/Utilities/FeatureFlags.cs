namespace JvJvMediaManager.Utilities;

/// <summary>
/// Central, environment-variable backed feature flags for risk-gated changes.
/// Runtime-sensitive optimizations default to the validated behavior; set the
/// matching env var to 0 to force the legacy path.
/// </summary>
public static class FeatureFlags
{
    private const string KeysetPaginationVariable = "JVJVMM_ENABLE_KEYSET_PAGINATION";
    private const string ReusePlayerVariable = "JVJVMM_ENABLE_PLAYER_REUSE";

    /// <summary>
    /// WP-08/WP-C: when enabled, the media list uses composite keyset pagination
    /// for stable sort fields instead of OFFSET. Enabled by default after the
    /// db-keyset scale contract tests (16k rows, all supported sorts/directions,
    /// filtered queries) passed; set JVJVMM_ENABLE_KEYSET_PAGINATION=0 to force
    /// the legacy OFFSET fallback at runtime. When a cursor is unavailable the
    /// page store still falls back to OFFSET automatically.
    /// </summary>
    public static bool KeysetPaginationEnabled { get; } = IsEnabled(KeysetPaginationVariable, defaultValue: true);

    /// <summary>
    /// WP-07: reuse a single MediaPlayer and only swap the source, instead of
    /// rebuilding the native decoder/GPU session per video. Enabled by default;
    /// set JVJVMM_ENABLE_PLAYER_REUSE=0 to force replace-per-source.
    /// </summary>
    public static bool ReusePlayerEnabled { get; } = IsEnabled(ReusePlayerVariable, defaultValue: true);

    private static bool IsEnabled(string variable, bool defaultValue = false)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        value = value.Trim();
        if (value is "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase) || value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value is "1"
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}
