namespace Assistant.Application.Manager;

/// <summary>Correlates a /newbot command to the managed_bot update that follows it (Telegram's
/// managed_bot update carries the creator and the new bot, but not which role the owner typed).
/// Persisted, so a restart between the two does not lose the role. One pending role per creator,
/// overwritten by a repeat /newbot; a pending role older than <see cref="MaxAge"/> is ignored.</summary>
public interface IPendingBotCreations
{
    /// <summary>Same limit as a bot's stored role.</summary>
    const int MaxRoleLength = 64;

    static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    Task SetPendingRoleAsync(long creatorTelegramUserId, string role, CancellationToken cancellationToken);

    /// <summary>Removes and returns the pending role for this creator, or null if there is none or
    /// it is older than <see cref="MaxAge"/>.</summary>
    Task<string?> TakeRoleAsync(long creatorTelegramUserId, CancellationToken cancellationToken);
}
