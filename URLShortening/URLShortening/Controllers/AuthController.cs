using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using URLShortening.Data;
using URLShortening.DTOs;
using URLShortening.Helpers;
using URLShortening.Models;
using URLShortening.Services;

namespace URLShortening.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
public class AuthController(
    IUserHelper userHelper,
    IMailService mailService,
    IConfiguration configuration,
    ICodeGeneratorHelper codeGeneratorHelper) :
    ControllerBase
{
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Index() => Ok(new { Message = "Everything is ok!" });

    [HttpPost("[action]")]
    [EnableRateLimiting(RateLimitPolicyNames.Authentication)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Register([FromBody] AuthDto authDto)
    {
        var user = await userHelper.FindUserByEmailAsync(authDto.Email);
        if (user is not null)
        {
            if (user.EmailConfirmed)
            {
                return Conflict(new
                {
                    Message = "An account with this email already exists."
                });
            }

            var resendResult = await SendConfirmationCodeAsync(user);
            return resendResult.IsSuccess
                ? Accepted(new
                {
                    Message = "The account is awaiting confirmation. A new confirmation code was sent."
                })
                : ConfirmationDeliveryUnavailable();
        }

        var newUser = new User
        {
            Email = authDto.Email,
            UserName = authDto.Email
        };

        var result = await userHelper.CreateUserAsync(newUser, authDto.Password);
        if (!result)
        {
            return BadRequest(new
            {
                Message = "The account could not be created."
            });
        }

        var mailResult = await SendConfirmationCodeAsync(newUser);
        return mailResult.IsSuccess
            ? Ok(new
            {
                Message = "Registration succeeded. Check your email for the confirmation code."
            })
            : ConfirmationDeliveryUnavailable();
    }

    [HttpPost("resend-confirmation")]
    [EnableRateLimiting(RateLimitPolicyNames.Authentication)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ResendConfirmation(
        [FromBody] ResendConfirmationDto confirmationDto)
    {
        var user = await userHelper.FindUserByEmailAsync(confirmationDto.Email);
        if (user is null || user.EmailConfirmed)
        {
            return Accepted(new
            {
                Message = "If an unconfirmed account exists, a new confirmation code will be sent."
            });
        }

        var mailResult = await SendConfirmationCodeAsync(user);
        return mailResult.IsSuccess
            ? Accepted(new
            {
                Message = "A new confirmation code was sent."
            })
            : ConfirmationDeliveryUnavailable();
    }

    [HttpPost("[action]")]
    [EnableRateLimiting(RateLimitPolicyNames.Authentication)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ConfirmEmail(
        [FromBody] EmailConfirmationDto confirmationDto)
    {
        var user = await userHelper.FindUserByEmailAsync(confirmationDto.Email);
        if (user == null)
        {
            return NotFound();
        }

        if (user.EmailConfirmed)
        {
            return Ok(new { Message = "The email address is already confirmed." });
        }

        if (string.IsNullOrWhiteSpace(user.EmailConfirmationCodeHash) ||
            user.EmailConfirmationCodeExpiresAt is null ||
            user.EmailConfirmationCodeExpiresAt <= DateTime.UtcNow)
        {
            return BadRequest(new
            {
                Message = "The confirmation code is missing or expired. Request a new code."
            });
        }

        var codeKey = configuration["CodeKey"] ??
                      throw new InvalidOperationException(
                          "CodeKey is not configured.");
        var isValid = codeGeneratorHelper.VerifyConfirmationCode(
            confirmationDto.ConfirmationCode,
            user.EmailConfirmationCodeHash,
            user.Id,
            codeKey);

        if (!isValid)
        {
            return BadRequest(new
            {
                Message = "The confirmation code is invalid."
            });
        }

        var token = await userHelper.GenerateEmailConfirmationTokenAsync(user);
        var confirmationResult = await userHelper.ConfirmEmailAsync(user, token);
        if (!confirmationResult.Succeeded)
        {
            return BadRequest(new
            {
                Message = "The email address could not be confirmed."
            });
        }

        user.EmailConfirmationCodeHash = null;
        user.EmailConfirmationCodeExpiresAt = null;
        await userHelper.UpdateUserAsync(user);

        return Ok(new { Message = "The email address was confirmed." });
    }

    [HttpPost("[action]")]
    [EnableRateLimiting(RateLimitPolicyNames.Authentication)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Login([FromBody] AuthDto authDto)
    {
        var user = await userHelper.FindUserByEmailAsync(authDto.Email);

        if (user is null || user is { EmailConfirmed: false })
        {
            return NotFound();
        }

        var confirmPassword = await userHelper.CheckPasswordAsync(user,
            authDto.Password);
        if (!confirmPassword)
        {
            return BadRequest();
        }

        var key = configuration["JWT:Key"] ??
                  throw new ArgumentNullException("JWT:Key",
                      "JWT:Key cannot be null.");
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(securityKey,
            SecurityAlgorithms.HmacSha256);

        var now = DateTime.UtcNow;
        var lifetimeMinutes = Math.Clamp(
            configuration.GetValue<int?>("JWT:AccessTokenMinutes") ?? 60,
            5,
            1440);
        var expiresAt = now.AddMinutes(lifetimeMinutes);

        var claims = new[]
        {
            new Claim(ClaimTypes.Email, user.Email!),
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(JwtRegisteredClaimNames.Jti,
                Guid.NewGuid().ToString("N")),
            new Claim("security_stamp", user.SecurityStamp ?? string.Empty)
        };

        var token = new JwtSecurityToken(
            issuer: configuration["JWT:Issuer"],
            audience: configuration["JWT:Audience"],
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: credentials);

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        return Ok(new
        {
            AccessToken = jwt,
            Expiration = (expiresAt - now).TotalSeconds,
            TokenType = "bearer",
            UserId = user.Id,
            user.UserName
        });
    }

    [HttpPost("logout")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [EnableRateLimiting(RateLimitPolicyNames.Authentication)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Logout()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var user = await userHelper.FindUserByIdAsync(userId);
        if (user is null)
        {
            return Unauthorized();
        }

        var result = await userHelper.UpdateSecurityStampAsync(user);
        return result.Succeeded
            ? NoContent()
            : BadRequest(new { Message = "Logout could not be completed." });
    }

    private async Task<ResponseResultModel> SendConfirmationCodeAsync(User user)
    {
        var codeKey = configuration["CodeKey"] ??
                      throw new InvalidOperationException(
                          "CodeKey is not configured.");
        var lifetimeMinutes = Math.Clamp(
            configuration.GetValue<int?>(
                "EmailConfirmation:CodeLifetimeMinutes") ?? 15,
            5,
            60);

        var code = codeGeneratorHelper.GenerateConfirmationCode();
        user.EmailConfirmationCodeHash =
            codeGeneratorHelper.HashConfirmationCode(code, user.Id, codeKey);
        user.EmailConfirmationCodeExpiresAt =
            DateTime.UtcNow.AddMinutes(lifetimeMinutes);

        var updateResult = await userHelper.UpdateUserAsync(user);
        if (!updateResult.Succeeded)
        {
            return new ResponseResultModel
            {
                IsSuccess = false,
                ErrorMessage = "The confirmation code could not be stored."
            };
        }

        var mailResult = await mailService.SendEmailAsync(user.Email!,
            "Confirm your email",
            $"<p>Your confirmation code is: <strong>{code}</strong></p>" +
            $"<p>This code expires in {lifetimeMinutes} minutes.</p>");

        if (!mailResult.IsSuccess)
        {
            user.EmailConfirmationCodeHash = null;
            user.EmailConfirmationCodeExpiresAt = null;
            await userHelper.UpdateUserAsync(user);
        }

        return mailResult;
    }

    private ObjectResult ConfirmationDeliveryUnavailable()
    {
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            Message = "The account exists, but the confirmation email could not be delivered. Try the resend-confirmation endpoint after SMTP is available."
        });
    }
}
