namespace Assistant.UnitTests;

public class TrivialTests
{
    [Fact]
    public void Build_pipeline_is_wired_up()
    {
        var result = 2 + 2;
        result.ShouldBe(4);
    }
}
