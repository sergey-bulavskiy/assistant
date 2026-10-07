using System.Text.RegularExpressions;
using Assistant.Application.Vet.Photos;

namespace Assistant.UnitTests.Vet.Photos;

public sealed class VetPhotoReviewFormatterTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2031-05-12T12:00:00Z");
    private static VetPhotoEffectiveReading Reading(DateTimeOffset? at = null) =>
        new(6.400m, "mmol/L", at ?? At, "2031-05-12 12:00:00", "UTC",
            "image", "batch_default", "batch_year", true);
    private static VetPhotoReviewRow Row(int number, VetPhotoReviewSection section = VetPhotoReviewSection.Clear,
        string? description = null, VetPhotoEffectiveReading? effective = null) =>
        new(number, section, section == VetPhotoReviewSection.Clear ? effective ?? Reading() : effective,
            description ?? $"synthetic complete row [{number:D3}]", $"https://t.me/c/100/{number}");
    private static string Joined(VetPhotoPreviewResult result)
    {
        result.Success.ShouldBeTrue(); result.FailureReason.ShouldBeNull();
        result.Pages.Count.ShouldBeGreaterThan(0);
        result.Pages.ShouldAllBe(p => p.Length > 0 && p.Length <= VetPhotoReviewFormatter.MaxPageChars);
        return string.Join("\n\n", result.Pages);
    }
    private static int[] ItemOrder(string text) => Regex.Matches(text, @"(?m)^#(\d+)")
        .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    private static void Refused(VetPhotoPreviewResult result)
    {
        result.Success.ShouldBeFalse(); result.Pages.ShouldBeEmpty();
        result.FailureReason.ShouldBe("invalid_preview_content");
    }

    [Theory]
    [InlineData(40)]
    [InlineData(50)]
    public void Complete_large_review_keeps_every_full_row_once_across_pages(int count)
    {
        var rows = Enumerable.Range(1, count).Reverse().Select(n => Row(n,
            description: $"synthetic full description [{n:D3}] " + new string('x', 120))).ToArray();
        var result = VetPhotoReviewFormatter.Format("synthetic heading", ["synthetic approved year", "synthetic approved unit"], rows, "synthetic final confirmation");
        var text = Joined(result);
        result.Pages.Count.ShouldBeGreaterThan(1);
        ItemOrder(text).ShouldBe(Enumerable.Range(1, count));
        foreach (var row in rows)
        {
            Regex.Matches(text, Regex.Escape(row.Description)).Count.ShouldBe(1);
            text.ShouldContain($"#{row.ItemNumber}: 6.400 mmol/L\nВремя: 2031-05-12 12:00:00; зона: UTC\nUTC: 2031-05-12 12:00:00Z");
            text.ShouldContain("Основание: значение=по фото; единица=подтверждённые допущения; время=подтверждённый год; профильные допущения=да");
            text.ShouldContain("Источник: " + row.SamePlaceSourceLink);
        }
        text.ShouldStartWith("synthetic heading");
        text.ShouldEndWith("Допущение: synthetic approved year\n\nДопущение: synthetic approved unit\n\nsynthetic final confirmation");
    }

    [Fact]
    public void Clear_UTC_order_then_item_tie_break_precedes_named_unresolved_sections()
    {
        var rows = new[] { Row(8, VetPhotoReviewSection.Failed), Row(6, VetPhotoReviewSection.Duplicates),
            Row(2, effective: Reading(At.AddHours(1))), Row(4, VetPhotoReviewSection.Exceptions),
            Row(1, effective: Reading(At.ToOffset(TimeSpan.FromHours(3)))),
            Row(3, effective: Reading(At)), Row(7, VetPhotoReviewSection.Excluded), Row(5, VetPhotoReviewSection.Exceptions) };
        var text = Joined(VetPhotoReviewFormatter.Format("synthetic heading", [], rows, "synthetic footer"));
        ItemOrder(text).ShouldBe(new[] { 1, 3, 2, 4, 5, 6, 7, 8 });
        foreach (var section in new[] { "Готовые показания", "Требуют уточнения", "Возможные повторы", "Исключены", "Ошибки обработки" })
            Regex.Matches(text, Regex.Escape(section)).Count.ShouldBe(1);
        text.ShouldContain("Требуют уточнения\n#4\nsynthetic complete row [004]");
        text.ShouldContain("Ошибки обработки\n#8\nsynthetic complete row [008]");
        Regex.Matches(text, "UTC:").Count.ShouldBe(3);
    }

    [Fact]
    public void Unresolved_reading_remains_in_its_section_and_shows_exact_effective_evidence()
    {
        var reading = Reading() with { Value = 17.25m, LocalTime = "2031-05-12 15:00:00",
            TimeZoneSnapshot = "+03:00", ValueEvidence = "human_correction", UsesProfileDefaults = false };
        var text = Joined(VetPhotoReviewFormatter.Format("synthetic heading", [],
            [Row(9, VetPhotoReviewSection.Duplicates, "synthetic duplicate decision pending", reading)], "synthetic footer"));
        text.ShouldContain("Возможные повторы\n#9: 17.25 mmol/L\nВремя: 2031-05-12 15:00:00; зона: +03:00");
        text.ShouldContain("значение=исправление пользователя"); text.ShouldContain("профильные допущения=нет");
        text.ShouldNotContain("Готовые показания");
    }

    [Fact]
    public void Exact_page_boundary_and_next_character_preserve_generic_blocks_in_order()
    {
        var exact = new string('a', 3497);
        var first = VetPhotoReviewFormatter.FormatBlocks("H", [exact], "F");
        first.Success.ShouldBeTrue(); first.Pages.ShouldBe(new[] { "H\n\n" + exact, "F" });
        var next = VetPhotoReviewFormatter.FormatBlocks("H", [exact + "b"], "F");
        next.Success.ShouldBeTrue(); next.Pages.ShouldBe(new[] { "H", exact + "b", "F" });
        var maxBlock = new string('z', 3500);
        VetPhotoReviewFormatter.FormatBlocks("H", [maxBlock], "F").Pages.ShouldBe(new[] { "H", maxBlock, "F" });
    }

    [Fact]
    public void Generic_previews_keep_supplied_block_order_and_full_unicode_content()
    {
        var blocks = new[] { "synthetic third revision " + new string('a', 1800),
            "synthetic first correction 😀 " + new string('b', 1800), "synthetic second deletion" };
        var result = VetPhotoReviewFormatter.FormatBlocks("synthetic heading 😀", blocks, "synthetic footer 😀");
        Joined(result).ShouldBe("synthetic heading 😀\n\n" + string.Join("\n\n", blocks) + "\n\nsynthetic footer 😀");
    }

    [Fact]
    public void Surrogate_pair_at_page_boundary_moves_whole_block_without_splitting_or_loss()
    {
        var block = new string('a', 3496) + "😀";
        var result = VetPhotoReviewFormatter.FormatBlocks("H", [block], "F");
        result.Pages.ShouldBe(new[] { "H", block, "F" });
        Joined(result).ShouldContain(block);
        foreach (var page in result.Pages)
            System.Text.Encoding.UTF8.GetString(new System.Text.UTF8Encoding(false, true).GetBytes(page)).ShouldBe(page);
    }

    [Fact]
    public void Assumptions_and_footer_follow_all_rows_and_can_span_complete_final_pages()
    {
        var assumptions = new[] { "synthetic year " + new string('a', 2000), "synthetic zone " + new string('b', 2000) };
        var footer = "synthetic final instruction " + new string('c', 2000);
        var result = VetPhotoReviewFormatter.Format("synthetic heading", assumptions, [Row(1)], footer);
        var text = Joined(result);
        text.IndexOf("Допущение:", StringComparison.Ordinal).ShouldBeGreaterThan(text.IndexOf("Источник:", StringComparison.Ordinal));
        text.ShouldEndWith("Допущение: " + assumptions[0] + "\n\nДопущение: " + assumptions[1] + "\n\n" + footer);
        result.Pages[^1].ShouldBe(footer);
        result.Pages[^2].ShouldBe("Допущение: " + assumptions[1]);
    }

    [Fact]
    public void Empty_rows_preserve_heading_assumptions_footer_and_do_not_invent_readings()
    {
        var result = VetPhotoReviewFormatter.Format("synthetic empty review", ["synthetic assumption"], [], "synthetic footer");
        result.Pages.ShouldBe(new[] { "synthetic empty review\n\nДопущение: synthetic assumption\n\nsynthetic footer" });
        Joined(result).ShouldNotContain("Готовые показания");
        VetPhotoReviewFormatter.FormatBlocks("H", [], "F").Pages.ShouldBe(new[] { "H\n\nF" });
    }

    [Fact]
    public void Missing_optional_link_does_not_invent_one_and_results_are_deterministic_and_immutable()
    {
        var rows = new[] { Row(1) with { SamePlaceSourceLink = null } };
        var first = VetPhotoReviewFormatter.Format("synthetic heading", [], rows, "synthetic footer");
        var next = VetPhotoReviewFormatter.Format("synthetic heading", [], rows, "synthetic footer");
        first.Pages.ShouldBe(next.Pages);
        Joined(first).ShouldNotContain("Источник:");
        var list = (IList<string>)first.Pages;
        list.IsReadOnly.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => list[0] = "synthetic overwritten");
        Joined(first).ShouldContain("synthetic complete row [001]");
    }

    [Fact]
    public void Page_count_matches_persisted_proof_limit_and_next_page_refuses_without_partial_result()
    {
        var block = new string('b', 3500);
        var heading = new string('h', 3500);
        var footer = new string('f', 3500);
        var exact = VetPhotoReviewFormatter.FormatBlocks(heading, Enumerable.Repeat(block, 62).ToArray(), footer);
        exact.Success.ShouldBeTrue(); exact.Pages.Count.ShouldBe(64);
        exact.Pages[0].ShouldBe(heading); exact.Pages[^1].ShouldBe(footer);
        exact.Pages.Skip(1).Take(62).ShouldAllBe(p => p == block);
        Refused(VetPhotoReviewFormatter.FormatBlocks(heading, Enumerable.Repeat(block, 63).ToArray(), footer));
        Refused(VetPhotoReviewFormatter.Format("H", [], Enumerable.Range(1, 51).Select(n => Row(n)).ToArray(), "F"));
    }

    [Theory]
    [InlineData("image", "image", "image_or_caption", "по фото", "по фото", "фото или подпись")]
    [InlineData("human_correction", "human_correction", "human_correction", "исправление пользователя", "исправление пользователя", "исправление пользователя")]
    [InlineData("image", "caption", "batch_year", "по фото", "по подписи", "подтверждённый год")]
    public void Known_evidence_is_rendered_as_readable_labels(string value, string unit, string time,
        string valueLabel, string unitLabel, string timeLabel)
    {
        var reading = Reading() with { ValueEvidence = value, UnitEvidence = unit, TimeEvidence = time };
        var text = Joined(VetPhotoReviewFormatter.Format("H", [], [Row(1, effective: reading)], "F"));
        text.ShouldContain($"Основание: значение={valueLabel}; единица={unitLabel}; время={timeLabel}");
        text.ShouldNotContain("image_or_caption"); text.ShouldNotContain("human_correction");
        text.ShouldNotContain("batch_year"); text.ShouldNotContain("batch_default");
    }

    [Theory]
    [InlineData("duplicate_item")]
    [InlineData("zero_item")]
    [InlineData("negative_item")]
    [InlineData("invalid_section")]
    [InlineData("clear_missing_reading")]
    [InlineData("empty_description")]
    [InlineData("oversized_description")]
    [InlineData("oversized_rendered_row")]
    [InlineData("invalid_surrogate")]
    [InlineData("nul")]
    [InlineData("nonpositive_value")]
    [InlineData("unsupported_unit")]
    [InlineData("missing_local_time")]
    [InlineData("missing_timezone")]
    [InlineData("missing_value_evidence")]
    [InlineData("missing_unit_evidence")]
    [InlineData("missing_time_evidence")]
    [InlineData("unknown_value_evidence")]
    [InlineData("unknown_unit_evidence")]
    [InlineData("unknown_time_evidence")]
    [InlineData("oversized_field")]
    [InlineData("unsafe_link")]
    [InlineData("link_credentials")]
    [InlineData("link_query")]
    [InlineData("null_row")]
    public void Invalid_structured_rows_fail_closed_with_no_preview_pages(string invalid)
    {
        var row = Row(1);
        row = invalid switch {
            "zero_item" => row with { ItemNumber = 0 },
            "negative_item" => row with { ItemNumber = -1 },
            "invalid_section" => row with { Section = (VetPhotoReviewSection)99 },
            "clear_missing_reading" => row with { Effective = null },
            "empty_description" => row with { Description = " " },
            "oversized_description" => row with { Description = new string('x', 3501) },
            "oversized_rendered_row" => row with { Description = new string('x', 3400) },
            "invalid_surrogate" => row with { Description = "synthetic\uD800" },
            "nul" => row with { Description = "synthetic\0" },
            "nonpositive_value" => row with { Effective = Reading() with { Value = 0 } },
            "unsupported_unit" => row with { Effective = Reading() with { Unit = "mg/dL" } },
            "missing_local_time" => row with { Effective = Reading() with { LocalTime = "" } },
            "missing_timezone" => row with { Effective = Reading() with { TimeZoneSnapshot = "" } },
            "missing_value_evidence" => row with { Effective = Reading() with { ValueEvidence = "" } },
            "missing_unit_evidence" => row with { Effective = Reading() with { UnitEvidence = "" } },
            "missing_time_evidence" => row with { Effective = Reading() with { TimeEvidence = "" } },
            "unknown_value_evidence" => row with { Effective = Reading() with { ValueEvidence = "synthetic_unknown" } },
            "unknown_unit_evidence" => row with { Effective = Reading() with { UnitEvidence = "synthetic_unknown" } },
            "unknown_time_evidence" => row with { Effective = Reading() with { TimeEvidence = "synthetic_unknown" } },
            "oversized_field" => row with { Effective = Reading() with { TimeZoneSnapshot = new string('x', 257) } },
            "unsafe_link" => row with { SamePlaceSourceLink = "https://example.invalid/source" },
            "link_credentials" => row with { SamePlaceSourceLink = "https://synthetic@t.me/c/100/1" },
            "link_query" => row with { SamePlaceSourceLink = "https://t.me/c/100/1?synthetic=1" },
            "null_row" => null!,
            _ => row
        };
        var rows = invalid == "duplicate_item" ? new[] { row, row } : new[] { row };
        Refused(VetPhotoReviewFormatter.Format("synthetic heading", [], rows, "synthetic footer"));
    }

    [Theory]
    [InlineData("heading")]
    [InlineData("footer")]
    [InlineData("assumption")]
    [InlineData("assumption_surrogate")]
    [InlineData("null_assumptions")]
    [InlineData("null_rows")]
    [InlineData("null_heading")]
    public void Invalid_outer_content_returns_fixed_failure_without_partial_pages(string invalid)
    {
        var heading = invalid == "heading" ? new string('h', 3501) : invalid == "null_heading" ? null! : "H";
        var footer = invalid == "footer" ? new string('f', 3501) : "F";
        string[] assumptions = invalid == "null_assumptions" ? null! :
            invalid == "assumption" ? [new string('a', 3500)] : invalid == "assumption_surrogate" ? ["\uDC00"] : [];
        VetPhotoReviewRow[] rows = invalid == "null_rows" ? null! : [Row(1)];
        Refused(VetPhotoReviewFormatter.Format(heading, assumptions, rows, footer));
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("empty")]
    [InlineData("surrogate")]
    [InlineData("null_block")]
    [InlineData("null_collection")]
    [InlineData("too_many")]
    public void Invalid_generic_block_refuses_entire_preview_without_truncation(string invalid)
    {
        string[] blocks = invalid switch {
            "oversized" => ["synthetic valid", new string('x', 3501)],
            "empty" => ["synthetic valid", ""],
            "surrogate" => ["synthetic valid", "\uD800"],
            "null_block" => ["synthetic valid", null!],
            "too_many" => Enumerable.Repeat("synthetic", 10001).ToArray(),
            _ => null!
        };
        Refused(VetPhotoReviewFormatter.FormatBlocks("H", blocks, "F"));
    }

    [Fact]
    public void ReviewFix_formatter_keeps_exact_complete_scalar_safe_evidence_after_its_row_before_next_item()
    {
        var value=new string('a',2999)+"\U0001F9EA"+new string('b',1095);
        var evidence=VetPhotoReviewEvidence.Data("synthetic immutable evidence",value);
        evidence.Count.ShouldBe(2);
        var rows=new[]{Row(1) with {EvidenceBlocks=evidence},Row(2,effective:Reading(At.AddSeconds(1)))};
        var review=VetPhotoReviewFormatter.Format("Synthetic heading",[],rows,"Synthetic footer");
        review.Success.ShouldBeTrue();review.Pages.ShouldAllBe(page=>page.Length<=3500);
        var text=string.Join("\n\n",review.Pages);
        text.IndexOf("#1:",StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("synthetic immutable evidence — часть 1/2",StringComparison.Ordinal));
        text.IndexOf("synthetic immutable evidence — часть 1/2",StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("synthetic immutable evidence — часть 2/2",StringComparison.Ordinal));
        text.IndexOf("synthetic immutable evidence — часть 2/2",StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("#2:",StringComparison.Ordinal));
        var actual=review.Pages.SelectMany(page=>page.Split("\n\n")).Where(block=>block.StartsWith("synthetic immutable evidence — часть ",StringComparison.Ordinal)).ToArray();
        actual.ShouldBe(evidence);string.Concat(actual.Select(block=>block[(block.IndexOf('\n')+1)..])).ShouldBe(value);
        foreach(var page in review.Pages)System.Text.Encoding.UTF8.GetString(new System.Text.UTF8Encoding(false,true).GetBytes(page)).ShouldBe(page);
        text.ShouldContain("Synthetic heading");text.ShouldEndWith("Synthetic footer");
    }
    [Fact]
    public void ReviewFix_formatter_default_rows_keep_the_exact_existing_text_without_evidence_labels()
    {
        var row=new VetPhotoReviewRow(1,VetPhotoReviewSection.Excluded,null,"synthetic default");
        var review=VetPhotoReviewFormatter.Format("Synthetic heading",[],[row],"Synthetic footer");
        review.Success.ShouldBeTrue();review.Pages.ShouldBe(new[]{"Synthetic heading\n\nИсключены\n#1\nsynthetic default\n\nSynthetic footer"});
    }
    [Theory]
    [InlineData("nul")]
    [InlineData("high-surrogate")]
    [InlineData("low-surrogate")]
    [InlineData("oversized-block")]
    [InlineData("global-block-cap")]
    [InlineData("page-cap")]
    public void ReviewFix_formatter_refuses_entire_invalid_or_over_cap_evidence_without_partial_pages(string boundary)
    {
        var evidence=boundary switch
        {
            "nul"=>new[]{"synthetic\0hidden"},
            "high-surrogate"=>new[]{"synthetic\uD83E"},
            "low-surrogate"=>new[]{"synthetic\uDDEA"},
            "oversized-block"=>new[]{new string('x',3501)},
            "global-block-cap"=>Enumerable.Repeat("x",5001).ToArray(),
            "page-cap"=>Enumerable.Repeat(new string('x',3000),65).ToArray(),
            _=>throw new ArgumentOutOfRangeException(nameof(boundary))
        };
        var rows=boundary=="global-block-cap"
            ?new[]{Row(1) with {EvidenceBlocks=evidence},Row(2) with {EvidenceBlocks=evidence}}
            :new[]{Row(1) with {EvidenceBlocks=evidence}};
        var review=VetPhotoReviewFormatter.Format("Synthetic heading",[],rows,"Synthetic footer");
        review.Success.ShouldBeFalse();review.FailureReason.ShouldBe(VetPhotoReviewFormatter.RefusalReason);review.Pages.ShouldBeEmpty();
    }
}
