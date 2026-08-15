using System.Runtime.CompilerServices;

namespace JvJvMediaManager.SmokeTests.Harness;

internal static class Assert
{
    public static void True(bool condition, string message, [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (!condition)
        {
            throw new AssertionException($"Expected true: {message} ({expr}).");
        }
    }

    public static void False(bool condition, string message, [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (condition)
        {
            throw new AssertionException($"Expected false: {message} ({expr}).");
        }
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{message}. Expected='{expected}', Actual='{actual}'.");
        }
    }

    public static void NotNull(object? value, string message)
    {
        if (value is null)
        {
            throw new AssertionException($"Expected non-null: {message}.");
        }
    }

    public static void Null(object? value, string message)
    {
        if (value is not null)
        {
            throw new AssertionException($"Expected null: {message}. Actual='{value}'.");
        }
    }

    public static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string message)
    {
        if (expected.Count != actual.Count)
        {
            throw new AssertionException(
                $"{message}. Length mismatch. Expected={expected.Count}, Actual={actual.Count}. " +
                $"ExpectedItems=[{string.Join(", ", expected)}], ActualItems=[{string.Join(", ", actual)}].");
        }

        for (var i = 0; i < expected.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(expected[i], actual[i]))
            {
                throw new AssertionException(
                    $"{message}. Element {i} mismatch. Expected='{expected[i]}', Actual='{actual[i]}'. " +
                    $"ExpectedItems=[{string.Join(", ", expected)}], ActualItems=[{string.Join(", ", actual)}].");
            }
        }
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new AssertionException($"{message}. Expected {typeof(TException).Name} but got {ex.GetType().Name}: {ex.Message}.");
        }

        throw new AssertionException($"{message}. Expected {typeof(TException).Name} but no exception was thrown.");
    }
}

internal sealed class AssertionException : Exception
{
    public AssertionException(string message)
        : base(message)
    {
    }
}
