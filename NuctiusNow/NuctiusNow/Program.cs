using Microsoft.EntityFrameworkCore;
using NuctiusNow.Components;
using NuctiusNow.Components.User_Related;
using NuctiusNow.Data;
using NuctiusNow.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. SERVICIOS Y ESTADOS
builder.Services.AddScoped<ThemeState>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// Configuraci�n de PostgreSQL con Entity Framework Core
var connectionString = builder.Configuration.GetConnectionString("PostgresConnection");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// Registro del servicio de autenticaci�n
builder.Services.AddScoped<AuthService>();
// Registro del servicio de sesión de usuario para control de acceso en cliente
builder.Services.AddScoped<UserSessionService>();
// Registro del motor de recomendaciones (AI helper)
builder.Services.AddScoped<RecommendationEngine>();
// Registro del servicio LLM local (llamar a un servidor LLM local por HTTP)
builder.Services.AddScoped<ILocalLlmService, LocalLlmService>();
// Servicio para almacenar noticias capturadas desde la web
builder.Services.AddScoped<NewsIngestService>();
// Register HttpClient factory for server-side HTTP calls (reCAPTCHA verification)
builder.Services.AddHttpClient();

var app = builder.Build();

// 2. PIPELINE / MIDDLEWARES
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    // WebAssembly debugging can interfere with hybrid interactive server mode and may throw
    // response-started exceptions when the debug proxy connects.
    // app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

// 3. MAPEO DE BLAZOR
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(NuctiusNow.Client._Imports).Assembly);

app.Run();