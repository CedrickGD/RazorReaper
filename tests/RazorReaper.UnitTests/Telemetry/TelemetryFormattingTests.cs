using RazorReaper.Configuration;
using RazorReaper.Services.Implementations;

namespace RazorReaper.UnitTests.Telemetry;

public sealed class TelemetryFormattingTests
{
    [Fact]
    public void HasValidConfigurationAcceptsEndpointOnlyWithoutSharedKey()
    {
        var settings = new TelemetrySettings
        {
            Enabled = true,
            Endpoint = "https://backend.rr-admin-panel.workers.dev/api/ingest"
        };

        var valid = TelemetryFormatting.HasValidConfiguration(settings, out var error);

        Assert.True(valid);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://backend.rr-admin-panel.workers.dev/api/ingest")]
    public void HasValidConfigurationStillRejectsBadEndpoints(string endpoint)
    {
        var settings = new TelemetrySettings { Enabled = true, Endpoint = endpoint };

        var valid = TelemetryFormatting.HasValidConfiguration(settings, out var error);

        Assert.False(valid);
        Assert.Contains("endpoint", error, StringComparison.OrdinalIgnoreCase);
    }
}
