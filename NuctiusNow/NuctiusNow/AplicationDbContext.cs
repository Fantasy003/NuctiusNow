using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();

        // Colocá acá tu cadena de conexión real a PostgreSQL
        optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=nuctiusnow;Username=postgres;Password=admin");

        return new ApplicationDbContext(optionsBuilder.Options);
    }
}
public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    // Tu tabla de noticias
    public DbSet<NoticiaEntity> Noticias { get; set; }
}

// El modelo que representará la tabla en Postgres
public class NoticiaEntity
{
    public string Id { get; set; } // Postgres lo tomará como Clave Primaria (text)
    public string Titulo { get; set; }
    public string CuerpoHtml { get; set; }
    public string ImagenUrl { get; set; }
    public string Categoria { get; set; }
    public DateTime Fecha { get; set; } = DateTime.UtcNow; // Postgres prefiere UTC para DateTime
}