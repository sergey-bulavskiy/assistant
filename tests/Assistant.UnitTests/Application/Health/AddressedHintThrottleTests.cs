using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class AddressedHintThrottleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-02-07T10:00:00Z");
    private static readonly DateTimeOffset AlmostFiveMinutesLater = Now.AddMinutes(5).AddSeconds(-1);

    private readonly AddressedHintThrottle _throttle = new();

    [Fact]
    public void One_hint_per_place_every_five_minutes()
    {
        _throttle.TryAcquire(999, -100, 7, Now).ShouldBeTrue();
        _throttle.TryAcquire(999, -100, 7, AlmostFiveMinutesLater).ShouldBeFalse();
        _throttle.TryAcquire(999, -100, 7, Now.AddMinutes(5)).ShouldBeTrue();
    }

    [Fact]
    public void Other_bots_chats_and_topics_are_independent()
    {
        _throttle.TryAcquire(999, -100, 7, Now).ShouldBeTrue();

        _throttle.TryAcquire(1001, -100, 7, AlmostFiveMinutesLater).ShouldBeTrue();
        _throttle.TryAcquire(999, -200, 7, AlmostFiveMinutesLater).ShouldBeTrue();
        _throttle.TryAcquire(999, -100, 8, AlmostFiveMinutesLater).ShouldBeTrue();
        _throttle.TryAcquire(999, -100, null, AlmostFiveMinutesLater).ShouldBeTrue();
        _throttle.TryAcquire(999, -100, 7, AlmostFiveMinutesLater).ShouldBeFalse();
    }

    [Fact]
    public void Expired_keys_are_removed_from_memory()
    {
        _throttle.TryAcquire(999, -1, null, Now).ShouldBeTrue();
        _throttle.TryAcquire(999, -2, null, Now).ShouldBeTrue();

        _throttle.TryAcquire(999, -3, null, Now.AddHours(1)).ShouldBeTrue();

        _throttle.TrackedPlaces.ShouldBe(1);
    }
}
