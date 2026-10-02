using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class HealthEventValidatorTests
{
    private static readonly DateTimeOffset Sent = new(2030, 2, 7, 10, 0, 0, TimeSpan.Zero);

    private static EventValidation Validate(ExtractedEvent e, string zone = "UTC", DateTimeOffset? sent = null) =>
        HealthEventValidator.Validate(e, sent ?? Sent, zone);

    private static decimal D(double value) => (decimal)value;

    private static void ShouldBeProblem(EventValidation result, string? fragment, string reason)
    {
        result.Event.ShouldBeNull();
        result.Problem.ShouldNotBeNull();
        result.Problem.Fragment.ShouldBe(fragment);
        result.Problem.Reason.ShouldBe(reason);
    }

    private static ExtractedEvent Glucose(double? value, string? unit = null, string? time = null, int? day = null) =>
        new() { Type = "glucose", Value = value is null ? null : D(value.Value), Unit = unit, Time = time, Day = day };

    [Fact]
    public void Glucose_is_recorded_in_mmol_with_its_context()
    {
        var result = Validate(new ExtractedEvent
        {
            Type = "glucose", Value = 7.8m, Unit = "mmol/L", Context = "after_meal_1h", Time = "09:30"
        });

        result.Problem.ShouldBeNull();
        result.Event.ShouldNotBeNull();
        result.Event.Type.ShouldBe("glucose");
        result.Event.OccurredAt.ShouldBe(new DateTimeOffset(2030, 2, 7, 9, 30, 0, TimeSpan.Zero));
        result.Event.OccurredAt.Offset.ShouldBe(TimeSpan.Zero);
        result.Event.OccurredAtSource.ShouldBe("stated");
        result.Event.PayloadJson.ShouldBe("{\"value\":7.8,\"context\":\"after_meal_1h\"}");
    }

    [Theory]
    [InlineData(null, "other")]
    [InlineData("SOMETHING", "other")]
    [InlineData(" Fasting ", "fasting")]
    public void Glucose_context_is_normalized(string? context, string expected)
    {
        var result = Validate(new ExtractedEvent { Type = "glucose", Value = 5.6m, Context = context });

        result.Event.ShouldNotBeNull();
        result.Event.PayloadJson.ShouldBe("{\"value\":5.6,\"context\":\"" + expected + "\"}");
    }

    [Theory]
    [InlineData("mg/dL")]
    [InlineData("mg/dl")]
    [InlineData("MG/DL")]
    [InlineData("ммоль")]
    public void Glucose_in_other_units_is_not_recorded(string unit)
    {
        ShouldBeProblem(Validate(Glucose(110, unit)), "110", "unit");
    }

    [Theory]
    [InlineData(0.5, null, null, null)]
    [InlineData(35.0, null, null, null)]
    [InlineData(0.4, null, "0.4", "value")]
    [InlineData(35.1, null, "35.1", "unit")]
    [InlineData(36.0, "mmol/L", "36", "value")]
    [InlineData(null, null, null, "value")]
    public void Glucose_bounds(double? value, string? unit, string? fragment, string? reason)
    {
        var result = Validate(Glucose(value, unit));

        if (reason is null)
        {
            result.Problem.ShouldBeNull();
            result.Event.ShouldNotBeNull();
        }
        else
        {
            ShouldBeProblem(result, fragment, reason);
        }
    }

    [Fact]
    public void Insulin_is_recorded_without_judging_the_dose()
    {
        var named = Validate(new ExtractedEvent { Type = "insulin", Units = 6m, Kind = "short", Name = " ExampleName " });
        named.Event.ShouldNotBeNull();
        named.Event.PayloadJson.ShouldBe("{\"kind\":\"short\",\"name\":\"ExampleName\",\"units\":6}");

        var unnamed = Validate(new ExtractedEvent { Type = "insulin", Units = 7.5m, Kind = "rapid", Name = null });
        unnamed.Event.ShouldNotBeNull();
        unnamed.Event.PayloadJson.ShouldBe("{\"kind\":\"unknown\",\"name\":null,\"units\":7.5}");

        var longName = Validate(new ExtractedEvent { Type = "insulin", Units = 6m, Name = new string('n', 150) });
        longName.Event.ShouldNotBeNull();
        var payload = HealthEventPayloads.TryDeserialize<InsulinPayload>(longName.Event.PayloadJson);
        payload.ShouldNotBeNull();
        payload.Name!.Length.ShouldBe(100);
    }

    [Theory]
    [InlineData(0.5, null, null)]
    [InlineData(100.0, null, null)]
    [InlineData(0.4, "0.4", "value")]
    [InlineData(100.5, "100.5", "value")]
    [InlineData(6.3, "6.3", "value")]
    [InlineData(null, null, "value")]
    public void Insulin_bounds(double? units, string? fragment, string? reason)
    {
        var result = Validate(new ExtractedEvent { Type = "insulin", Units = units is null ? null : D(units.Value) });

        if (reason is null)
        {
            result.Problem.ShouldBeNull();
            result.Event.ShouldNotBeNull();
        }
        else
        {
            ShouldBeProblem(result, fragment, reason);
        }
    }

    [Fact]
    public void Meal_needs_a_description()
    {
        var lunch = Validate(new ExtractedEvent { Type = "meal", MealKind = "lunch", Description = " гречка " });
        lunch.Event.ShouldNotBeNull();
        lunch.Event.PayloadJson.ShouldBe("{\"meal_kind\":\"lunch\",\"description\":\"гречка\"}");

        var odd = Validate(new ExtractedEvent { Type = "meal", MealKind = "brunch", Description = "суп" });
        odd.Event.ShouldNotBeNull();
        odd.Event.PayloadJson.ShouldBe("{\"meal_kind\":\"other\",\"description\":\"суп\"}");

        ShouldBeProblem(Validate(new ExtractedEvent { Type = "meal", Description = "   " }), null, "value");

        var long600 = Validate(new ExtractedEvent { Type = "meal", Description = new string('a', 600) });
        long600.Event.ShouldNotBeNull();
        HealthEventPayloads.TryDeserialize<MealPayload>(long600.Event.PayloadJson)!.Description.Length.ShouldBe(500);
    }

    [Fact]
    public void Symptom_code_is_normalized()
    {
        var known = Validate(new ExtractedEvent { Type = "symptom", Code = "headache", Text = "болит голова" });
        known.Event.ShouldNotBeNull();
        known.Event.PayloadJson.ShouldBe("{\"code\":\"headache\",\"text\":\"болит голова\"}");

        var unknown = Validate(new ExtractedEvent { Type = "symptom", Code = "unknown_code", Text = "x" });
        unknown.Event.ShouldNotBeNull();
        HealthEventPayloads.TryDeserialize<SymptomPayload>(unknown.Event.PayloadJson)!.Code.ShouldBe("other");

        var noText = Validate(new ExtractedEvent { Type = "symptom", Code = "headache", Text = null });
        noText.Event.ShouldNotBeNull();
        noText.Event.PayloadJson.ShouldBe("{\"code\":\"headache\",\"text\":\"\"}");

        var longText = Validate(new ExtractedEvent { Type = "symptom", Text = new string('b', 250) });
        longText.Event.ShouldNotBeNull();
        HealthEventPayloads.TryDeserialize<SymptomPayload>(longText.Event.PayloadJson)!.Text.Length.ShouldBe(200);
    }

    [Theory]
    [InlineData(64.5, null, null)]
    [InlineData(30.0, null, null)]
    [InlineData(250.0, null, null)]
    [InlineData(29.9, "29.9", "value")]
    [InlineData(250.1, "250.1", "value")]
    [InlineData(null, null, "value")]
    public void Weight_bounds(double? kg, string? fragment, string? reason)
    {
        var result = Validate(new ExtractedEvent { Type = "weight", Kg = kg is null ? null : D(kg.Value) });

        if (reason is null)
        {
            result.Problem.ShouldBeNull();
            result.Event.ShouldNotBeNull();
        }
        else
        {
            ShouldBeProblem(result, fragment, reason);
        }
    }

    [Fact]
    public void Weight_payload_is_the_kilograms()
    {
        var result = Validate(new ExtractedEvent { Type = "weight", Kg = 64.5m });

        result.Event.ShouldNotBeNull();
        result.Event.PayloadJson.ShouldBe("{\"kg\":64.5}");
    }

    [Fact]
    public void Blood_pressure_is_recorded()
    {
        var withPulse = Validate(new ExtractedEvent { Type = "blood_pressure", Systolic = 128, Diastolic = 84, Pulse = 76 });
        withPulse.Event.ShouldNotBeNull();
        withPulse.Event.PayloadJson.ShouldBe("{\"systolic\":128,\"diastolic\":84,\"pulse\":76}");

        var noPulse = Validate(new ExtractedEvent { Type = "blood_pressure", Systolic = 128, Diastolic = 84 });
        noPulse.Event.ShouldNotBeNull();
        noPulse.Event.PayloadJson.ShouldBe("{\"systolic\":128,\"diastolic\":84,\"pulse\":null}");

        Validate(new ExtractedEvent { Type = "blood_pressure", Systolic = 60, Diastolic = 30 }).Event.ShouldNotBeNull();
        Validate(new ExtractedEvent { Type = "blood_pressure", Systolic = 260, Diastolic = 160 }).Event.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(59, 40, null, "59/40")]
    [InlineData(261, 100, null, "261/100")]
    [InlineData(120, 29, null, "120/29")]
    [InlineData(120, 161, null, "120/161")]
    [InlineData(80, 80, null, "80/80")]
    [InlineData(120.5, 80, null, "120.5/80")]
    [InlineData(120, 80, 29, "120/80")]
    [InlineData(120, 80, 221, "120/80")]
    public void Blood_pressure_bounds(double systolic, double diastolic, int? pulse, string fragment)
    {
        var result = Validate(new ExtractedEvent
        {
            Type = "blood_pressure", Systolic = D(systolic), Diastolic = D(diastolic), Pulse = pulse
        });

        ShouldBeProblem(result, fragment, "value");
    }

    [Fact]
    public void Blood_pressure_without_systolic_asks_for_the_value()
    {
        ShouldBeProblem(Validate(new ExtractedEvent { Type = "blood_pressure", Diastolic = 80 }), null, "value");
    }

    [Fact]
    public void Message_time_is_used_when_no_time_is_stated()
    {
        var result = Validate(new ExtractedEvent { Type = "glucose", Value = 5.6m });

        result.Event.ShouldNotBeNull();
        result.Event.OccurredAt.ShouldBe(Sent);
        result.Event.OccurredAtSource.ShouldBe("message");
    }

    [Fact]
    public void Yesterday_with_a_time_is_that_local_day()
    {
        var result = Validate(new ExtractedEvent { Type = "glucose", Value = 5.6m, Day = -1, Time = "22:00" });

        result.Event.ShouldNotBeNull();
        result.Event.OccurredAt.ShouldBe(new DateTimeOffset(2030, 2, 6, 22, 0, 0, TimeSpan.Zero));
        result.Event.OccurredAtSource.ShouldBe("stated");
    }

    [Fact]
    public void Yesterday_without_a_time_keeps_the_message_time_of_day()
    {
        var result = Validate(new ExtractedEvent { Type = "glucose", Value = 5.6m, Day = -1 });

        result.Event.ShouldNotBeNull();
        result.Event.OccurredAt.ShouldBe(new DateTimeOffset(2030, 2, 6, 10, 0, 0, TimeSpan.Zero));
        result.Event.OccurredAtSource.ShouldBe("stated");
    }

    [Fact]
    public void The_stated_time_is_read_in_the_profile_zone()
    {
        var berlin = Validate(new ExtractedEvent { Type = "glucose", Value = 5.6m, Time = "09:30" }, "Europe/Berlin");
        berlin.Event.ShouldNotBeNull();
        berlin.Event.OccurredAt.ShouldBe(new DateTimeOffset(2030, 2, 7, 8, 30, 0, TimeSpan.Zero));

        // 22:30 UTC is already Feb 8, 07:30 in Tokyo: "07:00" means that morning.
        var tokyo = Validate(
            new ExtractedEvent { Type = "glucose", Value = 5.6m, Time = "07:00" },
            "Asia/Tokyo",
            new DateTimeOffset(2030, 2, 7, 22, 30, 0, TimeSpan.Zero));
        tokyo.Event.ShouldNotBeNull();
        tokyo.Event.OccurredAt.ShouldBe(new DateTimeOffset(2030, 2, 7, 22, 0, 0, TimeSpan.Zero));

        var unknown = Validate(new ExtractedEvent { Type = "glucose", Value = 5.6m, Time = "09:30" }, "Mars/Base");
        unknown.Event.ShouldNotBeNull();
        unknown.Event.OccurredAt.ShouldBe(new DateTimeOffset(2030, 2, 7, 9, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_single_digit_hour_is_accepted()
    {
        var result = Validate(new ExtractedEvent { Type = "glucose", Value = 5.6m, Time = "7:05" });

        result.Event.ShouldNotBeNull();
        result.Event.OccurredAt.ShouldBe(new DateTimeOffset(2030, 2, 7, 7, 5, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(0, "10:10", null, null)]
    [InlineData(0, "10:11", "10:11", "time")]
    [InlineData(-30, "10:00", null, null)]
    [InlineData(-30, "09:59", "09:59", "time")]
    [InlineData(-31, null, null, "time")]
    [InlineData(1, null, null, "time")]
    [InlineData(0, "25:00", "25:00", "time")]
    [InlineData(0, "7 утра", "7 утра", "time")]
    public void Times_outside_the_window_are_unclear(int day, string? time, string? fragment, string? reason)
    {
        var result = Validate(new ExtractedEvent { Type = "glucose", Value = 5.6m, Day = day, Time = time });

        if (reason is null)
        {
            result.Problem.ShouldBeNull();
            result.Event.ShouldNotBeNull();
        }
        else
        {
            ShouldBeProblem(result, fragment, reason);
        }
    }

    [Fact]
    public void A_local_time_skipped_by_a_clock_change_is_unclear()
    {
        // 2030-03-31 02:30 does not exist in Berlin (clocks jump from 02:00 to 03:00).
        var result = Validate(
            new ExtractedEvent { Type = "glucose", Value = 5.6m, Time = "02:30" },
            "Europe/Berlin",
            new DateTimeOffset(2030, 3, 31, 5, 0, 0, TimeSpan.Zero));

        ShouldBeProblem(result, "02:30", "time");
    }

    [Fact]
    public void A_local_time_repeated_by_a_clock_change_is_unclear()
    {
        // 2030-10-27 02:30 occurs twice in Berlin (clocks go back from 03:00 to 02:00).
        var result = Validate(
            new ExtractedEvent { Type = "glucose", Value = 5.6m, Time = "02:30" },
            "Europe/Berlin",
            new DateTimeOffset(2030, 10, 27, 10, 0, 0, TimeSpan.Zero));

        ShouldBeProblem(result, "02:30", "time");
    }

    [Fact]
    public void Payload_problems_win_over_time_problems()
    {
        var result = Validate(new ExtractedEvent { Type = "glucose", Value = 110m, Unit = "mg/dL", Time = "25:00" });

        result.Problem.ShouldNotBeNull();
        result.Problem.Reason.ShouldBe("unit");
    }

    [Fact]
    public void Unknown_type_is_unclear()
    {
        ShouldBeProblem(Validate(new ExtractedEvent { Type = "lab" }), null, "type");
    }
}
