namespace JvJvMediaManager.Utilities;

/// <summary>
/// Central, environment-variable backed feature flags for risk-gated changes.
/// Flags default to the safe/legacy behavior and are opt-in so runtime-sensitive
/// optimizations (WP-07 player reuse, WP-08 keyset pagination) can be validated
/// before becoming default.
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
    /// WP-07: when enabled, the video player reuses a single MediaPlayer and
    /// only swaps the source, instead of rebuilding the player per source.
    /// Off by default; the replace-per-source strategy remains the default.
    /// </summary>
    public static bool ReusePlayerEnabled { get; } = IsEnabled(ReusePlayerVariable);

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
