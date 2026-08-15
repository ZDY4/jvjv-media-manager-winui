using System.Diagnostics;

namespace JvJvMediaManager.SmokeTests.Harness;

internal sealed class TestCase
{
    public required string Suite { get; init; }
    public required string Name { get; init; }
    public required Func<Task> Body { get; init; }
}

internal sealed class TestRegistry
{
    private readonly List<TestCase> _cases = new();

    public void Add(string suite, string name, Func<Task> body)
        => _cases.Add(new TestCase { Suite = suite, Name = name, Body = body });

    public void Add(string suite, string name, Action body)
        => _cases.Add(new TestCase
        {
            Suite = suite,
            Name = name,
            Body = () =>
            {
                body();
                return Task.CompletedTask;
            }
        });

    public IReadOnlyList<TestCase> Cases => _cases;
}

internal sealed class TestRunner
{
    private readonly TestRegistry _registry;

    public TestRunner(TestRegistry registry)
    {
        _registry = registry;
    }

    public async Task<int> RunAsync(string? suiteFilter)
    {
        var cases = _registry.Cases
            .Where(c => suiteFilter == null
                || c.Suite.Equals(suiteFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (cases.Count == 0)
        {
            Console.Error.WriteLine($"No tests matched filter '{suiteFilter}'.");
            return 2;
        }

        var passed = 0;
        var failed = 0;
        var overall = Stopwatch.StartNew();

        foreach (var group in cases.GroupBy(c => c.Suite).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"== {group.Key} ==");
            foreach (var test in group)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    await test.Body();
                    sw.Stop();
                    passed++;
                    Console.WriteLine($"  PASS  {test.Name} ({sw.ElapsedMilliseconds}ms)");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    failed++;
                    Console.WriteLine($"  FAIL  {test.Name} ({sw.ElapsedMilliseconds}ms)");
                    Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
                    var trace = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
                    if (!string.IsNullOrWhiteSpace(trace))
                    {
                        Console.WriteLine($"        {trace}");
                    }
                }
            }
        }

        overall.Stop();
        Console.WriteLine();
        Console.WriteLine($"Total: {passed + failed}, Passed: {passed}, Failed: {failed}, ElapsedMs: {overall.ElapsedMilliseconds}");
        return failed == 0 ? 0 : 1;
    }
}
