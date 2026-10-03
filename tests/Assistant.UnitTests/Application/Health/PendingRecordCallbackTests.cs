using System.Text;
using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class PendingRecordCallbackTests
{
    [Fact]
    public void Yes_and_no_round_trip()
    {
        PendingRecordCallback.Format(accept: true, 42).ShouldBe("rec_yes:42");
        PendingRecordCallback.Format(accept: false, 42).ShouldBe("rec_no:42");

        PendingRecordCallback.TryParse("rec_yes:42", out var yes, out var yesId).ShouldBeTrue();
        yes.ShouldBeTrue();
        yesId.ShouldBe(42);
        PendingRecordCallback.TryParse("rec_no:42", out var no, out var noId).ShouldBeTrue();
        no.ShouldBeFalse();
        noId.ShouldBe(42);
    }

    [Theory]
    [InlineData("")]
    [InlineData("rec_yes:")]
    [InlineData("rec_yes:0")]
    [InlineData("rec_yes:-1")]
    [InlineData("rec_yes: 1")]
    [InlineData("rec_yes:12x")]
    [InlineData("rec_maybe:1")]
    [InlineData("place_approve:1")]
    public void Anything_else_is_rejected(string data)
    {
        PendingRecordCallback.TryParse(data, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void The_largest_id_fits_the_telegram_limit()
    {
        Encoding.UTF8.GetByteCount(PendingRecordCallback.Format(accept: true, long.MaxValue)).ShouldBeLessThanOrEqualTo(64);
    }
}
