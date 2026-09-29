using System.Collections.Concurrent;
using Assistant.Application.Manager;

namespace Assistant.Infrastructure.Manager;

public class PendingBotCreations : IPendingBotCreations
{
    private readonly ConcurrentDictionary<long, string> _pending = new();

    public void SetPendingRole(long creatorTelegramUserId, string role) => _pending[creatorTelegramUserId] = role;

    public string? TakeRole(long creatorTelegramUserId) =>
        _pending.TryRemove(creatorTelegramUserId, out var role) ? role : null;
}
