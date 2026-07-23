namespace RazorReaper
{
    public partial class MainPage : ContentPage
    {
        public MainPage()
        {
            InitializeComponent();

#if WINDOWS
            blazorWebView.BlazorWebViewInitialized += OnBlazorWebViewInitialized;
#endif
        }

#if WINDOWS
        private void OnBlazorWebViewInitialized(object? sender, Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
        {
            if (e.WebView is Microsoft.UI.Xaml.Controls.WebView2 webView2)
            {
                // Disable user-initiated zoom so the UI can't drift across sessions.
                webView2.CoreWebView2.Settings.IsZoomControlEnabled = false;
                webView2.CoreWebView2.Settings.IsPinchZoomEnabled = false;

                // Serve the hosted-media cache through a virtual host so large
                // files (videos) stream from disk with Range support instead of
                // being embedded as base64 data URLs.
                System.IO.Directory.CreateDirectory(Services.MediaCachePaths.Directory);
                webView2.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    Services.MediaCachePaths.VirtualHost,
                    Services.MediaCachePaths.Directory,
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

                // Reset zoom factor to 1.0 on every launch (clears any persisted zoom).
                webView2.CoreWebView2.NavigationCompleted += (_, _) =>
                {
                    _ = webView2.CoreWebView2.ExecuteScriptAsync(
                        "document.addEventListener('wheel', function(e){ if(e.ctrlKey) e.preventDefault(); }, {passive:false});" +
                        "document.addEventListener('keydown', function(e){ if(e.ctrlKey && (e.key==='+' || e.key==='-' || e.key==='=' || e.key==='0')) e.preventDefault(); });");
                };
            }
        }
#endif
    }
}
