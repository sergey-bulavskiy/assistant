using Assistant.Application.Telegram;

namespace Assistant.UnitTests.Application.Telegram;

public class ReplySplitterTests
{
    [Fact]
    public void A_short_reply_is_returned_as_a_single_chunk()
    {
        ReplySplitter.Split("short reply").ShouldBe(new[] { "short reply" });
    }

    [Fact]
    public void A_long_reply_splits_at_a_paragraph_boundary()
    {
        var paragraph1 = new string('a', 4000);
        var paragraph2 = new string('b', 200);
        var text = paragraph1 + "\n\n" + paragraph2;

        var chunks = ReplySplitter.Split(text, maxLength: 4096);

        chunks.Count.ShouldBe(2);
        chunks[0].ShouldBe(paragraph1);
        chunks[1].ShouldBe(paragraph2);
    }

    [Fact]
    public void No_chunk_ever_exceeds_the_max_length()
    {
        var text = string.Join("\n", Enumerable.Range(0, 2000).Select(i => $"line {i} with some filler text"));

        var chunks = ReplySplitter.Split(text, maxLength: 4096);

        chunks.ShouldAllBe(c => c.Length <= 4096);
        string.Concat(chunks).Replace("\n", "").ShouldBe(text.Replace("\n", ""));
    }

    [Fact]
    public void A_single_word_far_longer_than_the_limit_still_gets_hard_cut()
    {
        var text = new string('x', 5000);

        var chunks = ReplySplitter.Split(text, maxLength: 4096);

        chunks.Count.ShouldBe(2);
        chunks[0].Length.ShouldBe(4096);
    }

    [Fact]
    public void Exactly_the_limit_is_one_chunk()
    {
        var text = new string('x', 4096);

        ReplySplitter.Split(text, maxLength: 4096).Count.ShouldBe(1);
    }

    [Fact]
    public void One_char_over_the_limit_splits_into_two_chunks()
    {
        var text = new string('x', 4097);

        var chunks = ReplySplitter.Split(text, maxLength: 4096);

        chunks.Count.ShouldBe(2);
        chunks[0].Length.ShouldBe(4096);
        chunks[1].ShouldBe("x");
    }

    [Fact]
    public void A_split_point_never_lands_inside_a_surrogate_pair()
    {
        // Spec §8.12: split on UTF-16-safe boundaries. U+1F600 (an emoji outside the Basic
        // Multilingual Plane) is stored as TWO .NET chars (a surrogate pair) -- a naive hard cut at
        // exactly maxLength can land between them and corrupt both halves.
        var emoji = "\U0001F600";
        var text = new string('a', 10) + emoji + new string('b', 10);

        var chunks = ReplySplitter.Split(text, maxLength: 11); // 11 lands exactly between the pair's two chars

        chunks[0].ShouldBe(new string('a', 10)); // split moved back by one rather than slicing the pair
        string.Concat(chunks).ShouldBe(text);
    }

    [Fact]
    public void A_space_boundary_is_preferred_over_a_hard_cut_when_no_paragraph_or_line_break_exists()
    {
        var text = new string('a', 4090) + " " + new string('b', 20);

        var chunks = ReplySplitter.Split(text, maxLength: 4096);

        chunks[0].ShouldBe(new string('a', 4090));
        chunks[1].ShouldBe(new string('b', 20));
    }

    [Fact]
    public void Leading_whitespace_before_a_single_long_paragraph_never_produces_an_empty_chunk()
    {
        // The only "\n\n" in the text is the leading one, so the paragraph-break split point would
        // otherwise fall right after it, producing an empty/whitespace-only first chunk.
        var text = " \n\n" + new string('a', 5000);

        var chunks = ReplySplitter.Split(text, maxLength: 4096);

        chunks.ShouldAllBe(c => c.Length > 0 && !string.IsNullOrWhiteSpace(c));
        string.Concat(chunks).ShouldBe(new string('a', 5000));
    }

    [Fact]
    public void A_whitespace_only_reply_produces_no_chunks_at_all()
    {
        ReplySplitter.Split(" \n\t ").ShouldBeEmpty();
    }

    [Fact]
    public void A_text_with_no_spaces_longer_than_the_limit_is_hard_cut_into_full_chunks()
    {
        var text = new string('x', 9000);

        var chunks = ReplySplitter.Split(text, maxLength: 4096);

        chunks.Count.ShouldBe(3);
        chunks[0].Length.ShouldBe(4096);
        chunks[1].Length.ShouldBe(4096);
        chunks[2].Length.ShouldBe(9000 - 4096 - 4096);
        string.Concat(chunks).ShouldBe(text);
    }
}
