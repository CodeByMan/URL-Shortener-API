namespace URLShortening.Helpers;

public interface ICodeGeneratorHelper
{
    string GenerateConfirmationCode();
    string HashConfirmationCode(string code, string userId,
        string secretKey);
    bool VerifyConfirmationCode(string code, string expectedHash,
        string userId, string secretKey);
}
