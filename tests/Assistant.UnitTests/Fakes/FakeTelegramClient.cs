using Assistant.Application.Telegram;

namespace Assistant.UnitTests.Fakes;

public class FakeTelegramClient : ITelegramClient
{
    public List<(long ChatId, int? TopicId, string Text)> Sent { get; } = new();

    public bool ThrowOnSend { get; set; }

    public Task<BotIdentity> GetMeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new BotIdentity(999, "test_bot"));

    public Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IncomingUpdate>>(Array.Empty<IncomingUpdate>());

    public Task SendTextAsync(long chatId, int? topicId, string text, CancellationToken cancellationToken)
    {
        if (ThrowOnSend)
        {
            throw new InvalidOperationException("simulated send failure");
        }

        Sent.Add((chatId, topicId, text));
        return Task.CompletedTask;
    }
}
