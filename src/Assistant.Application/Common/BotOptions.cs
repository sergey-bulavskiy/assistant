using System.Globalization;
using Microsoft.Extensions.Options;

namespace Assistant.Application.Common;

public class BotOptions
{
    public string Token { get; set; } = string.Empty;
    public string AllowedUserIdsRaw { get; set; } = string.Empty;

    private IReadOnlyList<long>? _allowedUserIds;

    public IReadOnlyList<long> AllowedUserIds
    {
        get
        {
            if (_allowedUserIds is not null)
            {
                return _allowedUserIds;
            }

            _allowedUserIds = AllowedUserIdsRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => long.Parse(s, CultureInfo.InvariantCulture))
                .ToArray();
            return _allowedUserIds;
        }
    }

    public long? OwnerUserId => AllowedUserIds.Count > 0 ? AllowedUserIds[0] : null;
}

public class BotOptionsValidator : IValidateOptions<BotOptions>
{
    public ValidateOptionsResult Validate(string? name, BotOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            failures.Add("TELEGRAM_BOT_TOKEN is required.");
        }

        if (string.IsNullOrWhiteSpace(options.AllowedUserIdsRaw))
        {
            failures.Add("ALLOWED_USER_IDS is required and must contain at least one Telegram user id.");
        }
        else
        {
            try
            {
                if (options.AllowedUserIds.Count == 0)
                {
                    failures.Add("ALLOWED_USER_IDS must contain at least one Telegram user id.");
                }
            }
            catch (FormatException)
            {
                failures.Add("ALLOWED_USER_IDS must be a comma-separated list of numeric Telegram user ids.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
