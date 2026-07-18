using System.Security.Claims;
using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NanoidDotNet;
using URLShortening.Data;
using URLShortening.Data.Repository;
using URLShortening.DTOs;
using URLShortening.Helpers;
using URLShortening.Models;

namespace URLShortening.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class ShortenController(
    IUserHelper userHelper,
    IUrlRepository urlRepository,
    IAccessLogRepository accessLogRepository,
    IDeviceInfoHelper deviceInfoHelper,
    IGeoHelper geoLocationHelper,
    IMapper mapper) :
    ControllerBase
{
    private const int ShortCodeGenerationAttempts = 10;
    private const int LocationLookupConcurrency = 4;
    private readonly IGeoHelper _geoLocationHelper = geoLocationHelper;

    [HttpGet("{shortUrl}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicyNames.PublicRedirect)]
    public async Task<IActionResult> Get(string shortUrl)
    {
        var url = await urlRepository.FindRedirectTargetByShortUrl(shortUrl);
        if (url is null ||
            url.ExpiresAt is not null && url.ExpiresAt <= DateTime.Now)
        {
            return NotFound();
        }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var referer = Request.Headers["Referer"].FirstOrDefault() ??
                      Request.Headers["Origin"].FirstOrDefault();
        var userAgent = Request.Headers["User-Agent"].FirstOrDefault();

        var accessLog = new AccessLog()
        {
            AccessedAt = DateTime.Now,
            IPAddress = string.IsNullOrWhiteSpace(ip) ? null : ip,
            Ref = string.IsNullOrWhiteSpace(referer) ? null : referer,
            UserAgent = string.IsNullOrWhiteSpace(userAgent)
                ? "Unknown"
                : userAgent,
            UrlId = url.Id
        };

        await accessLogRepository.AddAsync(accessLog);

        return Redirect(url.LongUrl);
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicyNames.UrlCreation)]
    public async Task<IActionResult> Post(
        [FromBody] UrlRequestDto urlRequestDto)
    {
        var newUrl = new Url()
        {
            LongUrl = urlRequestDto.Url,
            ShortId = await GenerateUniqueShortUrlId(),
            ExpiresAt = urlRequestDto.ExpiresAt,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        await urlRepository.AddAsync(newUrl);

        var email = User.FindFirstValue(ClaimTypes.Email);
        if (!string.IsNullOrWhiteSpace(email))
        {
            var user = await userHelper.FindUserByEmailAsync(email);
            if (user != null)
            {
                user.Urls.Add(newUrl);
                await userHelper.UpdateUserAsync(user);
            }
        }

        var dto = mapper.Map<urlDto>(newUrl);
        return Created($"/api/v1/shorten/{newUrl.ShortId}", dto);
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }

        var urlsDto = mapper.Map<IEnumerable<urlDto>>(user.Urls);
        return Ok(urlsDto);
    }

    [HttpGet("{shortUrl}/details")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetails(string shortUrl)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }

        var url = await urlRepository.FindByShortUrl(shortUrl);
        if (url is null || !UserOwnsUrl(user, url))
        {
            return NotFound();
        }

        return Ok(mapper.Map<urlDto>(url));
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Put([FromBody] UrlRequestDto urlRequestDto)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }

        var matchingUrls = user.Urls
            .Where(u => u.LongUrl == urlRequestDto.Url)
            .Take(2)
            .ToList();
        if (matchingUrls.Count == 0)
        {
            return NotFound();
        }

        if (matchingUrls.Count > 1)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "The destination URL is not unique.",
                Detail = "Use PUT /api/v1/shorten/update with the current short code to select the link explicitly."
            });
        }

        var url = matchingUrls[0];
        url.ShortId = await GenerateUniqueShortUrlId();
        url.UpdatedAt = DateTime.Now;
        if (urlRequestDto.ExpiresAt is not null)
        {
            url.ExpiresAt = urlRequestDto.ExpiresAt;
        }

        await urlRepository.UpdateAsync(url);

        var dto = mapper.Map<urlDto>(url);
        return Ok(dto);
    }

    [HttpPut("expire")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Expire(updateUrlDto updateUrlDto)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }

        var url = await urlRepository.FindByShortUrl(updateUrlDto.ShortCode);
        if (url is null || !UserOwnsUrl(user, url))
        {
            return NotFound();
        }

        url.ExpiresAt = updateUrlDto.ExpiresAt;
        url.UpdatedAt = DateTime.Now;
        await urlRepository.UpdateAsync(url);

        var dto = mapper.Map<urlDto>(url);
        return Ok(dto);
    }

    [HttpPut("reactivate/{shortUrl}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reactivate(string shortUrl)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }

        var url = await urlRepository.FindByShortUrl(shortUrl);
        if (url is null || !UserOwnsUrl(user, url))
        {
            return NotFound();
        }

        url.ExpiresAt = null;
        url.UpdatedAt = DateTime.Now;
        await urlRepository.UpdateAsync(url);

        return Ok(mapper.Map<urlDto>(url));
    }

    [HttpPut("update")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(updateUrlDto updateUrlDto)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }

        var url = await urlRepository.FindByShortUrl(updateUrlDto.ShortCode);
        if (url is null || !UserOwnsUrl(user, url))
        {
            return NotFound();
        }

        url.ShortId = await GenerateUniqueShortUrlId();
        url.UpdatedAt = DateTime.Now;
        url.ExpiresAt = updateUrlDto.ExpiresAt;
        await urlRepository.UpdateAsync(url);

        var dto = mapper.Map<urlDto>(url);
        return Ok(dto);
    }

    [HttpDelete("{shortUrl}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string shortUrl)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }

        var url = await urlRepository.FindByShortUrl(shortUrl);
        if (url is null || !UserOwnsUrl(user, url))
        {
            return NotFound();
        }

        await urlRepository.DeleteAsync(url);
        return NoContent();
    }

    [HttpGet("{shortUrl}/stats")]
    [EnableRateLimiting(RateLimitPolicyNames.Analytics)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStats(string shortUrl)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }

        var url = await urlRepository.FindByShortUrl(shortUrl);
        if (url is null || !UserOwnsUrl(user, url))
        {
            return NotFound();
        }

        var stats = await BuildStats(url);
        return Ok(stats);
    }

    [HttpGet("{shortUrl}/access-logs")]
    [EnableRateLimiting(RateLimitPolicyNames.Analytics)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAccessLogs(string shortUrl)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }

        var url = await urlRepository.FindByShortUrl(shortUrl);
        if (url is null || !UserOwnsUrl(user, url))
        {
            return NotFound();
        }

        var logs = url.AccessLogs
            .OrderByDescending(log => log.AccessedAt)
            .Select(log => new AccessLogDto
            {
                AccessedAt = log.AccessedAt,
                IPAddress = log.IPAddress,
                Referrer = log.Ref,
                UserAgent = string.IsNullOrWhiteSpace(log.UserAgent)
                    ? "Unknown"
                    : log.UserAgent
            });

        return Ok(logs);
    }

    [HttpGet("topUrls")]
    [EnableRateLimiting(RateLimitPolicyNames.Analytics)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTopUrls()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }

        var urls = user.Urls.OrderByDescending(u => u.AccessLogs.Count)
            .Take(5);

        var urlsDto = mapper.Map<IEnumerable<urlDto>>(urls);
        return Ok(urlsDto);
    }

    protected virtual string GenerateShortUrlId()
    {
        return Nanoid.Generate(Nanoid.Alphabets.LettersAndDigits, 8);
    }

    private async Task<string> GenerateUniqueShortUrlId()
    {
        for (var attempt = 0;
             attempt < ShortCodeGenerationAttempts;
             attempt++)
        {
            var shortId = GenerateShortUrlId();
            if (!await urlRepository.ShortIdExistsAsync(shortId))
            {
                return shortId;
            }
        }

        throw new InvalidOperationException(
            "A unique short code could not be generated.");
    }

    private async Task<User?> GetCurrentUserAsync()
    {
        var email = User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        return await userHelper.FindUserByEmailIncludeUrlsAsync(email);
    }

    private static bool UserOwnsUrl(User user, Url url)
    {
        return user.Urls.Any(userUrl => userUrl.Id == url.Id);
    }

    private async Task<object> BuildStats(Url url)
    {
        var totalAccessCount = url.AccessLogs.Count;
        var lastAccess = url.AccessLogs.OrderByDescending(log => log.AccessedAt)
            .FirstOrDefault();
        var uniqueIPs = url.AccessLogs.Select(log => log.IPAddress)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var locationStats = await GetGroupLocation(url.AccessLogs);
        var referrerStats = GetGroupedStats(url.AccessLogs,
            GetReferrerHost);
        var osStats = GetGroupedStats(url.AccessLogs,
            log => deviceInfoHelper.GetDeviceInfo(log.UserAgent).OS);
        var deviceStats = GetGroupedStats(url.AccessLogs,
            log => deviceInfoHelper.GetDeviceInfo(log.UserAgent).DeviceType);
        var browserStats = GetGroupedStats(url.AccessLogs,
            log => deviceInfoHelper.GetDeviceInfo(log.UserAgent).Browser);

        var lastAccessDevice = lastAccess != null
            ? deviceInfoHelper.GetDeviceInfo(lastAccess.UserAgent)
            : null;

        return new
        {
            url.Id,
            url = url.LongUrl,
            shortCode = url.ShortId,
            url.CreatedAt,
            url.UpdatedAt,
            TotalAccessCount = totalAccessCount,
            LastAccessed = lastAccess?.AccessedAt,
            UniqueIPCount = uniqueIPs,
            LastAccessDevice = lastAccessDevice == null
                ? null
                : new
                {
                    lastAccessDevice.DeviceType,
                    lastAccessDevice.OS,
                    lastAccessDevice.Browser
                },
            LocationStats = locationStats,
            ReferrerStats = referrerStats,
            OSStats = osStats,
            DeviceStats = deviceStats,
            BrowserStats = browserStats
        };
    }

    private static IEnumerable<GroupStats> GetGroupedStats(
        IEnumerable<AccessLog> logs,
        Func<AccessLog, string?> keySelector)
    {
        return logs
            .Select(log => keySelector(log)?.Trim())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .GroupBy(key => key!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new GroupStats
                { Key = group.Key, Count = group.Count() })
            .OrderByDescending(stat => stat.Count)
            .ToList();
    }

    private static string GetReferrerHost(AccessLog log)
    {
        if (string.IsNullOrWhiteSpace(log.Ref))
        {
            return "Direct";
        }

        return Uri.TryCreate(log.Ref, UriKind.Absolute, out var uri) &&
               !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : "Unknown";
    }

    private async Task<IEnumerable<object>> GetGroupLocation(
        IEnumerable<AccessLog> logs)
    {
        var group = GetGroupedStats(logs, log => log.IPAddress).ToList();
        var cancellationToken = HttpContext.RequestAborted;
        using var semaphore = new SemaphoreSlim(LocationLookupConcurrency);

        var tasks = group.Select(async ip =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var location = await _geoLocationHelper.GetCountryAsync(
                    ip.Key,
                    cancellationToken);
                return location != null
                    ? (object)new
                    {
                        location.Country,
                        location.City,
                        Flags = location.Flag,
                        ip.Count
                    }
                    : new
                    {
                        Country = "Unknown",
                        Count = ip.Count
                    };
            }
            finally
            {
                semaphore.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }
}

public class GroupStats
{
    public string Key { get; set; } = string.Empty;
    public int Count { get; set; }
}
