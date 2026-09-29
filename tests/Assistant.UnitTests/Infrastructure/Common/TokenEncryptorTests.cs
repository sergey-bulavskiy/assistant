using Assistant.Infrastructure.Common;

namespace Assistant.UnitTests.Infrastructure.Common;

public class TokenEncryptorTests
{
    // Synthetic 32-byte key, base64-encoded — never a real secret.
    private const string TestKeyBase64 = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=";

    [Fact]
    public void Round_trips_a_token()
    {
        var encryptor = new TokenEncryptor(TestKeyBase64);

        var cipher = encryptor.Encrypt("123456:test-token-value");
        var plain = encryptor.Decrypt(cipher);

        plain.ShouldBe("123456:test-token-value");
    }

    [Fact]
    public void Two_encryptions_of_the_same_token_produce_different_ciphertext()
    {
        var encryptor = new TokenEncryptor(TestKeyBase64);

        var first = encryptor.Encrypt("123456:test-token-value");
        var second = encryptor.Encrypt("123456:test-token-value");

        first.ShouldNotBe(second);
    }

    [Fact]
    public void Ciphertext_never_contains_the_plaintext_token_bytes()
    {
        var encryptor = new TokenEncryptor(TestKeyBase64);
        var plainBytes = System.Text.Encoding.UTF8.GetBytes("123456:test-token-value");

        var cipher = encryptor.Encrypt("123456:test-token-value");

        // A naive/no-op "encryptor" would just copy the plaintext bytes through — assert the
        // plaintext byte sequence is not a contiguous substring of the ciphertext.
        IndexOf(cipher, plainBytes).ShouldBeLessThan(0);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public void Rejects_a_key_that_is_not_32_bytes()
    {
        var shortKeyBase64 = Convert.ToBase64String(new byte[16]);

        Should.Throw<ArgumentException>(() => new TokenEncryptor(shortKeyBase64));
    }
}
