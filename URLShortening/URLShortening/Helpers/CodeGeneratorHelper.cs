using System.Security.Cryptography;
using System.Text;

namespace URLShortening.Helpers;

public class CodeGeneratorHelper : ICodeGeneratorHelper
{
    public string GenerateConfirmationCode()
    {
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    }

    public string HashConfirmationCode(string code, string userId,
        string secretKey)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(secretKey))
        {
            throw new ArgumentException(
                "Code, userId and secretKey cannot be null or empty.");
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        var payload = Encoding.UTF8.GetBytes($"{userId}:{code}");
        return Convert.ToHexString(hmac.ComputeHash(payload));
    }

    public bool VerifyConfirmationCode(string code, string expectedHash,
        string userId, string secretKey)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        var actualHash = HashConfirmationCode(code, userId, secretKey);
        var actualBytes = Convert.FromHexString(actualHash);
        var expectedBytes = Convert.FromHexString(expectedHash);

        return actualBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(actualBytes,
                   expectedBytes);
    }
}
