namespace JvJvMediaManager.SmokeTests.Harness;

internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace(string label)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "jvjvmm-smoke",
            $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Root = root;
    }

    public string Root { get; }

    public string Combine(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = Root;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Path.Combine(all);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
