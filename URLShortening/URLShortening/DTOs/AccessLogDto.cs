namespace URLShortening.DTOs;

public class AccessLogDto
{
    public DateTime AccessedAt { get; set; }
    public string? IPAddress { get; set; }
    public string? Referrer { get; set; }
    public string UserAgent { get; set; } = "Unknown";
}
