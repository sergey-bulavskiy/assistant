namespace Assistant.Application.Messages;

/// <summary>Role names. Roles are stored exactly as typed to /newbot, so comparisons trim and ignore
/// case.</summary>
public static class BotRoles
{
    public const string General = "general";

    public const string Health = "health";

    // Spec 2.1: role `general`, trimmed and case-insensitive.
    public static bool IsGeneral(string role) =>
        string.Equals(role.Trim(), General, StringComparison.OrdinalIgnoreCase);

    // The health tracking assistant: role `health`, same matching rule.
    public static bool IsHealth(string role) =>
        string.Equals(role.Trim(), Health, StringComparison.OrdinalIgnoreCase);
}
