namespace DolaCoordinator.Services.Security;

public interface ISecurityService
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
}
