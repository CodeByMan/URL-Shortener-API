using System.ComponentModel.DataAnnotations;

namespace URLShortening.DTOs;

public class ResendConfirmationDto
{
    [Required, EmailAddress]
    public string Email { get; set; }
}
