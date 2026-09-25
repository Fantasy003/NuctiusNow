using Microsoft.EntityFrameworkCore;
using NuctiusNow.Components.User_Related;

namespace NuctiusNow.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users { get; set; }
    public DbSet<Tag> Tags { get; set; }
    public DbSet<DetailNews> DetailNews { get; set; }
    public DbSet<News> News { get; set; }
    public DbSet<Preference> Preferences { get; set; }
    public DbSet<UserPreference> UserPreferences { get; set; }
    public DbSet<UserPreferenceTag> UserPreferenceTags { get; set; }
    public DbSet<UserNews> UserNews { get; set; }
    public DbSet<NewsComment> NewsComments { get; set; }
    public DbSet<UserInterestKeyword> UserInterestKeywords { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>()
            .Property(u => u.Birthday)
            .HasColumnType("timestamp");

        modelBuilder.Entity<UserPreference>()
            .HasOne(up => up.User)
            .WithMany(u => u.UserPreferences)
            .HasForeignKey(up => up.IdUser);

        modelBuilder.Entity<UserPreference>()
            .HasOne(up => up.Preference)
            .WithMany(p => p.UserPreferences)
            .HasForeignKey(up => up.IdPreference);

        modelBuilder.Entity<UserPreferenceTag>()
            .HasOne(upt => upt.UserPreference)
            .WithMany(up => up.UserPreferenceTags)
            .HasForeignKey(upt => upt.IdUserPreference);

        modelBuilder.Entity<UserPreferenceTag>()
            .HasOne(upt => upt.Tag)
            .WithMany(t => t.UserPreferenceTags)
            .HasForeignKey(upt => upt.IdTag);

        modelBuilder.Entity<UserInterestKeyword>()
            .HasOne(k => k.User)
            .WithMany(u => u.InterestKeywords)
            .HasForeignKey(k => k.IdUser)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserInterestKeyword>()
            .HasIndex(k => new { k.IdUser, k.Keyword })
            .IsUnique();
    }
}
