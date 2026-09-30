using Xunit.Sdk;

namespace Assistant.SmokeTests;

public class SmokeScenarioTests
{
    [SmokeFact]
    public async Task Family_lifecycle_against_real_telegram()
    {
        var config = SmokeConfig.Load();
        await using var stack = await SmokeStack.StartAsync(config);
        await using var owner = await TelegramUser.LoginAsync(config.ApiId, config.ApiHash, config.OwnerSession);
        var scenario = new Scenario(config, stack, owner);

        await StepAsync("2 claim", scenario.ClaimAsync);
        await StepAsync("3 role bot (seeded)", scenario.SeedRoleBotAsync);
        await StepAsync("4 private chat and group", scenario.PrivateChatAndGroupAsync);
        await StepAsync("5 forum topic", scenario.ForumTopicAsync);
        await StepAsync("7 settings", scenario.SettingsAsync);
        await StepAsync("9 restart survives", scenario.RestartSurvivesAsync);
    }

    /// <summary>The first failing step stops the run; the exception names it.</summary>
    private static async Task StepAsync(string name, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            throw new XunitException($"Smoke step '{name}' failed: {ex.Message}", ex);
        }
    }
}
