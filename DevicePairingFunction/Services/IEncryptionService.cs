namespace DevicePairingFunction.Services;

public interface IEncryptionService
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
    string HashDeviceSecret(string deviceSecret);
    bool VerifyDeviceSecret(string deviceSecret, string hash);
}
