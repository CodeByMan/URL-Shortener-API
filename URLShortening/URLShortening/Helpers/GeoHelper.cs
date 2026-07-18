using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using URLShortening.Models;

namespace URLShortening.Helpers;

public class GeoHelper(
    HttpClient httpClient,
    IMemoryCache memoryCache,
    ILogger<GeoHelper> logger) : IGeoHelper
{
    private static readonly TimeSpan SuccessfulLookupLifetime =
        TimeSpan.FromHours(6);
    private static readonly TimeSpan FailedLookupLifetime =
        TimeSpan.FromMinutes(5);

    public async Task<LocationModel?> GetCountryAsync(string ipAddress,
        CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(ipAddress, out var parsedAddress) ||
            !IsPublicAddress(parsedAddress))
        {
            return null;
        }

        var cacheKey = $"geo-location:{parsedAddress}";
        if (memoryCache.TryGetValue(cacheKey, out GeoCacheEntry? cached))
        {
            return cached?.Location;
        }

        try
        {
            using var response = await httpClient.GetAsync(
                Uri.EscapeDataString(ipAddress),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Geolocation provider returned status code {StatusCode} for an IP lookup.",
                    response.StatusCode);
                Cache(cacheKey, null, FailedLookupLifetime);
                return null;
            }

            await using var content = await response.Content
                .ReadAsStreamAsync(cancellationToken);
            var location = await JsonSerializer.DeserializeAsync<LocationModel>(
                content,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                },
                cancellationToken);

            if (string.IsNullOrWhiteSpace(location?.Country))
            {
                Cache(cacheKey, null, FailedLookupLifetime);
                return null;
            }

            Cache(cacheKey, location, SuccessfulLookupLifetime);
            return location;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Geolocation provider request timed out.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception,
                "Geolocation provider request failed.");
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception,
                "Geolocation provider returned invalid JSON.");
        }

        Cache(cacheKey, null, FailedLookupLifetime);
        return null;
    }

    private void Cache(string key, LocationModel? location, TimeSpan lifetime)
    {
        memoryCache.Set(key, new GeoCacheEntry(location), lifetime);
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] != 10 &&
                   !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                   !(bytes[0] == 192 && bytes[1] == 168) &&
                   !(bytes[0] == 169 && bytes[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !address.IsIPv6LinkLocal &&
                   !address.IsIPv6SiteLocal &&
                   !address.IsIPv6Multicast &&
                   !address.Equals(IPAddress.IPv6None);
        }

        return false;
    }

    private sealed record GeoCacheEntry(LocationModel? Location);
}
