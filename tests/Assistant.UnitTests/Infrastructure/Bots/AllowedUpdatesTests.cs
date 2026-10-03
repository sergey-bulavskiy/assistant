using Assistant.Application.Telegram;
using Assistant.Infrastructure.Bots;

namespace Assistant.UnitTests.Infrastructure.Bots;

public class AllowedUpdatesTests
{
    [Fact]
    public void Role_bots_get_button_taps_but_never_managed_bot_events()
    {
        BotPollingCoordinator.AllowedUpdates(isManager: false).ShouldBe(
            new[] { UpdateKind.Message, UpdateKind.EditedMessage, UpdateKind.CallbackQuery, UpdateKind.MyChatMember });
    }

    [Fact]
    public void The_manager_keeps_its_list()
    {
        BotPollingCoordinator.AllowedUpdates(isManager: true).ShouldBe(
            new[] { UpdateKind.Message, UpdateKind.EditedMessage, UpdateKind.CallbackQuery, UpdateKind.ManagedBot });
    }
}
