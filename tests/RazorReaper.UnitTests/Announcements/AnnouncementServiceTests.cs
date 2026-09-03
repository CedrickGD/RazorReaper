using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Services.Implementations;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Announcements;

public sealed class AnnouncementServiceTests
{
    [Fact]
    public async Task SuccessfulEmptyResponse_IsDistinctFromFailure()
    {
        var handler = new RecordingHttpMessageHandler
        {
            ResponseFactory = static (_, _) => Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                """{"ok":true,"announcements":[]}"""))
        };
        var service = CreateService(handler);

        var result = await service.GetActiveAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(result.Announcements);
    }

    [Fact]
    public async Task HttpFailure_IsReportedWithoutReplacingLastKnownState()
    {
        var handler = new RecordingHttpMessageHandler
        {
            ResponseFactory = static (_, _) => Task.FromResult(
                new HttpResponseMessage((HttpStatusCode)530))
        };
        var service = CreateService(handler);

        var result = await service.GetActiveAsync();

        Assert.False(result.Succeeded);
        Assert.Empty(result.Announcements);
    }

    [Fact]
    public async Task SuccessfulResponse_ReturnsAnnouncementsAndUsesPublicRoute()
    {
        var handler = new RecordingHttpMessageHandler
        {
            ResponseFactory = static (_, _) => Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                """{"ok":true,"announcements":[{"id":7,"title":"Notice","body":"Hello","level":"warning","created_at":"2026-08-29T12:00:00Z"}]}"""))
        };
        var service = CreateService(handler);

        var result = await service.GetActiveAsync();

        Assert.True(result.Succeeded);
        var announcement = Assert.Single(result.Announcements);
        Assert.Equal(7, announcement.Id);
        Assert.Equal("Notice", announcement.Title);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://rr-admin-panel.pages.dev/api/announcements/active", request.Uri?.ToString());
    }

    private static AnnouncementService CreateService(RecordingHttpMessageHandler handler)
    {
        var options = Options.Create(new AppConfiguration
        {
            AdminPanel = new AdminPanelSettings
            {
                BaseUrl = "https://rr-admin-panel.pages.dev",
                RequestTimeoutSeconds = 10
            }
        });

        return new AnnouncementService(
            new HttpClient(handler),
            options,
            new RecordingLogger<AnnouncementService>());
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
