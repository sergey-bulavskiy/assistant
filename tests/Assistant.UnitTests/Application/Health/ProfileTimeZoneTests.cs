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
}
