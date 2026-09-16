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
    public DbSet<User> Users { get; set; }
    public DbSet<Tag> Tags { get; set; }
    public DbSet<DetailNews> DetailNews { get; set; }
    public DbSet<News> News { get; set; }
    public DbSet<Preference> Preferences { get; set; }

    // Tablas Intermedias / Relaciones
    public DbSet<UserPreference> UserPreferences { get; set; }
    public DbSet<UserNews> UserNews { get; set; }
    public DbSet<NewsComment> NewsComments { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configurear Birthday como timestamp with time zone (UTC)
        modelBuilder.Entity<User>()
            .Property(u => u.Birthday)
            .HasColumnType("timestamp");
    }
}