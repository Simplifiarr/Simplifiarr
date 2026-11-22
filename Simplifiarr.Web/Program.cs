using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;
using Simplifiarr.Web.Components;
using Simplifiarr.Web.Support;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

// Where settings, history, caches and logs live. Blank means the default
// (%ProgramData%\Simplifiarr); a second install sets Simplifiarr:DataPath
// so the two never share files. Must run before any service is created.
AppPaths.Configure(builder.Configuration["Simplifiarr:DataPath"]);

builder.Logging.AddProvider(new FileLoggerProvider());

// Give in-flight work 20 seconds to finish when the service is stopped.
builder.Services.Configure<HostOptions>(options =>
    options.ShutdownTimeout = TimeSpan.FromSeconds(20));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The client keeps its own login cookie, so the handler mustn't.
builder.Services.AddHttpClient<ITorrentClient, QbittorrentClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false });

builder.Services.AddHttpClient<IRadarrClient, RadarrClient>();
builder.Services.AddHttpClient<ISonarrClient, SonarrClient>();
// Redirects are followed by hand: some lead to magnet links, which the
// automatic redirect handling can't cope with.
builder.Services.AddHttpClient<IProwlarrClient, ProwlarrClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<IJellyfinClient, JellyfinClient>();
builder.Services.AddHttpClient<IPosterService, PosterService>();

builder.Services.AddSingleton<IFileScannerService, FileScannerService>();
builder.Services.AddSingleton<IHardlinkService, HardlinkService>();
builder.Services.AddSingleton<IDuplicateCandidateService, DuplicateCandidateService>();
builder.Services.AddSingleton<IDashboardSummaryService, DashboardSummaryService>();
builder.Services.AddSingleton<IHardlinkConversionPlanner, HardlinkConversionPlanner>();
builder.Services.AddSingleton<IRemediationPreviewService, RemediationPreviewService>();
builder.Services.AddSingleton<IHardlinkRemediationService, HardlinkRemediationService>();
builder.Services.AddSingleton<IQuarantineService, QuarantineService>();
builder.Services.AddSingleton<IQuarantineInventoryService, QuarantineInventoryService>();
builder.Services.AddSingleton<IQuarantineManagementService, QuarantineManagementService>();
builder.Services.AddSingleton<IHistoryService, HistoryService>();
builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<IMediaCatalogService, MediaCatalogService>();
builder.Services.AddSingleton<IMediaCatalogPopulationService, MediaCatalogPopulationService>();
builder.Services.AddSingleton<ITorrentCatalogPopulationService, TorrentCatalogPopulationService>();
builder.Services.AddSingleton<IAssetTorrentMatcher, AssetTorrentMatcher>();
builder.Services.AddSingleton<IReleaseParser, ReleaseParser>();
builder.Services.AddSingleton<IAssetMatchAnalysisService, AssetMatchAnalysisService>();
builder.Services.AddSingleton<IMediaActionAnalysisService, MediaActionAnalysisService>();
builder.Services.AddSingleton<IAssetIdentityService, AssetIdentityService>();
builder.Services.AddSingleton<IPromotionPlanService, PromotionPlanService>();
builder.Services.AddSingleton<IPromotionPreviewService, PromotionPreviewService>();
builder.Services.AddSingleton<IPromotionExecuteService, PromotionExecuteService>();
builder.Services.AddSingleton<IMoviePromotionCandidateService, MoviePromotionCandidateService>();
builder.Services.AddSingleton<IPromotionCandidateCacheService, PromotionCandidateCacheService>();
builder.Services.AddSingleton<IMediaMetadataExtractor, FfprobeMediaMetadataExtractor>();
builder.Services.AddSingleton<ITorrentClassificationService, TorrentClassificationService>();
builder.Services.AddSingleton<ILibraryTargetResolver, LibraryTargetResolver>();
builder.Services.AddSingleton<IMediaPromotionService, MediaPromotionService>();
builder.Services.AddSingleton<IWasteCandidateCacheService, WasteCandidateCacheService>();
builder.Services.AddSingleton<IWasteCandidateService, WasteCandidateService>();
// Simplifiarr only runs on Windows; this one says so out loud.
#pragma warning disable CA1416
builder.Services.AddSingleton<IPlaybackErrorService, PlaybackErrorService>();
#pragma warning restore CA1416
builder.Services.AddSingleton<ISizeReductionCandidateCacheService, SizeReductionCandidateCacheService>();
builder.Services.AddSingleton<ISizeReductionCandidateService, SizeReductionCandidateService>();
builder.Services.AddSingleton<IFileInventoryService, FileInventoryCacheService>();
builder.Services.AddSingleton<IIgnoredItemsService, IgnoredItemsService>();
builder.Services.AddSingleton<IDuplicateAssetAnalysisService, DuplicateAssetAnalysisService>();
builder.Services.AddSingleton<IArrEnrichmentService, ArrEnrichmentService>();
builder.Services.AddSingleton<ICollectionService, CollectionService>();
builder.Services.AddSingleton<ICollectionBuilderService, CollectionBuilderService>();
builder.Services.AddSingleton<IJunkFileScannerService, JunkFileScannerService>();
builder.Services.AddSingleton<IDownloadCleanupService, DownloadCleanupService>();
builder.Services.AddSingleton<ISeedMoveService, SeedMoveService>();
builder.Services.AddSingleton<IStorageService, StorageService>();
builder.Services.AddHttpClient<IArrPathService, ArrPathService>();
builder.Services.AddHttpClient<ISeerrClient, SeerrClient>();
builder.Services.AddSingleton<IWatchHistoryService, WatchHistoryService>();
builder.Services.AddSingleton<ISizeReductionDownloadService, SizeReductionDownloadService>();
builder.Services.AddHttpClient<ISonarrShowClient, SonarrShowClient>();
builder.Services.AddSingleton<IRollingShowService, RollingShowService>();
builder.Services.AddSingleton<IStorageMoveService>(services => new StorageMoveService(
    services.GetRequiredService<IArrPathService>(),
    services.GetRequiredService<IStorageService>(),
    services.GetRequiredService<IHistoryService>(),
    services.GetRequiredService<IScanCoordinator>(),
    services.GetRequiredService<ISeerrClient>()));
builder.Services.AddSingleton<IArrNamingService, ArrNamingService>();
builder.Services.AddSingleton<ITorrentPieceVerificationService, TorrentPieceVerificationService>();
builder.Services.AddSingleton<ITorrentReconstructionService, TorrentReconstructionService>();
builder.Services.AddSingleton<IWholeTorrentCandidateService, WholeTorrentCandidateService>();
builder.Services.AddSingleton<ICrossSeedScanService, CrossSeedScanService>();
builder.Services.AddSingleton<IScanStepProvider,DefaultScanStepProvider>();
builder.Services.AddSingleton<IScanCoordinator, ScanCoordinator>();
builder.Services.AddHostedService<Simplifiarr.Web.AutoScanService>();

var app = builder.Build();

// Route the Core project's log messages into the real logger (and so into
// the log file).
var coreLogger = app.Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("Simplifiarr");

AppLog.Sink = (level, message, exception) =>
    coreLogger.Log(
        level switch
        {
            AppLogLevel.Error => LogLevel.Error,
            AppLogLevel.Warning => LogLevel.Warning,
            _ => LogLevel.Information
        },
        exception,
        "{Message}",
        message);

coreLogger.LogInformation(
    "Simplifiarr starting. Data folder: {DataRoot}",
    AppPaths.DataRoot);

using (var scope = app.Services.CreateScope())
{
    var settingsService =
        scope.ServiceProvider.GetRequiredService<ISettingsService>();

    await settingsService.GetSettingsAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

// An item's current poster, fetched from Jellyfin by Simplifiarr. The
// browser can't be sent to Jellyfin for it: the address in Settings is
// often "localhost", which means something else on a phone.
app.MapGet(
    "/poster-image/{itemId}",
    async (string itemId, IJellyfinClient jellyfin) =>
    {
        if (!Guid.TryParse(itemId, out _))
        {
            return Results.NotFound();
        }

        try
        {
            var image =
                await jellyfin.GetPosterAsync(itemId, maxWidth: 400);

            return image is null
                ? Results.NotFound()
                : Results.File(image.Bytes, image.ContentType);
        }
        catch (InvalidOperationException)
        {
            return Results.NotFound();
        }
    });

// Sonarr calls this when it imports an episode ("On Import" in its
// Connect settings), so Rolling Shows can act straight away. The key in
// the address keeps anything else from triggering it.
app.MapPost(
    "/api/hooks/sonarr/{key}",
    (string key, System.Text.Json.JsonElement body, IRollingShowService rolling) =>
    {
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(key),
                System.Text.Encoding.UTF8.GetBytes(rolling.WebhookKey)))
        {
            return Results.NotFound();
        }

        var eventType =
            body.TryGetProperty("eventType", out var type) ? type.GetString() : null;

        int? seriesId =
            body.TryGetProperty("series", out var series) &&
            series.TryGetProperty("id", out var id) &&
            id.ValueKind == System.Text.Json.JsonValueKind.Number
                ? id.GetInt32()
                : null;

        // Sonarr's "Test" button sends eventType "Test": answer and do nothing.
        if (eventType == "Download" && seriesId is not null &&
            rolling.Rules.Any(rule => rule.IsOn && rule.SeriesId == seriesId))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await rolling.RunAsync(seriesId);
                }
                catch (Exception exception)
                {
                    Simplifiarr.Core.Utilities.AppLog.Warn("Rolling Shows failed after a Sonarr import.", exception);
                }
            });
        }

        return Results.Ok();
    })
    .DisableAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

try
{
    app.Run();
}
catch (OperationCanceledException)
{
    // If stopping takes longer than the shutdown timeout, WindowsServiceLifetime
    // throws this from StopAsync. Left unhandled it crashes the process, which
    // then lingers in Windows Error Reporting with the install folder locked and
    // makes the updater hang. Exit cleanly instead.
}
