using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class ProfileTimeZoneTests
{
    private static readonly DateTimeOffset Late = new(2030, 2, 7, 22, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Local_today_uses_the_zone()
    {
        ProfileTimeZone.LocalToday(Late, "Asia/Tokyo").ShouldBe(new DateOnly(2030, 2, 8));
        ProfileTimeZone.LocalToday(Late, "UTC").ShouldBe(new DateOnly(2030, 2, 7));
    }

    [Theory]
    [InlineData("Europe/Berlin", "2030-02-06T23:00:00Z")]
    [InlineData("UTC", "2030-02-07T00:00:00Z")]
    [InlineData("Asia/Tokyo", "2030-02-06T15:00:00Z")]
    public void StartOfDayUtc_is_local_midnight_in_utc(string zone, string expected)
    {
        var start = ProfileTimeZone.StartOfDayUtc(new DateOnly(2030, 2, 7), zone);

        start.ShouldBe(DateTimeOffset.Parse(expected));
        start.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Unknown_zone_falls_back_to_utc()
    {
        ProfileTimeZone.LocalToday(Late, "Mars/Base").ShouldBe(new DateOnly(2030, 2, 7));
    }

    [Theory]
    [InlineData("Europe/Berlin", "Europe/Berlin")]
    [InlineData(" Asia/Tokyo ", "Asia/Tokyo")]
    [InlineData("UTC", "UTC")]
    public void TryNormalize_accepts_area_city_ids_and_utc(string input, string expectedId)
    {
        ProfileTimeZone.TryNormalize(input, out var id).ShouldBeTrue();
        id.ShouldBe(expectedId);
    }

    [Theory]
    [InlineData("Mars/Base")]
    [InlineData("Berlin")]
    [InlineData("W. Europe Standard Time")]
    [InlineData("")]
    public void TryNormalize_rejects_unknown_and_non_area_ids(string input)
    {
        ProfileTimeZone.TryNormalize(input, out _).ShouldBeFalse();
    }

    [Fact]
    public void Find_falls_back_to_utc_for_unknown_ids()
    {
        ProfileTimeZone.Find("Mars/Base").ShouldBe(TimeZoneInfo.Utc);
        TimeZoneInfo.ConvertTime(new DateTimeOffset(2030, 2, 7, 10, 0, 0, TimeSpan.Zero), ProfileTimeZone.Find("Europe/Berlin")).Hour.ShouldBe(11);
    }

    [Fact]
    public void DisplayId_shows_the_stored_id_or_utc()
    {
        ProfileTimeZone.DisplayId("Europe/Berlin").ShouldBe("Europe/Berlin");
        ProfileTimeZone.DisplayId("Mars/Base").ShouldBe("UTC");
    }
}
