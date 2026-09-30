using Assistant.Application.Common;
using Assistant.Infrastructure.Llm;

namespace Assistant.UnitTests.Infrastructure.Llm;

public class ModelAvailabilityTests
{
    // A settable fake, local to this test class: the existing shared fakes
    // (Fakes/FixedClock.cs, IntegrationTests/Host/TestClock.cs) are either immutable or tied to real
    // wall-clock time: neither lets a test jump to an exact instant after construction, which this
    // suite needs to prove "available again once retry_at has passed" deterministically.
    private sealed class MutableClock : IClock
    {
        public MutableClock(DateTimeOffset now) => UtcNow = now;
        public DateTimeOffset UtcNow { get; set; }
    }

    [Fact]
    public void A_model_never_marked_unavailable_is_available()
    {
        var availability = new ModelAvailability(new MutableClock(DateTimeOffset.UtcNow));

        availability.IsAvailable("sonnet").ShouldBeTrue();
        availability.RetryAt("sonnet").ShouldBeNull();
    }

    [Fact]
    public void Marking_unavailable_hides_the_model_until_the_given_time()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(now);
        var availability = new ModelAvailability(clock);

        availability.MarkUnavailable("sonnet", now.AddMinutes(30));

        availability.IsAvailable("sonnet").ShouldBeFalse();
        availability.RetryAt("sonnet").ShouldBe(now.AddMinutes(30));
    }

    [Fact]
    public void A_model_becomes_available_again_once_the_clock_passes_retry_at()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(now);
        var availability = new ModelAvailability(clock);
        availability.MarkUnavailable("sonnet", now.AddMinutes(30));

        clock.UtcNow = now.AddMinutes(31);

        availability.IsAvailable("sonnet").ShouldBeTrue();
        availability.RetryAt("sonnet").ShouldBeNull();
    }

    [Fact]
    public void MarkAvailable_clears_an_existing_unavailability_mark()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var availability = new ModelAvailability(new MutableClock(now));
        availability.MarkUnavailable("sonnet", now.AddYears(1)); // spec §8.10: "unavailable until installed" has no natural expiry

        availability.MarkAvailable("sonnet");

        availability.IsAvailable("sonnet").ShouldBeTrue();
        availability.RetryAt("sonnet").ShouldBeNull();
    }

    [Fact]
    public void MarkAvailable_on_a_model_that_was_never_marked_unavailable_is_a_no_op()
    {
        var availability = new ModelAvailability(new MutableClock(DateTimeOffset.UtcNow));

        availability.MarkAvailable("sonnet"); // must not throw

        availability.IsAvailable("sonnet").ShouldBeTrue();
    }
}
