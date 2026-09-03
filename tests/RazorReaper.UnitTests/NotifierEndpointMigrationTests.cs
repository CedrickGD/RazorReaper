using RazorReaper.Services.Overlay;

namespace RazorReaper.UnitTests;

public class NotifierEndpointMigrationTests
{
    [Theory]
    [InlineData("https://razorreaper-bot-production.up.railway.app/notifier/stream?token=abc",
                "https://bot.razorreaper.app/notifier/stream?token=abc")]
    [InlineData("http://razorreaper-bot-production.up.railway.app/notifier/stream?token=abc&x=1",
                "https://bot.razorreaper.app/notifier/stream?token=abc&x=1")]
    public void RewritesRetiredRailwayHost(string input, string expected)
        => Assert.Equal(expected, NotifierEndpointMigration.Migrate(input));

    [Theory]
    [InlineData("https://bot.razorreaper.app/notifier/stream?token=abc")]
    [InlineData("https://example.com/notifier/stream?token=abc")]
    [InlineData("")]
    [InlineData("not a url")]
    public void LeavesOtherEndpointsAlone(string input)
        => Assert.Equal(input, NotifierEndpointMigration.Migrate(input));
}
