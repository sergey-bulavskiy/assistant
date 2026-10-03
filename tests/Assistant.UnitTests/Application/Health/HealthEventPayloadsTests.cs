using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class HealthEventPayloadsTests
{
    [Theory]
    [InlineData("{\"value\":7.8,\"context\":\"other\"}", "{\"context\": \"other\", \"value\": 7.8}", true)]
    [InlineData("{\"value\":7.8,\"context\":\"other\"}", "{\"value\":7.80,\"context\":\"other\"}", true)]
    [InlineData("{\"kg\":60}", "{\"kg\": 60.0}", true)]
    [InlineData("{\"systolic\":150,\"diastolic\":95,\"pulse\":null}", "{\"pulse\": null, \"diastolic\": 95, \"systolic\": 150}", true)]
    [InlineData("{\"value\":7.8,\"context\":\"other\"}", "{\"value\":7.9,\"context\":\"other\"}", false)]
    [InlineData("{\"value\":7.8,\"context\":\"other\"}", "{\"value\":7.8,\"context\":\"fasting\"}", false)]
    [InlineData("{\"code\":\"headache\",\"text\":\"болит голова\"}", "{\"code\":\"headache\",\"text\":\"болит голова сильно\"}", false)]
    [InlineData("{\"kg\":60}", "{\"kg\":60,\"extra\":1}", false)]
    [InlineData("not json", "{\"kg\":60}", false)]
    [InlineData("{\"kg\":60}", "", false)]
    public void SameJson_compares_documents_not_text(string left, string right, bool expected)
    {
        HealthEventPayloads.SameJson(left, right).ShouldBe(expected);
    }
}
