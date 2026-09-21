using System.Globalization;
using System.Threading.RateLimiting;
using Grundstuecksfinder.Components;
using Grundstuecksfinder.Data;
using Grundstuecksfinder.Infrastructure;
using Grundstuecksfinder.Services;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Nrw;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

var enableDevSeeding = builder.Configuration.GetValue<bool>("EnableDevSeeding");

builder.Logging.ClearProviders();
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

// ── Database ──────────────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not configured.");

var dataSource = NpgsqlDataSource.Create(connectionString);
builder.Services.AddSingleton(dataSource);
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(dataSource));

// ── Property importers ────────────────────────────────────────────────────────
// Add a new Bundesland/country by implementing IPropertyImporter and registering it here.
builder.Services.Configure<NrwImporterOptions>(builder.Configuration.GetSection("Import:Nrw"));
builder.Services.AddScoped<IPropertyImporter, NrwPropertyImporter>();

// One IPropertyImporter per configured INSPIRE-split Bundesland (e.g. Schleswig-Holstein) –
// adding a state is adding an "Import:Inspire:Sources" entry, no new code. "Enabled": false
// stops its imports and hides its rows (see DisabledSources) without deleting them.
var inspireSources = builder.Configuration.GetSection("Import:Inspire:Sources").Get<List<InspireSourceOptions>>() ?? [];
builder.Services.AddSingleton(new DisabledSources(
    inspireSources.Where(s => !s.Enabled).Select(s => s.Source).ToList()));
foreach (var inspireSource in inspireSources.Where(s => s.Enabled))
{
    builder.Services.AddScoped<IPropertyImporter>(sp => new InspirePropertyImporter(
        sp.GetRequiredService<ILogger<InspirePropertyImporter>>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        inspireSource));
}

builder.Services.AddScoped<PropertyBulkWriter>();
builder.Services.AddScoped<ImportOrchestrator>();
builder.Services.AddHostedService<ImportWorker>();

// ── Telemetrie (OpenTelemetry-kompatibel via System.Diagnostics.Metrics) ──────
builder.Services.AddSingleton<AppMetrics>();
builder.Services.AddHostedService<TelemetryWorker>();

// ── Shared services ───────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMemoryCache();

builder.Services.AddNominatim(builder.Configuration);

builder.Services.AddScoped<PropertyService>();

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
    await db.Database.MigrateAsync();
    if (app.Environment.IsDevelopment() && enableDevSeeding)
    {
        await DevSeeder.SeedAsync(db);
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles(); // Must be before UseStatusCodePagesWithReExecute to prevent HTML 404 pages for static assets
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseRateLimiter();
app.UseAntiforgery();

app.MapHealthChecks("/health");
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
