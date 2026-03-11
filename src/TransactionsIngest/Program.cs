using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TransactionsIngest.Configuration;
using TransactionsIngest.Data;
using TransactionsIngest.Services;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((_, config) =>
    {
        config
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddCommandLine(args);
    })
    .ConfigureLogging((ctx, logging) =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(
            ctx.HostingEnvironment.IsDevelopment()
                ? LogLevel.Debug
                : LogLevel.Information);
    })
    .ConfigureServices((ctx, services) =>
    {
        var appSettingsSection = ctx.Configuration.GetSection(AppSettings.SectionName);
        services.Configure<AppSettings>(appSettingsSection);
        var appSettings = appSettingsSection.Get<AppSettings>() ?? new AppSettings();

        var connectionString = ctx.Configuration.GetConnectionString("DefaultConnection")
            ?? "Data Source=transactions.db";

        services.AddDbContext<TransactionsDbContext>(opts =>
            opts.UseSqlite(connectionString));

        // use mock feed if configured, otherwise call the real API
        if (!string.IsNullOrWhiteSpace(appSettings.MockFeedPath))
            services.AddTransient<ITransactionFetcher, MockTransactionFetcher>();
        else
            services.AddHttpClient<ITransactionFetcher, HttpTransactionFetcher>();

        services.AddTransient<IngestionService>();
    })
    .Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TransactionsDbContext>();
    logger.LogInformation("Applying database migrations...");
    await db.Database.MigrateAsync();
}

using (var scope = host.Services.CreateScope())
{
    var service = scope.ServiceProvider.GetRequiredService<IngestionService>();
    try
    {
        await service.RunAsync();
        logger.LogInformation("Ingestion run finished successfully.");
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Ingestion run failed.");
        return 1;
    }
}

return 0;