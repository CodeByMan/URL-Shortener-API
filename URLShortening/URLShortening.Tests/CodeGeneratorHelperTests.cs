using URLShortening.Helpers;

namespace URLShortening.Tests;

public class CodeGeneratorHelperTests
{
    [Fact]
    public void ConfirmationCode_IsSixDigitsAndCanBeVerified()
    {
        var helper = new CodeGeneratorHelper();
        var code = helper.GenerateConfirmationCode();
        var hash = helper.HashConfirmationCode(code, "user-1", "secret-key");

        Assert.Equal(6, code.Length);
        Assert.All(code, character => Assert.True(char.IsDigit(character)));
        Assert.True(helper.VerifyConfirmationCode(code, hash,
            "user-1", "secret-key"));
        Assert.False(helper.VerifyConfirmationCode("000000", hash,
            "user-1", "secret-key"));
    }
}
