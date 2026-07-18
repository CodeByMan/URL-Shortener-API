using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using URLShortening.Helpers;

namespace URLShortening.Tests;

public class GeoHelperTests
{
    [Fact]
    public async Task ProviderUnavailable_ReturnsNull()
    {
        var helper = CreateHelper(new HttpResponseMessage(
            HttpStatusCode.ServiceUnavailable));

        var result = await helper.GetCountryAsync("8.8.8.8");

        Assert.Null(result);
    }

    [Fact]
    public async Task InvalidJson_ReturnsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json")
        };
        var helper = CreateHelper(response);

        var result = await helper.GetCountryAsync("8.8.8.8");

        Assert.Null(result);
    }

    [Fact]
    public async Task PrivateAddress_DoesNotCallProvider()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://ipwho.is/")
        };
        var helper = new GeoHelper(client, CreateCache(),
            NullLogger<GeoHelper>.Instance);

        var result = await helper.GetCountryAsync("127.0.0.1");

        Assert.Null(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SuccessfulLookup_IsCached()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"country\":\"United States\",\"city\":\"Example\"}")
        };
        var handler = new StubHandler(response);
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://ipwho.is/")
        };
        var helper = new GeoHelper(client, CreateCache(),
            NullLogger<GeoHelper>.Instance);

        var first = await helper.GetCountryAsync("8.8.8.8");
        var second = await helper.GetCountryAsync("8.8.8.8");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, handler.CallCount);
    }

    private static GeoHelper CreateHelper(HttpResponseMessage response)
    {
        var client = new HttpClient(new StubHandler(response))
        {
            BaseAddress = new Uri("https://ipwho.is/")
        };
        return new GeoHelper(client, CreateCache(),
            NullLogger<GeoHelper>.Instance);
    }

    private static IMemoryCache CreateCache()
    {
        return new MemoryCache(new MemoryCacheOptions());
    }

    private sealed class StubHandler(HttpResponseMessage response)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(response);
        }
    }
}
