using System.Diagnostics;
using System.Threading.RateLimiting;
using HomeLocator.Components;
using HomeLocator.Data;
using HomeLocator.Infrastructure;
using HomeLocator.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Prometheus;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console());

// ── Database ──────────────────────────────────────────────────────────────────
if (Debugger.IsAttached)
{
    builder.Services.AddDbContext<AppDbContext>(o =>
        o.UseSqlite("Data Source=homelocator-dev.db"));
}
else
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not configured.");

    var dataSource = NpgsqlDataSource.Create(connectionString);
    builder.Services.AddSingleton(dataSource);
    builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(dataSource));
    builder.Services.AddTransient<DataImportService>();
    builder.Services.AddHostedService<ImportWorker>();
}

// ── Metrics (prometheus-net) ──────────────────────────────────────────────────
builder.Services.AddSingleton<AppMetrics>();

// ── Shared services ───────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMemoryCache();

// Named client for Nominatim – required User-Agent per usage policy.
builder.Services.AddHttpClient("Nominatim", client =>
{
    client.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("HomeLocator/1.0 (contact@example.com)");
});

builder.Services.AddScoped<PropertyService>();
builder.Services.AddScoped<GeocodingService>();

// ── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database");

// ── Rate limiting ─────────────────────────────────────────────────────────────
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (Debugger.IsAttached)
    {
        await db.Database.EnsureCreatedAsync();
        await DevSeeder.SeedAsync(db);
    }
    else
    {
        await db.Database.MigrateAsync();
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseHttpMetrics();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapHealthChecks("/health");
// Only reachable by Prometheus on the internal Docker network (host header = "app" or "app:8080")
app.MapMetrics()
   .RequireHost("app", "app:8080", "localhost", "localhost:8080", "localhost:5000", "localhost:7000");
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
