using Eetaa.Worker;
using Eetaa.Worker.Configuration;
using Eetaa.Worker.Data;
using Eetaa.Worker.Discovery;

var cliOptions = WorkerCliOptions.Parse(args);

var builder = Host.CreateApplicationBuilder(args);

// Register CLI options
builder.Services.AddSingleton(cliOptions);

// Register Data access
builder.Services.AddSingleton<ISqliteConnectionFactory>(_ => new SqliteConnectionFactory(cliOptions.DatabasePath));
builder.Services.AddSingleton<ISqliteQueueRepository, SqliteQueueRepository>();

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

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
