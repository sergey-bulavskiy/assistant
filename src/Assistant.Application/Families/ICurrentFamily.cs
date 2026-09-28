namespace Assistant.Application.Families;

/// <summary>Scoped value set once, at the start of handling one Telegram update, from the family of
/// the bot or place that received it. Never set from anything a user typed. See
/// AssistantDbContext's query filters for how this scopes reads; the manager bot's own scope always
/// has FamilyId == null (it looks up the caller's family membership explicitly instead).</summary>
public interface ICurrentFamily
{
    long? FamilyId { get; }

    void Set(long? familyId);
}
