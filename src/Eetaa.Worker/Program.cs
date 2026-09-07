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

    // Register Data access
    builder.Services.AddSingleton<ISqliteConnectionFactory>(_ => new SqliteConnectionFactory(cliOptions.DatabasePath));
    builder.Services.AddSingleton<ISqliteQueueRepository, SqliteQueueRepository>();
    builder.Services.AddSingleton<ISettlementDetailsRepository, SqliteSettlementDetailsRepository>();

    // Register CSV & Storage channel services
    builder.Services.AddSingleton<ISettlementCsvParser, SettlementCsvParser>();
    builder.Services.AddSingleton<ISingleWriterChannel, SingleWriterChannel>();
    builder.Services.AddSingleton<ISingleWriterStorageEngine, SingleWriterStorageEngine>();

    // Register Discovery crawler
    builder.Services.AddHttpClient<IIndexDiscoveryCrawler, IndexDiscoveryCrawler>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddSingleton<IndexParser>();
    builder.Services.AddSingleton(new CrawlerOptions
    {
        BaseUrl = cliOptions.BaseUrl,
        Concurrency = cliOptions.Concurrency,
        DelayMs = cliOptions.DelayMs,
        StartPage = cliOptions.StartPage,
        MaxPages = cliOptions.MaxPages
    });

    // Register Ingestion & Retry services
    builder.Services.AddHttpClient<ISettlementIngestionService, SettlementIngestionService>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EetaaSettlementWorker/1.0 (+https://github.com/eetaa)");
    });
    builder.Services.AddSingleton<IRetryPolicy, ExponentialBackoffRetryPolicy>();
    builder.Services.AddSingleton(new IngestionOptions
    {
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
