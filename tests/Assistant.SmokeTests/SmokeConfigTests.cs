namespace Assistant.SmokeTests;

public class SmokeConfigTests
{
    private static Dictionary<string, string> CompleteSettings() => new()
    {
        ["SMOKE_TG_API_ID"] = "123",
        ["SMOKE_TG_API_HASH"] = "test-hash",
        ["SMOKE_OWNER_SESSION"] = "test-session-owner",
        ["SMOKE_MANAGER_BOT_TOKEN"] = "test-token",
        ["SMOKE_MANAGER_BOT_USERNAME"] = "test_manager_bot",
        ["SMOKE_ROLE_BOT_TOKEN"] = "111:test-token",
        ["SMOKE_ROLE_BOT_USERNAME"] = "test_role_bot",
        ["SMOKE_GROUP_TITLE"] = "test group",
        ["SMOKE_FORUM_TITLE"] = "test forum"
    };

    [Fact]
    public void Missing_required_setting_is_named_in_the_error()
    {
        var settings = CompleteSettings();
        settings.Remove("SMOKE_ROLE_BOT_TOKEN");

        var error = Should.Throw<InvalidOperationException>(() => SmokeConfig.Load(name => settings.GetValueOrDefault(name)));

        error.Message.ShouldContain("SMOKE_ROLE_BOT_TOKEN");
    }

    [Fact]
    public void Empty_required_setting_counts_as_missing()
    {
        var settings = CompleteSettings();
        settings["SMOKE_ROLE_BOT_TOKEN"] = "";

        var error = Should.Throw<InvalidOperationException>(() => SmokeConfig.Load(name => settings.GetValueOrDefault(name)));

        error.Message.ShouldContain("SMOKE_ROLE_BOT_TOKEN");
    }

    [Fact]
    public void Non_numeric_api_id_is_reported_by_name()
    {
        var settings = CompleteSettings();
        settings["SMOKE_TG_API_ID"] = "abc";

        var error = Should.Throw<InvalidOperationException>(() => SmokeConfig.Load(name => settings.GetValueOrDefault(name)));

        error.Message.ShouldContain("SMOKE_TG_API_ID");
        error.Message.ShouldNotContain("abc");
    }

    [Fact]
    public void Empty_image_setting_is_treated_as_unset()
    {
        var settings = CompleteSettings();
        settings["SMOKE_IMAGE"] = "";
        SmokeConfig.Load(name => settings.GetValueOrDefault(name)).Image.ShouldBeNull();

        settings["SMOKE_IMAGE"] = "example.invalid/assistant:sha-abc1234";
        SmokeConfig.Load(name => settings.GetValueOrDefault(name)).Image.ShouldBe("example.invalid/assistant:sha-abc1234");
    }
}
