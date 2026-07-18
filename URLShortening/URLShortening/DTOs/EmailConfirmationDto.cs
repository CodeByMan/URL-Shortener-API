using System.ComponentModel.DataAnnotations;

namespace URLShortening.DTOs;

public class EmailConfirmationDto
{
    [Required, StringLength(6, MinimumLength = 6)]
    public string ConfirmationCode { get; set; }

    [Required, EmailAddress]
    public string Email { set; get; }
}
