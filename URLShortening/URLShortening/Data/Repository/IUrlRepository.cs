namespace URLShortening.Data.Repository;

public interface IUrlRepository : IRepository<Url>
{
    Task<Url?> FindByShortUrl(string shortUrl);
    Task<Url?> FindRedirectTargetByShortUrl(string shortUrl);
    Task<Url?> FindByLongUrl(string lonUrl);
    Task<bool> ShortIdExistsAsync(string shortId);
    Task<IEnumerable<Url>> GetTopUrlsAsync();
}
