using WTelegram;

// One-off tool: logs a throwaway Telegram account in and prints its session as a base64 string.
// Run: dotnet run --project tests/Assistant.SmokeTests.Login   (see tests/Assistant.SmokeTests/README.md)
static string Ask(string prompt)
{
    Console.Write(prompt + ": ");
    return Console.ReadLine() ?? string.Empty;
}

var apiId = Ask("api_id");
var apiHash = Ask("api_hash");

Helpers.Log = (_, _) => { };
var sessionStore = new MemoryStream();
using var client = new Client(
    what => what switch
    {
        "api_id" => apiId,
        "api_hash" => apiHash,
        "phone_number" => Ask("Phone number (international format)"),
        "verification_code" => Ask("Verification code sent by Telegram"),
        "password" => Ask("2FA password"),
        "first_name" => "Smoke",
        "last_name" => "Test",
        _ => null
    },
    sessionStore);

var me = await client.LoginUserIfNeeded();
Console.WriteLine($"Logged in as user id {me.id}.");
Console.WriteLine("Session (a secret: store it in smoke.env / a GitHub secret, never commit it):");
Console.WriteLine(Convert.ToBase64String(sessionStore.ToArray()));
