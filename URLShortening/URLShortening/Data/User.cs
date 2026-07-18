using Microsoft.AspNetCore.Identity;

namespace URLShortening.Data;

public class User : IdentityUser
{
    public string? EmailConfirmationCodeHash { get; set; }
    public DateTime? EmailConfirmationCodeExpiresAt { get; set; }
    public List<Url> Urls { get; set; } = [];
}
