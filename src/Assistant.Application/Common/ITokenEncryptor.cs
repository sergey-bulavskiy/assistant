namespace Assistant.Application.Common;

public interface ITokenEncryptor
{
    byte[] Encrypt(string plainToken);

    string Decrypt(byte[] cipherToken);
}
