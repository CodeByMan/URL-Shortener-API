using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace URLShortening.Data;

public class DataContext(DbContextOptions<DataContext> options) :
    IdentityDbContext<User>(options)
{
    public DbSet<Url> Urls { set; get; }
    public DbSet<AccessLog> AccessLogs { set; get; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Url>(entity =>
        {
            entity.Property(u => u.LongUrl).HasMaxLength(450);
            entity.HasIndex(u => u.ShortId).IsUnique();
            entity.HasIndex(u => u.LongUrl);
        });

        builder.Entity<AccessLog>()
            .HasOne<Url>()
            .WithMany(url => url.AccessLogs)
            .HasForeignKey(log => log.UrlId);

        builder.Entity<User>()
            .Property(user => user.EmailConfirmationCodeHash)
            .HasMaxLength(64);

        base.OnModelCreating(builder);
    }
}
