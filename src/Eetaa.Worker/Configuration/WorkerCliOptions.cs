namespace Eetaa.Worker.Configuration;

public sealed class WorkerCliOptions
{
    public string Mode { get; set; } = "all"; // all, discover, download
    public string DatabasePath { get; set; } = "data/eetaa.db";
    public string BaseUrl { get; set; } = "https://eetaa.gr/eetaa/metaboles/oik_metaboles.php";
    public int Concurrency { get; set; } = 3;
    public int DelayMs { get; set; } = 150;
    public int StartPage { get; set; } = 1;
    public int? MaxPages { get; set; }
    public bool RetryFailed { get; set; }
    public bool Force { get; set; }

    public static WorkerCliOptions Parse(string[] args)
    {
        var options = new WorkerCliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase))
            {
                options.Mode = arg["--mode=".Length..].Trim().ToLowerInvariant();
            }
            else if (string.Equals(arg, "--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                options.Mode = args[++i].Trim().ToLowerInvariant();
            }
            else if (arg.StartsWith("--db=", StringComparison.OrdinalIgnoreCase))
            {
                options.DatabasePath = arg["--db=".Length..].Trim();
            }
            else if (string.Equals(arg, "--db", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                options.DatabasePath = args[++i].Trim();
            }
            else if (arg.StartsWith("--base-url=", StringComparison.OrdinalIgnoreCase))
            {
                options.BaseUrl = arg["--base-url=".Length..].Trim();
            }
            else if (string.Equals(arg, "--base-url", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                options.BaseUrl = args[++i].Trim();
            }
            else if (arg.StartsWith("--concurrency=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(arg["--concurrency=".Length..], out var c)) options.Concurrency = c;
            }
            else if (string.Equals(arg, "--concurrency", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var c)) options.Concurrency = c;
            }
            else if (arg.StartsWith("--delay-ms=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(arg["--delay-ms=".Length..], out var d)) options.DelayMs = d;
            }
            else if (string.Equals(arg, "--delay-ms", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var d)) options.DelayMs = d;
            }
            else if (arg.StartsWith("--start-page=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(arg["--start-page=".Length..], out var sp)) options.StartPage = sp;
            }
            else if (string.Equals(arg, "--start-page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var sp)) options.StartPage = sp;
            }
            else if (arg.StartsWith("--max-pages=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(arg["--max-pages=".Length..], out var mp)) options.MaxPages = mp;
            }
            else if (string.Equals(arg, "--max-pages", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var mp)) options.MaxPages = mp;
            }
            else if (string.Equals(arg, "--retry-failed", StringComparison.OrdinalIgnoreCase))
            {
                options.RetryFailed = true;
            }
            else if (string.Equals(arg, "--force", StringComparison.OrdinalIgnoreCase))
            {
                options.Force = true;
            }
        }

        return options;
    }
}
