using System.Globalization;
using TL;
using WTelegram;

namespace Assistant.SmokeTests;

/// <summary>
/// One logged-in MTProto account acting as a human: sends messages, reads everything it receives,
/// taps inline buttons. The session is a base64 string (a secret); expired sessions fail fast.
/// </summary>
public sealed class TelegramUser : IAsyncDisposable
{
    // Short pause after every send, to stay well under Telegram's flood limits.
    private static readonly TimeSpan SendPause = TimeSpan.FromMilliseconds(700);

    private readonly Client _client;
    private readonly List<Message> _received = [];
    private readonly Dictionary<string, InputPeer> _peers = new();

    private TelegramUser(Client client, long id)
    {
        _client = client;
        Id = id;
    }

    public long Id { get; }

    public static async Task<TelegramUser> LoginAsync(int apiId, string apiHash, string sessionBase64)
    {
        Helpers.Log = (_, _) => { };
        var sessionStore = new MemoryStream();
        sessionStore.Write(Convert.FromBase64String(sessionBase64));
        sessionStore.Position = 0;

        var client = new Client(
            what => what switch
            {
                "api_id" => apiId.ToString(CultureInfo.InvariantCulture),
                "api_hash" => apiHash,
                "phone_number" or "verification_code" or "password" or "email" or "email_verification_code" =>
                    throw new InvalidOperationException(
                        $"Telegram asked for '{what}': the stored session is expired or invalid. Regenerate it (tests/Assistant.SmokeTests/README.md)."),
                _ => null
            },
            sessionStore);

        var me = await client.LoginUserIfNeeded();
        var user = new TelegramUser(client, me.id);
        client.OnUpdates += user.OnUpdatesAsync;
        return user;
    }

    /// <summary>Marks "now": pass the value to the Wait/Assert methods so they only look at newer messages.</summary>
    public int Mark()
    {
        lock (_received)
        {
            return _received.Count;
        }
    }

    public async Task<InputPeer> ResolveUsernameAsync(string username)
    {
        if (_peers.TryGetValue(username, out var known))
        {
            return known;
        }

        var resolved = await _client.Contacts_ResolveUsername(username.TrimStart('@'));
        InputPeer peer = resolved.User;
        _peers[username] = peer;
        return peer;
    }

    public async Task<InputPeer> FindChatAsync(string title)
    {
        var dialogs = await _client.Messages_GetAllDialogs();
        var chat = dialogs.chats.Values.FirstOrDefault(c => c.IsActive && c.Title == title)
            ?? throw new InvalidOperationException($"The account is not a member of a chat titled '{title}'.");
        return chat;
    }

    public async Task SendAsync(InputPeer peer, string text, int topicId = 0)
    {
        await _client.SendMessageAsync(peer, text, reply_to_msg_id: topicId);
        await Task.Delay(SendPause);
    }

    /// <summary>Creates a forum topic with a unique title and returns its id (the id of its service message).</summary>
    public async Task<int> CreateForumTopicAsync(InputPeer forum, string title)
    {
        var updates = await _client.Messages_CreateForumTopic(forum, title, Helpers.RandomLong());
        return updates.UpdateList.OfType<UpdateNewChannelMessage>().Select(u => u.message.ID).First();
    }

    public async Task<Message> WaitForAsync(Func<Message, bool> predicate, int since, string description, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            lock (_received)
            {
                var match = _received.Skip(since).FirstOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"No message matching '{description}' arrived within {seconds}s.");
    }

    public async Task AssertNoneAsync(Func<Message, bool> predicate, int since, string description, int seconds = 8)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        lock (_received)
        {
            var match = _received.Skip(since).FirstOrDefault(predicate);
            if (match is not null)
            {
                throw new InvalidOperationException($"Unexpected message matching '{description}'");
            }
        }
    }

    /// <summary>Taps the inline callback button with the given label on a message received in <paramref name="peer"/>.</summary>
    public async Task TapAsync(InputPeer peer, Message message, string buttonLabel)
    {
        var callback = (message.reply_markup as ReplyInlineMarkup)?.rows
            .SelectMany(row => row.buttons)
            .Where(b => b.text == buttonLabel)
            .Select(b => b.type)
            .OfType<InlineButtonTypeCallback>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"No button '{buttonLabel}'");
        try
        {
            await _client.Messages_GetBotCallbackAnswer(peer, message.id, callback.data);
        }
        catch (RpcException e) when (e.Message == "BOT_RESPONSE_TIMEOUT")
        {
            // The bot answered slowly; the scenario verifies every tap through the database.
        }

        await Task.Delay(SendPause);
    }

    public async ValueTask DisposeAsync()
    {
        _client.OnUpdates -= OnUpdatesAsync;
        _client.Dispose();
        await Task.CompletedTask;
    }

    private Task OnUpdatesAsync(UpdatesBase updates)
    {
        updates.CollectUsersChats(new Dictionary<long, User>(), new Dictionary<long, ChatBase>());
        lock (_received)
        {
            foreach (var update in updates.UpdateList)
            {
                if (update is UpdateNewMessage { message: Message m })
                {
                    _received.Add(m);
                }
                else if (update is UpdateNewChannelMessage { message: Message cm })
                {
                    _received.Add(cm);
                }
            }
        }

        return Task.CompletedTask;
    }
}
