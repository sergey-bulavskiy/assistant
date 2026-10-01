using Assistant.Application.Messages;
using Assistant.Domain.Messages;

namespace Assistant.Application.Llm;

/// <summary>Turns stored context rows plus the current message into the LlmMessage list a request
/// carries (spec 2.4). IMessageStore.GetRecentContextAsync already caps the row *count*
/// (LLM_MAX_CONTEXT_MESSAGES) server-side and excludes commands and the current message itself; this
/// is the char-budget (LLM_MAX_INPUT_CHARS) pass, run in memory over that already-small list, and the
/// place the current message is appended exactly once. Group authorship reaches the CLI as the
/// <c>&lt;msg author="..."&gt;</c> attribute (spec §8.4), not a text prefix -- this class sets
/// <see cref="LlmMessage.Author"/>, it never rewrites Text. A null Author on a User-role message
/// falls back to "user" inside ClaudeCliChatClient (Decision #9), so this class doesn't need to
/// invent that fallback itself.</summary>
public static class ContextBuilder
{
    public static IReadOnlyList<LlmMessage> Build(
        IReadOnlyList<ContextMessage> history,
        string currentMessageText,
        string? currentUsername,
        bool isGroup,
        int maxInputChars)
    {
        var messages = new List<LlmMessage>(history.Count + 1);
        foreach (var entry in history)
        {
            var role = entry.Direction == MessageDirection.Out ? LlmMessageRole.Assistant : LlmMessageRole.User;
            var author = isGroup && role == LlmMessageRole.User ? entry.Username : null;
            messages.Add(new LlmMessage(role, entry.Text, author));
        }

        var currentText = currentMessageText;
        if (currentText.Length > maxInputChars)
        {
            // The current message alone exceeds the budget: truncated, never dropped (spec 2.4).
            currentText = currentText[..maxInputChars];
        }

        messages.Add(new LlmMessage(LlmMessageRole.User, currentText, isGroup ? currentUsername : null));

        var totalChars = messages.Sum(m => m.Text.Length);
        var firstKeptIndex = 0;
        while (totalChars > maxInputChars && firstKeptIndex < messages.Count - 1)
        {
            totalChars -= messages[firstKeptIndex].Text.Length;
            firstKeptIndex++;
        }

        // Review nit: Anthropic (and the other API providers) require the first message in a
        // request to be a User one -- the char-budget trim above can otherwise leave an Assistant
        // turn first (the oldest surviving messages happened to start with the bot's own reply).
        // The current message appended above is always User, so this loop always terminates before
        // running past the end of the list.
        while (firstKeptIndex < messages.Count - 1 && messages[firstKeptIndex].Role == LlmMessageRole.Assistant)
        {
            firstKeptIndex++;
        }

        return messages.Skip(firstKeptIndex).ToArray();
    }
}
