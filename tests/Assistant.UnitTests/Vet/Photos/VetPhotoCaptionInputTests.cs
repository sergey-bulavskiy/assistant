using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;

namespace Assistant.UnitTests.Vet.Photos;

public sealed class VetPhotoCaptionInputTests
{
    private const string Caption = "{\"intent\":\"record\",\"value\":null,\"unit\":\"mmol/L\",\"year\":2031,\"month\":5,\"day\":11,\"time\":\"10:01\",\"offset\":\"+00:00\"}";
    private const string Root = "{\"needs_reply\":true,\"events\":[{\"type\":\"insulin\",\"intent\":\"question_only\",\"dose\":\"0.125\",\"unit\":\"U\",\"time_evidence\":\"unknown\"}],\"unclear\":[]";
    private static VetPhotoCaptionInput? Parse(string json)
    { using var document = JsonDocument.Parse(json); return VetPhotoCaptionInputParser.Parse(document.RootElement); }

    [Theory]
    [InlineData("record")]
    [InlineData("question_only")]
    [InlineData("unsure")]
    public void Closed_caption_intent_keeps_explicit_partial_context_without_model_mutation_authority(string intent)
    {
        var input = Parse(Caption.Replace("\"record\"", JsonSerializer.Serialize(intent))).ShouldNotBeNull();
        input.Intent.ShouldBe(intent); input.Context.ShouldBe(new VetPhotoContext(null, "mmol/L", 2031, 5, 11, "10:01", "+00:00"));
        input.Context.CorrectionApproved.ShouldBeFalse(); input.Context.PreservedTime.ShouldBeNull(); input.Context.SelectedDisplayIndex.ShouldBeNull();
        var evidence = new VetPhotoCaptionEvidence("written", new(false, [], [], null, null) { PhotoCaption = input }, null);
        var (context, failure) = VetPhotoCaptionContext.Read(evidence);
        if (intent == "record") { context.ShouldBe(input.Context); failure.ShouldBeNull(); }
        else { context.ShouldBe(new VetPhotoContext()); failure.ShouldBe("uncertain_caption_reading"); }
    }

    [Theory]
    [InlineData("5.012500", "5.0125")]
    [InlineData("5,0125", "5.0125")]
    [InlineData("0.000125", "0.000125")]
    public void Caption_decimal_is_an_exact_string_and_never_rounds_the_original_evidence(string value, string number)
    {
        var input = Parse(Caption.Replace("\"value\":null", "\"value\":" + JsonSerializer.Serialize(value))).ShouldNotBeNull();
        input.Context.RawValue.ShouldBe(value); VetInterpretationParser.TryPositiveDecimal(input.Context.RawValue, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(decimal.Parse(number, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("unknown_intent")]
    [InlineData("unknown_key")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("number_value")]
    [InlineData("quoted_year")]
    [InlineData("fractional_month")]
    [InlineData("zero_day")]
    [InlineData("year_10000")]
    [InlineData("month_13")]
    [InlineData("day_32")]
    [InlineData("clock")]
    [InlineData("offset")]
    [InlineData("offset_14_01")]
    [InlineData("unit_41")]
    [InlineData("value_101")]
    [InlineData("negative")]
    [InlineData("zero")]
    [InlineData("nul")]
    [InlineData("surrogate")]
    [InlineData("restoration")]
    [InlineData("preserved_time")]
    [InlineData("selected_display")]
    [InlineData("not_object")]
    public void Malformed_or_injected_caption_rejects_the_entire_mixed_TEXT_interpretation(string malformed)
    {
        var bad = malformed switch
        {
            "unknown_intent" => Caption.Replace("\"record\"", "\"execute\""),
            "unknown_key" => Caption.Replace("\"intent\"", "\"instruction\""),
            "duplicate" => Caption.Replace("\"intent\":\"record\"", "\"intent\":\"record\",\"intent\":\"record\""),
            "missing" => Caption.Replace("\"value\":null,", ""),
            "number_value" => Caption.Replace("\"value\":null", "\"value\":5.01"),
            "quoted_year" => Caption.Replace("2031", "\"2031\""),
            "fractional_month" => Caption.Replace("\"month\":5", "\"month\":5.5"),
            "zero_day" => Caption.Replace("\"day\":11", "\"day\":0"),
            "year_10000" => Caption.Replace("2031", "10000"),
            "month_13" => Caption.Replace("\"month\":5", "\"month\":13"),
            "day_32" => Caption.Replace("\"day\":11", "\"day\":32"),
            "clock" => Caption.Replace("10:01", "24:00"),
            "offset" => Caption.Replace("+00:00", "+15:00"),
            "offset_14_01" => Caption.Replace("+00:00", "+14:01"),
            "unit_41" => Caption.Replace("mmol/L", new string('x', 41)),
            "value_101" => Caption.Replace("\"value\":null", "\"value\":\"" + new string('1', 101) + "\""),
            "negative" => Caption.Replace("\"value\":null", "\"value\":\"-1\""),
            "zero" => Caption.Replace("\"value\":null", "\"value\":\"0\""),
            "nul" => Caption.Replace("mmol/L", "x\\u0000"),
            "surrogate" => Caption.Replace("mmol/L", "x\\ud800"),
            "restoration" => Caption[..^1] + ",\"restore_requested\":true}",
            "preserved_time" => Caption[..^1] + ",\"preserved_time\":{}}",
            "selected_display" => Caption[..^1] + ",\"selected_display_index\":0}",
            _ => "[]"
        };
        Parse(bad).ShouldBeNull(); VetInterpretationParser.Parse(Root + ",\"photo_caption\":" + bad + "}").ShouldBeNull();
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("null")]
    [InlineData("valid")]
    public void Photo_caption_preserves_independent_TEXT_needs_reply_and_question_only_insulin(string caption)
    {
        var json = Root + (caption == "absent" ? "" : ",\"photo_caption\":" + (caption == "null" ? "null" : Caption)) + "}";
        var result = VetInterpretationParser.Parse(json).ShouldNotBeNull(); result.NeedsReply.ShouldBeTrue();
        var insulin = result.Events.Single(); insulin.EventType.ShouldBe("insulin"); insulin.Intent.ShouldBe("question_only");
        insulin.RawValue.ShouldBe("0.125"); insulin.Unit.ShouldBe("U"); insulin.TimeEvidence.ShouldBe("unknown");
        if (caption == "valid") result.PhotoCaption.ShouldNotBeNull().Context.RawValue.ShouldBeNull(); else result.PhotoCaption.ShouldBeNull();
        result.PhotoOperation.ShouldBeNull(); result.Operation.ShouldBeNull();
    }

    [Fact]
    public void Nullable_caption_fields_do_not_guess_measurement_clock_year_unit_or_value()
    {
        var input = Parse("{\"intent\":\"record\",\"value\":null,\"unit\":null,\"year\":null,\"month\":null,\"day\":null,\"time\":null,\"offset\":null}").ShouldNotBeNull();
        input.Context.ShouldBe(new VetPhotoContext());
    }

    [Theory]
    [InlineData(1, "-14:00")]
    [InlineData(9999, "+14:00")]
    public void Exact_year_and_offset_boundaries_remain_explicit(int year, string offset)
    {
        var input = Parse(Caption.Replace("2031", year.ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace("+00:00", offset)).ShouldNotBeNull();
        input.Context.Year.ShouldBe(year); input.Context.Offset.ShouldBe(offset);
    }

    [Fact]
    public void Complete_scalar_text_and_exact_value_length_boundaries_survive_without_truncation()
    {
        var unit = string.Concat(Enumerable.Repeat("🐈", 20)); var value = new string('0', 98) + "01";
        unit.Length.ShouldBe(40); value.Length.ShouldBe(100);
        var input = Parse(Caption.Replace("mmol/L", unit).Replace("\"value\":null", "\"value\":" + JsonSerializer.Serialize(value))).ShouldNotBeNull();
        input.Context.Unit.ShouldBe(unit); input.Context.RawValue.ShouldBe(value);
        Parse(Caption.Replace("mmol/L", unit + "x")).ShouldBeNull();
    }

    [Fact]
    public void Duplicate_photo_caption_root_key_refuses_mixed_TEXT_operation()
    {
        VetInterpretationParser.Parse(Root + ",\"photo_caption\":" + Caption + ",\"photo_caption\":null}").ShouldBeNull();
    }
}
