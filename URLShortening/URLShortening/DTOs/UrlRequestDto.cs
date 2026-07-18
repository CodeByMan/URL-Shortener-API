using System.ComponentModel.DataAnnotations;

namespace URLShortening.DTOs;

public class UrlRequestDto
{
    [Required, Url, StringLength(450)] public string Url { get; set; }
    public DateTime? ExpiresAt { get; set; } = null;
}
