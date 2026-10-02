using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class FailureNoticeThrottleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-02-07T10:00:00Z");
    private static readonly DateTimeOffset AlmostTenMinutesLater = Now.AddMinutes(10).AddSeconds(-1);

    private readonly FailureNoticeThrottle _throttle = new();

    [Fact]
    public void One_notice_per_place_every_ten_minutes()
    {
        _throttle.TryAcquire(999, -100, 7, Now).ShouldBeTrue();
        _throttle.TryAcquire(999, -100, 7, AlmostTenMinutesLater).ShouldBeFalse();
        _throttle.TryAcquire(999, -100, 7, Now.AddMinutes(10)).ShouldBeTrue();
    }

    [Fact]
    public void Expired_places_are_allowed_again_exactly_once()
    {
        for (var chat = 1; chat <= 50; chat++)
        {
            _throttle.TryAcquire(999, -chat, null, Now).ShouldBeTrue();
        }

        var later = Now.AddHours(1);
        for (var chat = 1; chat <= 50; chat++)
        {
            _throttle.TryAcquire(999, -chat, null, later).ShouldBeTrue();
            _throttle.TryAcquire(999, -chat, null, later).ShouldBeFalse();
        }
    }

    [Fact]
    public void Expired_keys_are_removed_from_memory()
    {
        _throttle.TryAcquire(999, -1, null, Now).ShouldBeTrue();
        _throttle.TryAcquire(999, -2, null, Now).ShouldBeTrue();

        _throttle.TryAcquire(999, -3, null, Now.AddHours(1)).ShouldBeTrue();

        _throttle.TrackedPlaces.ShouldBe(1);
    }

    [Fact]
    public void Other_topics_are_independent()
    {
        _throttle.TryAcquire(999, -100, 7, Now).ShouldBeTrue();

        _throttle.TryAcquire(999, -100, 8, AlmostTenMinutesLater).ShouldBeTrue();
        _throttle.TryAcquire(999, -100, null, AlmostTenMinutesLater).ShouldBeTrue();
        _throttle.TryAcquire(999, -100, null, AlmostTenMinutesLater).ShouldBeFalse();
    }

    [Fact]
    public void Other_bots_and_chats_are_independent()
    {
        _throttle.TryAcquire(999, -100, 7, Now).ShouldBeTrue();

        _throttle.TryAcquire(1001, -100, 7, AlmostTenMinutesLater).ShouldBeTrue();
        _throttle.TryAcquire(999, -200, 7, AlmostTenMinutesLater).ShouldBeTrue();
        _throttle.TryAcquire(999, -100, 7, AlmostTenMinutesLater).ShouldBeFalse();
    }
}
