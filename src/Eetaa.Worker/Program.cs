using Eetaa.Worker;
using Eetaa.Worker.Configuration;
using Eetaa.Worker.Data;
using Eetaa.Worker.Discovery;
using Eetaa.Worker.Ingestion;
using Eetaa.Worker.Storage;

using Serilog;
using Serilog.Events;

var cliOptions = WorkerCliOptions.Parse(args);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog();

    // Register CLI options
    builder.Services.AddSingleton(cliOptions);
    builder.Services.Configure<EetaaOptions>(builder.Configuration.GetSection(EetaaOptions.SectionName));

    // Register Data access
    builder.Services.AddSingleton<ISqliteConnectionFactory>(_ => new SqliteConnectionFactory(cliOptions.DatabasePath));
    builder.Services.AddSingleton<ISqliteQueueRepository, SqliteQueueRepository>();
    builder.Services.AddSingleton<ISqliteLocalAuthorityQueueRepository, SqliteLocalAuthorityQueueRepository>();
    builder.Services.AddSingleton<ISettlementDetailsRepository, SqliteSettlementDetailsRepository>();
    builder.Services.AddSingleton<ILocalAuthorityDetailsRepository, SqliteLocalAuthorityDetailsRepository>();

    // Register CSV & Storage channel services
    builder.Services.AddSingleton<ISettlementCsvParser, SettlementCsvParser>();
    builder.Services.AddSingleton<ILocalAuthorityCsvParser, LocalAuthorityCsvParser>();
    builder.Services.AddScoped<ISingleWriterChannel, SingleWriterChannel>();
    builder.Services.AddScoped<ISingleWriterStorageEngine, SingleWriterStorageEngine>();

    builder.Services.AddScoped<ILocalAuthorityChannel, LocalAuthorityChannel>();
    builder.Services.AddScoped<ISingleWriterChannel<LocalAuthorityPayload>>(sp => sp.GetRequiredService<ILocalAuthorityChannel>());
    builder.Services.AddScoped<ILocalAuthorityStorageEngine, LocalAuthorityStorageEngine>();

    // Register Discovery crawler
    builder.Services.AddHttpClient<IIndexDiscoveryCrawler, IndexDiscoveryCrawler>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddHttpClient<ILocalAuthorityDiscoveryCrawler, LocalAuthorityDiscoveryCrawler>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddSingleton<IndexParser>();
    builder.Services.AddSingleton(new CrawlerOptions
    {
        BaseUrl = cliOptions.SettlementsBaseUrl ?? cliOptions.BaseUrl,
        Concurrency = cliOptions.Concurrency,
        DelayMs = cliOptions.DelayMs,
        StartPage = cliOptions.StartPage,
        MaxPages = cliOptions.MaxPages
    });
    builder.Services.AddSingleton(new LocalAuthorityCrawlerOptions
    {
        BaseUrl = cliOptions.AuthoritiesBaseUrl,
        Concurrency = cliOptions.Concurrency,
        DelayMs = cliOptions.DelayMs,
        StartPage = cliOptions.StartPage,
        MaxPages = cliOptions.MaxPages
    });

    // Register Ingestion & Retry services
    builder.Services.AddHttpClient("Settlements", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EetaaSettlementWorker/1.0 (+https://github.com/eetaa)");
    });
    builder.Services.AddHttpClient("Authorities", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EetaaLocalAuthorityWorker/1.0 (+https://github.com/eetaa)");
    });
    builder.Services.AddSingleton<IRetryPolicy, ExponentialBackoffRetryPolicy>();
    builder.Services.AddSingleton(new IngestionOptions
    {
        CsvBaseUrl = "https://eetaa.gr/eetaa/metaboles/oikmet2csv.php",
        Concurrency = cliOptions.Concurrency,
        DelayMs = cliOptions.DelayMs,
        RetryFailed = cliOptions.RetryFailed,
        Force = cliOptions.Force
    });
    builder.Services.AddSingleton(new LocalAuthorityIngestionOptions
    {
        CsvBaseUrl = "https://eetaa.gr/eetaa/metaboles/dkmet2csv.php",
        Concurrency = cliOptions.Concurrency,
        DelayMs = cliOptions.DelayMs,
        RetryFailed = cliOptions.RetryFailed,
        Force = cliOptions.Force
    });

    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();
    host.Run();
}
finally
{
    Log.CloseAndFlush();
}