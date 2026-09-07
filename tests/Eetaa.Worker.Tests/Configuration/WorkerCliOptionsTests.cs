using Eetaa.Worker.Configuration;

namespace Eetaa.Worker.Tests.Configuration;

public class WorkerCliOptionsTests
{
    [Fact]
    public void Parse_DefaultValues_AreCorrect()
    {
        var options = WorkerCliOptions.Parse([]);

        Assert.Equal("all", options.Mode);
        Assert.Equal("data/eetaa.db", options.DatabasePath);
        Assert.Equal("https://eetaa.gr/eetaa/metaboles/oik_metaboles.php", options.BaseUrl);
        Assert.Equal(3, options.Concurrency);
        Assert.Equal(150, options.DelayMs);
        Assert.Equal(1, options.StartPage);
        Assert.Null(options.MaxPages);
        Assert.False(options.RetryFailed);
        Assert.False(options.Force);
    }

    [Fact]
    public void Parse_WithFlagsEqualsSyntax_ParsesCorrectly()
    {
        var args = new[]
        {
            "--mode=discover",
            "--db=custom/path/settlements.db",
            "--base-url=https://example.com/index.php",
            "--concurrency=5",
            "--delay-ms=250",
            "--start-page=10",
            "--max-pages=50",
            "--retry-failed",
            "--force"
        };

        var options = WorkerCliOptions.Parse(args);

        Assert.Equal("discover", options.Mode);
        Assert.Equal("custom/path/settlements.db", options.DatabasePath);
        Assert.Equal("https://example.com/index.php", options.BaseUrl);
        Assert.Equal(5, options.Concurrency);
        Assert.Equal(250, options.DelayMs);
        Assert.Equal(10, options.StartPage);
        Assert.Equal(50, options.MaxPages);
        Assert.True(options.RetryFailed);
        Assert.True(options.Force);
    }

    [Fact]
    public void Parse_WithFlagsSpaceSyntax_ParsesCorrectly()
    {
        var args = new[]
        {
            "--mode", "discover",
            "--db", "custom/db.sqlite",
            "--start-page", "2",
            "--max-pages", "10"
        };

        var options = WorkerCliOptions.Parse(args);

        Assert.Equal("discover", options.Mode);
        Assert.Equal("custom/db.sqlite", options.DatabasePath);
        Assert.Equal(2, options.StartPage);
        Assert.Equal(10, options.MaxPages);
    }
}
