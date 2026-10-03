using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class QuickReadingScannerTests
{
    private static decimal D(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("сахар 2.5", "2.5")]
    [InlineData("Сахара было 3,4 утром", "3.4")]
    [InlineData("ГЛЮКОЗА: 12", "12")]
    [InlineData("глюкоза натощак 5.1", "5.1")]
    [InlineData("сахар12.5", "12.5")]
    [InlineData("сахар 2.55", "2.55")]
    [InlineData("сахар 7,85 после ужина", "7.85")]
    [InlineData("глюкометр показал 2.5", "2.5")]
    [InlineData("Глюкометр: 3,15", "3.15")]
    [InlineData("сахар утром после пробуждения был 2.5", "2.5")]
    [InlineData("сахар 5 ммоль", "5")]
    public void Finds_glucose_after_the_keyword(string text, string value)
    {
        var hit = QuickReadingScanner.Scan(text).ShouldHaveSingleItem();

        hit.Type.ShouldBe("glucose");
        hit.Value.ShouldBe(D(value));
        hit.Context.ShouldBe("other");
    }

    [Fact]
    public void The_number_must_follow_within_thirty_characters()
    {
        var near = QuickReadingScanner.Scan("сахар" + new string(' ', 30) + "3.5").ShouldHaveSingleItem();

        near.Value.ShouldBe(3.5m);
        QuickReadingScanner.Scan("сахар" + new string(' ', 31) + "3.5").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("сахар 250", "250")]
    [InlineData("глюкоза 125", "125")]
    [InlineData("сахар 45", "45")]
    public void Finds_large_glucose_numbers_for_the_caller_to_question(string text, string value) =>
        QuickReadingScanner.Scan(text).ShouldHaveSingleItem().Value.ShouldBe(D(value));

    [Theory]
    [InlineData("сахар 1250")]
    [InlineData("сахар 7.855")]
    [InlineData("у неё 2.5 утром")]
    [InlineData("сахар в норме, вес 65.5")]
    [InlineData("сахар в норме. Съела 2 яблока")]
    [InlineData("сахар после еды через 2 часа")]
    [InlineData("сахар 100 г на торт")]
    [InlineData("сахар: 2 ед инсулина")]
    [InlineData("в 2.5 раза меньше сахара")]
    [InlineData("сахар в норме")]
    [InlineData("болит голова")]
    [InlineData("")]
    public void Ignores_numbers_it_cannot_read_safely(string text) =>
        QuickReadingScanner.Scan(text).ShouldBeEmpty();

    [Theory]
    [InlineData("давление 150/95", 150, 95)]
    [InlineData("150 на 95", 150, 95)]
    [InlineData("АД 150 / 95", 150, 95)]
    [InlineData("165 НА 100", 165, 100)]
    public void Finds_blood_pressure(string text, int systolic, int diastolic)
    {
        var hit = QuickReadingScanner.Scan(text).ShouldHaveSingleItem();

        hit.Type.ShouldBe("blood_pressure");
        hit.Systolic.ShouldBe(systolic);
        hit.Diastolic.ShouldBe(diastolic);
    }

    [Fact]
    public void Pressure_after_a_glucose_word_is_not_glucose()
    {
        var hit = QuickReadingScanner.Scan("сахар норм, давление 150/95").ShouldHaveSingleItem();
        hit.Type.ShouldBe("blood_pressure");

        QuickReadingScanner.Scan("сахар 150/95").ShouldHaveSingleItem().Type.ShouldBe("blood_pressure");
    }

    [Theory]
    [InlineData("1/2")]
    [InlineData("1500/95")]
    [InlineData("150/9")]
    public void Pressure_needs_two_or_three_digits_each(string text) =>
        QuickReadingScanner.Scan(text).ShouldBeEmpty();

    [Fact]
    public void Returns_glucose_hits_then_pressure_hits()
    {
        var hits = QuickReadingScanner.Scan("давление 165/100 и сахар 2.5");

        hits.Count.ShouldBe(2);
        hits[0].Type.ShouldBe("glucose");
        hits[0].Value.ShouldBe(2.5m);
        hits[1].Type.ShouldBe("blood_pressure");
        hits[1].Systolic.ShouldBe(165);
        hits[1].Diastolic.ShouldBe(100);
    }

    [Fact]
    public void Hits_are_not_validated_here()
    {
        QuickReadingScanner.Scan("сахар 0.3").ShouldHaveSingleItem().Value.ShouldBe(0.3m);

        var pressure = QuickReadingScanner.Scan("давление 50/40").ShouldHaveSingleItem();
        pressure.Systolic.ShouldBe(50);
        pressure.Diastolic.ShouldBe(40);
    }
}
