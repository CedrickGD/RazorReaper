using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Services;
using RazorReaper.Services.Implementations;
using RazorReaper.UnitTests.Infrastructure;
using SkiaSharp;

namespace RazorReaper.UnitTests.Identity;

public sealed class CustomerAccountTests
{
    private sealed class Identity : IInstallIdentityService
    {
        public bool IsRegistered { get; set; } = true;
        public Task EnsureRegisteredAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<InstallPublicKeyJwk?> GetPublicKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult<InstallPublicKeyJwk?>(null);
        public Task<SignedRequestHeaders?> SignAsync(HttpMethod method, Uri uri, byte[] body, DateTimeOffset now, CancellationToken cancellationToken = default) => Task.FromResult<SignedRequestHeaders?>(null);
        public void ReportSignedRequestRejected(Uri uri, SignedRequestHeaders rejectedHeaders) { }
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static CustomerAccountService Create(FakeHttpClientFactory factory, Identity? identity = null) => new(factory, identity ?? new Identity(), Options.Create(new AppConfiguration()));
    private const string Profile = """{"id":"account-one","displayName":"Member One","discordId":"123456789","discordUsername":"member.one","avatar":null}""";

    [Fact]
    public async Task UnverifiedInstallationDoesNotSendAccountRequests()
    {
        using var handler = new RecordingHttpMessageHandler();
        using var factory = new FakeHttpClientFactory(handler);
        var service = Create(factory, new Identity { IsRegistered = false });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BeginSignInAsync());
        Assert.Empty(handler.Requests);
    }
    [Theory]
    [InlineData("https://rr-admin-panel.pages.dev/api/discord/account/authorize?request=abc", true)]
    [InlineData("https://evil.test/api/discord/account/authorize?request=abc", false)]
    [InlineData("http://rr-admin-panel.pages.dev/api/discord/account/authorize", false)]
    [InlineData("https://rr-admin-panel.pages.dev/other", false)]
    [InlineData("https://member@rr-admin-panel.pages.dev/api/discord/account/authorize", false)]
    public void SignInLinksAreRestrictedToTheConfiguredService(string url, bool allowed)
    {
        using var factory = new FakeHttpClientFactory(new RecordingHttpMessageHandler());
        Assert.Equal(allowed, Create(factory).IsValidVerificationUrl(url));
    }
    [Fact]
    public async Task BrowserApprovalDoesNotSignInUntilExplicitConfirmation()
    {
        using var handler = new RecordingHttpMessageHandler { ResponseFactory = (_, _) => Task.FromResult(Json("{\"ok\":true,\"status\":\"confirm\",\"account\":" + Profile + "}")) };
        using var factory = new FakeHttpClientFactory(handler);
        var service = Create(factory);
        var candidate = await service.PollSignInAsync("request-one");
        Assert.Equal("Member One", candidate?.DisplayName);
        Assert.Null(service.Profile);
        Assert.All(factory.RequestedNames, name => Assert.Equal("RazorReaperTelemetry", name));
    }
    [Fact]
    public async Task FailedSavesPreserveTheCurrentProfileAndSignedOutResponsesClearIt()
    {
        using var handler = new RecordingHttpMessageHandler { ResponseFactory = (_, _) => Task.FromResult(Json("{\"ok\":true,\"account\":" + Profile + "}")) };
        using var factory = new FakeHttpClientFactory(handler);
        var service = Create(factory);
        await service.RefreshAsync();
        handler.ResponseFactory = (_, _) => Task.FromResult(Json("""{"ok":false,"error":"Invalid profile"}""", HttpStatusCode.BadRequest));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveProfileAsync("x", null));
        Assert.Equal("Member One", service.Profile?.DisplayName);
        handler.ResponseFactory = (_, _) => Task.FromResult(Json("""{"ok":false,"code":"account_signed_out"}""", HttpStatusCode.Forbidden));
        await service.RefreshAsync();
        Assert.Null(service.Profile);
        Assert.Empty(service.Devices);
    }
    [Fact]
    public void AvatarIsCroppedAndReencodedToABoundedSquare()
    {
        using var bitmap = new SKBitmap(600, 300);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Purple);
        using var image = SKImage.FromBitmap(bitmap);
        using var original = image.Encode(SKEncodedImageFormat.Png, 100);
        var result = AccountAvatar.Create(original.ToArray());
        Assert.StartsWith("data:image/webp;base64,", result);
        Assert.True(result.Length < 350000);
        using var decoded = SKBitmap.Decode(Convert.FromBase64String(result.Split(',')[1]));
        Assert.Equal(256, decoded.Width);
        Assert.Equal(256, decoded.Height);
    }
    [Fact]
    public void AvatarRejectsNonImagesAndOversizedFiles()
    {
        Assert.Throws<InvalidOperationException>(() => AccountAvatar.Create(Encoding.UTF8.GetBytes("<svg onload='bad'/>")));
        Assert.Throws<InvalidOperationException>(() => AccountAvatar.Create(new byte[8 * 1024 * 1024 + 1]));
    }
}
