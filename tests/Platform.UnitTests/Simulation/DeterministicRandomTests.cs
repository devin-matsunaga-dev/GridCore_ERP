using GridCore.Platform.Simulation;

namespace GridCore.Platform.UnitTests.Simulation;

/// <summary>
/// The generator underneath GridCore's provider simulators. Its own tests because "the same seed
/// gives the same numbers" is the property every one of them depends on, and <see cref="Random"/>
/// is not documented to give it. Subjects here are meter numbers and payment numbers, which is the
/// rule the generator is about: the number the utility knows the thing by, never its id.
/// </summary>
public sealed class DeterministicRandomTests
{
    [Fact]
    public void The_same_seed_gives_the_same_sequence()
    {
        var first = new DeterministicRandom(42);
        var second = new DeterministicRandom(42);

        Assert.Equal(
            Enumerable.Range(0, 20).Select(_ => first.Next()).ToArray(),
            Enumerable.Range(0, 20).Select(_ => second.Next()).ToArray());
    }

    [Fact]
    public void The_same_seed_scope_and_subject_give_the_same_stream() =>
        Assert.Equal(
            DeterministicRandom.For(4471, "2026-08", "MTR-000001").Next(),
            DeterministicRandom.For(4471, "2026-08", "MTR-000001").Next());

    [Theory]
    [InlineData(9999, "2026-08")]
    [InlineData(4471, "2026-09")]
    public void Changing_the_seed_or_the_scope_changes_the_stream(int seed, string scope) =>
        Assert.NotEqual(
            DeterministicRandom.For(4471, "2026-08", "MTR-000001").Next(),
            DeterministicRandom.For(seed, scope, "MTR-000001").Next());

    [Fact]
    public void Different_subjects_get_unrelated_streams() =>
        Assert.NotEqual(
            DeterministicRandom.For(4471, "2026-08", "MTR-000001").Next(),
            DeterministicRandom.For(4471, "2026-08", "MTR-000002").Next());

    [Fact]
    public void Unit_values_stay_inside_zero_to_one()
    {
        var stream = new DeterministicRandom(4471);

        foreach (var _ in Enumerable.Range(0, 1_000))
        {
            var value = stream.NextUnit();

            Assert.InRange(value, 0m, 0.9999999999999999m);
        }
    }

    [Fact]
    public void A_range_is_respected()
    {
        var stream = new DeterministicRandom(4471);

        foreach (var _ in Enumerable.Range(0, 1_000))
        {
            Assert.InRange(stream.NextDecimal(0.75m, 1.25m), 0.75m, 1.25m);
        }
    }

    [Fact]
    public void A_chance_of_nothing_never_happens_and_a_certainty_always_does()
    {
        var stream = new DeterministicRandom(4471);

        foreach (var _ in Enumerable.Range(0, 100))
        {
            Assert.False(stream.Chance(0m));
            Assert.True(stream.Chance(1m));
        }
    }
}
