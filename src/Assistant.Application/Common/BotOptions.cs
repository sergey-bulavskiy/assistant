using Microsoft.Extensions.Options;

namespace Assistant.Application.Common;

public class BotOptions
{
    public string ManagerToken { get; set; } = string.Empty;
    public string TokenEncryptionKey { get; set; } = string.Empty;
}

public class BotOptionsValidator : IValidateOptions<BotOptions>
{
    public ValidateOptionsResult Validate(string? name, BotOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ManagerToken))
        {
            failures.Add("TELEGRAM_MANAGER_BOT_TOKEN is required.");
        }

        if (string.IsNullOrWhiteSpace(options.TokenEncryptionKey))
        {
            failures.Add("TOKEN_ENCRYPTION_KEY is required.");
        }
        else
        {
            try
            {
                var keyBytes = Convert.FromBase64String(options.TokenEncryptionKey);
                if (keyBytes.Length != 32)
                {
                    failures.Add("TOKEN_ENCRYPTION_KEY must be base64 for exactly 32 raw bytes (AES-256).");
                }
            }
            catch (FormatException)
            {
                failures.Add("TOKEN_ENCRYPTION_KEY must be valid base64.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
