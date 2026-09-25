using System.Globalization;
using System.Threading.RateLimiting;
using Grundstuecksfinder.Components;
using Grundstuecksfinder.Data;
using Grundstuecksfinder.Infrastructure;
using Grundstuecksfinder.Services;
using Grundstuecksfinder.Services.Importers;
using Grundstuecksfinder.Services.Importers.Inspire;
using Grundstuecksfinder.Services.Importers.Nrw;
using Grundstuecksfinder.Services.Importers.Postcodes;
using Grundstuecksfinder.Services.Importers.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

var enableDevSeeding = builder.Configuration.GetValue<bool>("EnableDevSeeding");

builder.Logging.ClearProviders();
// The console sink is configured here and not in "Serilog:WriteTo" because it needs the
// invariant culture: the container runs with LANG=de_DE.UTF-8 for the page, and log lines must
// stay machine-readable. Configuring it in both places wrote every line to the console twice.
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

// ── Property sources ──────────────────────────────────────────────────────────
// Add a new Bundesland/country by implementing IPropertySource and registering it here.
// Every source can be switched off with "Enabled": false, which stops its imports and hides
// its rows (see DisabledSources) without deleting them.
builder.Services.AddSingleton(TimeProvider.System);

// Downloaded files (NRW's ZIP, Hauskoordinaten files) are written here while they are read and
// deleted right after.
var importWorkDirectory = ImportWorkDirectory.Resolve(builder.Configuration[ImportWorkDirectory.ConfigKey]);

var nrwEnabled = builder.Configuration.GetSection("Import:Nrw").Get<NrwImporterOptions>()?.Enabled ?? true;
builder.Services.Configure<NrwImporterOptions>(builder.Configuration.GetSection("Import:Nrw"));
if (nrwEnabled)
{
    builder.Services.AddScoped<IPropertySource>(sp => new NrwPropertyImporter(
        sp.GetRequiredService<ILogger<NrwPropertyImporter>>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<IOptions<NrwImporterOptions>>(),
        sp.GetRequiredService<TimeProvider>(),
        workDirectory: importWorkDirectory));
}

// One IPropertySource per configured INSPIRE-split Bundesland (e.g. Schleswig-Holstein) –
// adding a state is adding an "Import:Inspire:Sources" entry, no new code. A broken entry
// fails startup (and so the deploy) instead of the nightly import.
var inspireSources = builder.Configuration.GetSection("Import:Inspire:Sources").Get<List<InspireSourceOptions>>() ?? [];
var inspireConfigErrors = InspireSourceOptions.Validate(inspireSources, [NrwPropertyImporter.SourceId]);
if (inspireConfigErrors.Count > 0)
    throw new InvalidOperationException("Invalid Import:Inspire:Sources config:\n" + string.Join("\n", inspireConfigErrors));

// When sources are re-imported (see RefreshPlanner); checked at startup like the sources.
var refreshOptions = builder.Configuration.GetSection(RefreshOptions.SectionName).Get<RefreshOptions>() ?? new RefreshOptions();
var refreshConfigErrors = refreshOptions.Validate(inspireSources.ToDictionary(s => s.Source, s => s.Refresh));
if (refreshConfigErrors.Count > 0)
    throw new InvalidOperationException("Invalid import refresh config:\n" + string.Join("\n", refreshConfigErrors));
builder.Services.AddSingleton(refreshOptions);

builder.Services.AddSingleton(new DisabledSources(
    inspireSources.Where(s => !s.Enabled).Select(s => s.Source)
        .Concat(nrwEnabled ? [] : [NrwPropertyImporter.SourceId])
        .ToList()));
builder.Services.AddSingleton(SupportedStates.FromSources(
    inspireSources.Where(s => s.Enabled).Select(s => s.Source)
        .Concat(nrwEnabled ? [NrwPropertyImporter.SourceId] : [])));
// Each request (headers, body and parsing) is bounded by the source's RequestTimeoutSeconds
// instead: HttpClient.Timeout stops counting once the headers arrive.
builder.Services.AddDownloadClient(InspirePropertyImporter.HttpClientName);
// Same for NRW's ~1 GB ZIP: bounded by NrwImporterOptions.DownloadTimeoutSeconds per attempt.
builder.Services.AddDownloadClient(NrwPropertyImporter.HttpClientName);
// Postcode areas for sources without PLZ; the download is bounded by DownloadTimeoutSeconds.
builder.Services.AddSingleton(builder.Configuration.GetSection("Import:PostcodeAreas").Get<PostcodeAreaOptions>() ?? new PostcodeAreaOptions());
builder.Services.AddDownloadClient(PostcodeAreaProvider.HttpClientName);
builder.Services.AddSingleton<IPostcodeAreaProvider, PostcodeAreaProvider>();
// Intact name spellings for sources whose own export lost them (Hessen); uses the Inspire client.
builder.Services.AddSingleton<NameCatalogLoader>();
foreach (var inspireSource in inspireSources.Where(s => s.Enabled))
{
    builder.Services.AddScoped<IPropertySource>(sp => new InspirePropertyImporter(
        sp.GetRequiredService<ILogger<InspirePropertyImporter>>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        inspireSource,
        postcodeAreas: sp.GetRequiredService<IPostcodeAreaProvider>(),
        nameCatalogLoader: sp.GetRequiredService<NameCatalogLoader>(),
        loggerFactory: sp.GetRequiredService<ILoggerFactory>(),
        refreshPolicy: refreshOptions.PolicyFor(inspireSource.Refresh)));
}

var minRetainedRatio = builder.Configuration.GetValue("Import:MinRetainedRatio", PropertyBulkWriter.DefaultMinRetainedRatio);
builder.Services.AddScoped(sp => new PropertyBulkWriter(
    sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<ILogger<PropertyBulkWriter>>(), minRetainedRatio,
    timeProvider: sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<ImportStateStore>();
builder.Services.AddScoped<ImportRunner>();
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
builder.Services.AddScoped<FilterOptionsService>();

// ── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database")
    .AddCheck<ImportHealthCheck>("imports", failureStatus: HealthStatus.Degraded);

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
