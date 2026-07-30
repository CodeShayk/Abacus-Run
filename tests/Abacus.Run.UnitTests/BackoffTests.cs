using Abacus.Run.Core;
using FluentAssertions;
using Xunit;

namespace Abacus.Run.UnitTests;

public class BackoffTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(300);

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 32)]
    public void Doubles_per_attempt_without_jitter(int attempt, double expectedSeconds)
        => Backoff.Exponential(attempt, Base, Cap, JitterMode.None)
            .Should().Be(TimeSpan.FromSeconds(expectedSeconds));

    [Fact]
    public void Is_capped()
    {
        TimeSpan delay = Backoff.Exponential(20, Base, Cap, JitterMode.None);
        delay.Should().Be(Cap);
    }

    [Fact]
    public void Large_attempt_counts_do_not_overflow()
    {
        // The exponent is clamped before the shift; without that this overflows to Infinity.
        TimeSpan delay = Backoff.Exponential(int.MaxValue, Base, Cap, JitterMode.None);
        delay.Should().Be(Cap);
        delay.TotalMilliseconds.Should().NotBe(double.PositiveInfinity);
    }

    [Fact]
    public void Full_jitter_stays_within_the_capped_window()
    {
        var random = new Random(12345);
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            TimeSpan delay = Backoff.Exponential(attempt, Base, Cap, JitterMode.Full, random);
            delay.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
            delay.Should().BeLessThanOrEqualTo(Cap);
        }
    }

    [Fact]
    public void Full_jitter_is_deterministic_under_a_seeded_random()
    {
        TimeSpan first = Backoff.Exponential(3, Base, Cap, JitterMode.Full, new Random(42));
        TimeSpan second = Backoff.Exponential(3, Base, Cap, JitterMode.Full, new Random(42));
        first.Should().Be(second);
    }

    [Fact]
    public void Full_jitter_actually_varies()
    {
        var random = new Random(7);
        TimeSpan[] delays = Enumerable.Range(0, 20)
            .Select(_ => Backoff.Exponential(5, Base, Cap, JitterMode.Full, random))
            .ToArray();

        delays.Distinct().Should().HaveCountGreaterThan(1, "jitter must spread retries across replicas");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_non_positive_attempts(int attempt)
    {
        Action act = () => Backoff.Exponential(attempt, Base, Cap);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("attempt");
    }

    [Fact]
    public void Rejects_non_positive_base()
    {
        Action act = () => Backoff.Exponential(1, TimeSpan.Zero, Cap);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("base");
    }

    [Fact]
    public void Rejects_cap_below_base()
    {
        Action act = () => Backoff.Exponential(1, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("cap");
    }

    [Fact]
    public void Is_monotonic_without_jitter()
    {
        TimeSpan previous = TimeSpan.Zero;
        for (int attempt = 1; attempt <= 8; attempt++)
        {
            TimeSpan delay = Backoff.Exponential(attempt, Base, Cap, JitterMode.None);
            delay.Should().BeGreaterThanOrEqualTo(previous);
            previous = delay;
        }
    }
}
