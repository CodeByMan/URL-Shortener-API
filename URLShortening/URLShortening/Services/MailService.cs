using MailKit.Net.Smtp;
using MimeKit;
using URLShortening.Models;

namespace URLShortening.Services;

public class MailService(
    IConfiguration configuration,
    ILogger<MailService> logger) : IMailService
{
    public async Task<ResponseResultModel> SendEmailAsync(string email,
        string subject, string body)
    {
        var displayName = configuration["EmailSettings:DisplayName"];
        var emailFrom = configuration["EmailSettings:Email"];
        var password = configuration["EmailSettings:Password"];
        var smtp = configuration["EmailSettings:Smtp"];
        var port = int.Parse(
            configuration["EmailSettings:Port"] ??
            throw new InvalidOperationException(
                "EmailSettings:Port is not configured."));

        var message = new MimeMessage();

        message.From.Add(new MailboxAddress(displayName, emailFrom));
        message.To.Add(new MailboxAddress(email, email));
        message.Subject = subject;

        var bodyBuilder = new BodyBuilder()
        {
            HtmlBody = body
        };

        message.Body = bodyBuilder.ToMessageBody();

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(smtp, port, false);
            await client.AuthenticateAsync(emailFrom, password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            return new ResponseResultModel() { IsSuccess = true };
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Email delivery failed for a registration confirmation message.");
            return new ResponseResultModel
            {
                IsSuccess = false,
                ErrorMessage = "Email delivery failed."
            };
        }
    }
}
