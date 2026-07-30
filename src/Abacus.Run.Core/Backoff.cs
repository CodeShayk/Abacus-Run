namespace Abacus.Run.Core;

/// <summary>Exponential backoff with an absolute cap and optional full jitter.</summary>
public static class Backoff
{
    /// <summary>
    /// Delay before <paramref name="attempt"/> is retried. Attempt 1 yields the base delay.
    /// </summary>
    /// <param name="attempt">1-based attempt number that just failed.</param>
    /// <param name="random">Supply a seeded instance for deterministic tests.</param>
    public static TimeSpan Exponential(
        int attempt, TimeSpan @base, TimeSpan cap, JitterMode jitter = JitterMode.Full, Random? random = null)
    {
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt), "Attempt is 1-based.");
        if (@base <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(@base));
        if (cap < @base) throw new ArgumentOutOfRangeException(nameof(cap), "Cap must be at least the base delay.");

        // Exponent is clamped before the shift so a large attempt count cannot overflow the double.
        int exponent = Math.Min(attempt - 1, 32);
        double scaled = @base.TotalMilliseconds * Math.Pow(2, exponent);
        double capped = Math.Min(scaled, cap.TotalMilliseconds);

        if (jitter == JitterMode.None)
        {
            return TimeSpan.FromMilliseconds(capped);
        }

        double jittered = (random ?? Random.Shared).NextDouble() * capped;
        return TimeSpan.FromMilliseconds(jittered);
    }
}
