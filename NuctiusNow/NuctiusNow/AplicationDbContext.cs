using Microsoft.EntityFrameworkCore;
using NuctiusNow.Components.User_Related; // Importá el namespace de tus entidades

namespace NuctiusNow.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    // Tablas Principales
    public DbSet<NuctiusNow.Components.User_Related.User> Users { get; set; }
    public DbSet<NuctiusNow.Components.User_Related.Tag> Tags { get; set; }
    public DbSet<NuctiusNow.Components.User_Related.DetailNews> DetailNews { get; set; }
    public DbSet<NuctiusNow.Components.User_Related.News> News { get; set; }
    public DbSet<NuctiusNow.Components.User_Related.Preference> Preferences { get; set; }

    // Tablas Intermedias / Relaciones
    public DbSet<NuctiusNow.Components.User_Related.UserPreference> UserPreferences { get; set; }
    public DbSet<NuctiusNow.Components.User_Related.UserPreferenceTag> UserPreferenceTags { get; set; }
    public DbSet<NuctiusNow.Components.User_Related.UserNews> UserNews { get; set; }
    public DbSet<NuctiusNow.Components.User_Related.NewsComment> NewsComments { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configurear Birthday como timestamp with time zone (UTC)
        modelBuilder.Entity<NuctiusNow.Components.User_Related.User>()
            .Property(u => u.Birthday)
            .HasColumnType("timestamp");

        // Configure UserPreferences
        modelBuilder.Entity<NuctiusNow.Components.User_Related.UserPreference>()
            .HasOne(up => up.User)
            .WithMany(u => u.UserPreferences)
            .HasForeignKey(up => up.IdUser);

        modelBuilder.Entity<NuctiusNow.Components.User_Related.UserPreference>()
            .HasOne(up => up.Preference)
            .WithMany(p => p.UserPreferences)
            .HasForeignKey(up => up.IdPreference);

        // Configure UserPreferenceTags (Many-to-Many relationship)
        modelBuilder.Entity<NuctiusNow.Components.User_Related.UserPreferenceTag>()
            .HasOne(upt => upt.UserPreference)
            .WithMany(up => up.UserPreferenceTags)
            .HasForeignKey(upt => upt.IdUserPreference);

        modelBuilder.Entity<NuctiusNow.Components.User_Related.UserPreferenceTag>()
            .HasOne(upt => upt.Tag)
            .WithMany(t => t.UserPreferenceTags)
            .HasForeignKey(upt => upt.IdTag);
    }
}