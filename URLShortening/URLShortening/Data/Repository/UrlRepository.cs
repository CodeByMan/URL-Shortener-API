using Microsoft.EntityFrameworkCore;

namespace URLShortening.Data.Repository;

public class UrlRepository(DataContext context) : Repository<Url>(context),
    IUrlRepository
{
    public async Task<Url?> FindByShortUrl(string shortUrl)
    {
        return await context.Urls
            .Include(u => u.AccessLogs)
            .FirstOrDefaultAsync(u => u.ShortId == shortUrl);
    }

    public async Task<Url?> FindRedirectTargetByShortUrl(string shortUrl)
    {
        return await context.Urls
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.ShortId == shortUrl);
    }

    public async Task<Url?> FindByLongUrl(string lonUrl)
    {
        return await context.Urls
            .Include(u => u.AccessLogs)
            .FirstOrDefaultAsync(u => u.LongUrl == lonUrl);
    }

    public async Task<bool> ShortIdExistsAsync(string shortId)
    {
        return await context.Urls.AnyAsync(u => u.ShortId == shortId);
    }

    public async Task<IEnumerable<Url>> GetTopUrlsAsync()
    {
        return await context.Urls
            .Include(u => u.AccessLogs)
            .OrderByDescending(u => u.AccessLogs.Count)
            .Take(5)
            .ToListAsync();
    }
}
