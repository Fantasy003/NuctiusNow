using NuctiusNow.Client.Pages;
using NuctiusNow.Components;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Agregá esta línea en tu Program.cs
builder.Services.AddScoped<ThemeState>();

// ======================================================================
// 1. SECCIÓN DE SERVICIOS
// ======================================================================

// ¡FUNDAMENTAL! Agrega el soporte para componentes Razor y sus modos interactivos
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// Leemos la cadena de conexión a PostgreSQL
var connectionString = builder.Configuration.GetConnectionString("PostgresConnection");

// Registramos tu DbContext antes de cerrar el contenedor
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));


// EL CANDADO: Construimos la aplicación
var app = builder.Build();


// ======================================================================
// 2. SECCIÓN DEL PIPELINE / MIDDLEWARES (CONFIGURACIÓN DE LA APP)
// ======================================================================
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseWebAssemblyDebugging(); // Permite depurar el proyecto .Client
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

// Requerido en .NET 8 para la seguridad de formularios y componentes
app.UseAntiforgery();

// ======================================================================System.InvalidOperationException: 'Assembly already defined.'
// 3. MAPEO DE BLAZOR (Lo que hace que tus páginas carguen)
// ======================================================================
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode(); // Sin el AddAdditionalAssemblies // Busca las páginas en tu proyecto Client

app.Run();