using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using CodexVsix.Services;

using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CodexVsix.UI;

internal sealed class MermaidWebViewPreview : Grid
{
    private const string MermaidAssetFileName = "mermaid.min.js";
    private const string MermaidHostFileName = "mermaid-preview.html";
    private const string MermaidBundleResourceName = "CodexVsix.UI.Assets.mermaid.min.js";
    private const string MermaidAssetsHostName = "appassets.codexvsix.local";
    private const int MermaidBootstrapTimeoutMs = 8000;
    private static readonly Lazy<string> MermaidBundle = new(LoadMermaidBundle);
    private static readonly Lazy<string> MermaidHostHtml = new(BuildHostHtml);
    private readonly string _code;
    private readonly WebView2 _webView;
    private readonly Image _snapshotImage;
    private readonly Border _statusHost;
    private readonly TextBlock _statusText;
    private readonly DispatcherTimer _bootstrapTimer;
    private readonly LocalizationService _localization;
    private bool _isInitialized;
    private bool _snapshotRequested;
    private bool _isDisposed;
    private bool _renderReady;
    private bool _hasTerminalState;

    public MermaidWebViewPreview(string code)
    {
        this._code = code ?? string.Empty;
        this._localization = new LocalizationService();
        this.Height = 180;
        this.MinHeight = 120;
        this.Margin = new Thickness(0, 2, 0, 0);
        this.VerticalAlignment = VerticalAlignment.Top;
        this.ClipToBounds = true;

        this._webView = new WebView2
        {
            Visibility = Visibility.Visible,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Height = this.Height
        };
        this._webView.SetValue(UIElement.OpacityProperty, 0d);

        this._snapshotImage = new Image
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly
        };

        this._statusText = new TextBlock
        {
            Text = this._localization.MermaidLoadingPreview,
            Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        };

        this._statusHost = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Child = this._statusText
        };

        this._bootstrapTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(MermaidBootstrapTimeoutMs + 2000)
        };
        this._bootstrapTimer.Tick += this.OnBootstrapTimerTick;

        _ = this.Children.Add(this._snapshotImage);
        _ = this.Children.Add(this._webView);
        _ = this.Children.Add(this._statusHost);

        Loaded += this.OnLoaded;
        Unloaded += this.OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (this._renderReady && !this._isDisposed)
        {
            this._statusHost.Visibility = Visibility.Collapsed;
            this._snapshotImage.Visibility = Visibility.Collapsed;
            this._webView.Visibility = Visibility.Visible;
            this._webView.SetValue(UIElement.OpacityProperty, 1d);
            this._webView.IsHitTestVisible = true;
            return;
        }

        if (this._isInitialized)
        {
            return;
        }

        this._isInitialized = true;
        _ = this.InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (this._snapshotImage.Source is not null || this._hasTerminalState)
        {
            this.DisposeWebView();
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexVsix",
                "WebView2");
            _ = Directory.CreateDirectory(userDataFolder);
            string assetsFolder = EnsureLocalAssets();

            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await this._webView.EnsureCoreWebView2Async(environment);
            this._webView.CoreWebView2.WebMessageReceived += this.OnWebMessageReceived;
            this._webView.CoreWebView2.NavigationCompleted += this.OnNavigationCompleted;
            this._webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            this._webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            this._webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            this._webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            this._webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                MermaidAssetsHostName,
                assetsFolder,
                CoreWebView2HostResourceAccessKind.Allow);
            this.ResetBootstrapTimer();
            this._webView.CoreWebView2.Navigate(BuildPreviewUri(this._code));
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            this.Fail(this._localization.MermaidInitFailed);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            return;
        }

        this.Fail(string.Format(CultureInfo.CurrentUICulture, this._localization.MermaidLoadFailedFormat, e.WebErrorStatus));
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message = e.TryGetWebMessageAsString() ?? string.Empty;
        if (message.StartsWith("height:", StringComparison.Ordinal))
        {
            string rawHeight = message.Substring("height:".Length);
            if (double.TryParse(rawHeight, NumberStyles.Float, CultureInfo.InvariantCulture, out double height))
            {
                this.ApplyMeasuredHeight(Math.Max(120, height));
            }

            return;
        }

        if (string.Equals(message, "ready", StringComparison.Ordinal))
        {
            this._renderReady = true;
            this.StopBootstrapTimer();
            this._statusHost.Visibility = Visibility.Collapsed;
            this._snapshotImage.Visibility = Visibility.Collapsed;
            this._webView.Visibility = Visibility.Visible;
            this._webView.SetValue(UIElement.OpacityProperty, 1d);
            this._webView.IsHitTestVisible = true;

            return;
        }

        if (message.StartsWith("error:", StringComparison.Ordinal))
        {
            string detail = message.Substring("error:".Length).Trim();
            this.Fail(string.IsNullOrWhiteSpace(detail)
                ? this._localization.MermaidRenderFailed
                : string.Format(CultureInfo.CurrentUICulture, this._localization.MermaidRenderFailedFormat, detail));
        }
    }

    private void ShowStatus(string text)
    {
        this._statusText.Text = text;
        this._statusHost.Visibility = Visibility.Visible;
        this._snapshotImage.Visibility = Visibility.Collapsed;
        this._webView.Visibility = Visibility.Visible;
        this._webView.SetValue(UIElement.OpacityProperty, 0d);
        this._webView.IsHitTestVisible = false;
    }

    private async Task FreezePreviewAsync()
    {
        try
        {
            if (this._isDisposed || this._webView.CoreWebView2 is null)
            {
                return;
            }

            using MemoryStream stream = new();
            await this._webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            stream.Position = 0;

            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            this._hasTerminalState = true;
            this.StopBootstrapTimer();
            this._snapshotImage.Source = image;
            this._snapshotImage.Visibility = Visibility.Visible;
            this._statusHost.Visibility = Visibility.Collapsed;
            this._webView.Visibility = Visibility.Hidden;
            this.DisposeWebView();
            this.ApplyMeasuredHeight(Math.Max(120, image.Height));
        }
        catch (Exception ex)
        {
            if (!this.IsLoaded && !this._isDisposed)
            {
                this._snapshotRequested = false;
                return;
            }

            Trace.WriteLine(ex);
            this.Fail(this._localization.MermaidFreezeFailed);
        }
    }

    private void DisposeWebView()
    {
        if (this._isDisposed)
        {
            return;
        }

        this._isDisposed = true;
        this.StopBootstrapTimer();
        if (this._webView.CoreWebView2 is not null)
        {
            this._webView.CoreWebView2.WebMessageReceived -= this.OnWebMessageReceived;
            this._webView.CoreWebView2.NavigationCompleted -= this.OnNavigationCompleted;
        }

        this._webView.Dispose();
    }

    private void OnBootstrapTimerTick(object? sender, EventArgs e)
    {
        if (this._hasTerminalState || this._renderReady || this._snapshotImage.Source is not null)
        {
            this.StopBootstrapTimer();
            return;
        }

        this.Fail(this._localization.MermaidLoadTimeout);
    }

    private void ResetBootstrapTimer()
    {
        if (this._hasTerminalState || this._renderReady || this._snapshotImage.Source is not null)
        {
            return;
        }

        this._bootstrapTimer.Stop();
        this._bootstrapTimer.Start();
    }

    private void StopBootstrapTimer()
    {
        this._bootstrapTimer.Stop();
    }

    private void Fail(string text)
    {
        this._hasTerminalState = true;
        this.ShowStatus(text);
        this.DisposeWebView();
    }

    private void ApplyMeasuredHeight(double height)
    {
        this.Height = height;
        if (!this._isDisposed)
        {
            this._webView.Height = height;
        }

        this.InvalidateMeasure();
        this.InvalidateArrange();
        this.UpdateLayout();

        FrameworkElement? parent = this.Parent as FrameworkElement;
        while (parent is not null)
        {
            parent.InvalidateMeasure();
            parent.InvalidateArrange();
            parent = parent.Parent as FrameworkElement;
        }
    }

    private static string BuildPreviewUri(string code)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(code ?? string.Empty);
        string encoded = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return $"https://{MermaidAssetsHostName}/{MermaidHostFileName}?code={encoded}";
    }

    private static string BuildHostHtml()
    {
        const string htmlTemplate = """
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8" />
  <style>
    html, body {
      margin: 0;
      padding: 0;
      background: transparent;
      overflow: hidden;
    }

    body {
      font-family: "Segoe UI", sans-serif;
      color: #ffffff;
    }

    #root {
      width: 100%;
      padding: 4px 0 0;
      box-sizing: border-box;
      display: flex;
      justify-content: center;
      align-items: flex-start;
    }

    #error {
      display: none;
      margin: 4px 0 0;
      padding: 12px 14px;
      box-sizing: border-box;
      border-radius: 10px;
      background: rgba(255, 255, 255, 0.08);
      border: 1px solid rgba(255, 255, 255, 0.12);
    }

    svg {
      display: block;
      max-width: 100%;
      height: auto;
    }
  </style>
</head>
<body>
  <div id="root"></div>
  <div id="error"></div>
  <script src="./mermaid.min.js"></script>
  <script>
    const root = document.getElementById('root');
    const errorNode = document.getElementById('error');
    let observer = null;
    let mutationObserver = null;
    let lastHeight = 0;
    let isFinished = false;
    let heightSyncHandle = 0;
    let heightSyncUntil = 0;
    const timeoutId = window.setTimeout(() => {
      fail('Timeout ao inicializar o Mermaid oficial.');
    }, __BOOTSTRAP_TIMEOUT_MS__);

    function postMessage(message) {
      window.chrome?.webview?.postMessage(message);
    }

    function decodeCode() {
      const encoded = new URLSearchParams(window.location.search).get('code') || '';
      if (!encoded) {
        return '';
      }

      const normalized = encoded.replace(/-/g, '+').replace(/_/g, '/');
      const padding = normalized.length % 4 === 0
        ? ''
        : '='.repeat(4 - (normalized.length % 4));
      const binary = window.atob(normalized + padding);
      const bytes = Uint8Array.from(binary, ch => ch.charCodeAt(0));
      return new TextDecoder('utf-8').decode(bytes);
    }

    function resolveMermaidApi() {
      const candidates = [
        window.mermaid,
        window.mermaid?.default,
        window.__esbuild_esm_mermaid_nm?.mermaid?.default,
        window.__esbuild_esm_mermaid_nm?.mermaid
      ];

      for (const candidate of candidates) {
        if (candidate && typeof candidate.initialize === 'function' && typeof candidate.render === 'function') {
          return candidate;
        }
      }

      return null;
    }

    function fail(detail) {
      if (isFinished) {
        return;
      }

      isFinished = true;
      window.clearTimeout(timeoutId);
      errorNode.style.display = 'block';
      errorNode.textContent = detail;
      watchHeight();
      scheduleHeightSync(1200);
      postMessage(`error:${detail}`);
    }

    function measureHeight() {
      const svg = root.querySelector('svg');
      const candidates = [
        document.documentElement?.scrollHeight || 0,
        document.body?.scrollHeight || 0,
        document.documentElement?.offsetHeight || 0,
        document.body?.offsetHeight || 0,
        root.scrollHeight || 0,
        Math.ceil(root.getBoundingClientRect().height),
        Math.ceil(errorNode.getBoundingClientRect().height),
        svg ? Math.ceil(svg.getBoundingClientRect().height) : 0
      ];

      return Math.max(120, ...candidates) + 12;
    }

    function postHeight() {
      window.requestAnimationFrame(() => {
        const measuredHeight = measureHeight();
        if (Math.abs(measuredHeight - lastHeight) > 1) {
          lastHeight = measuredHeight;
          postMessage(`height:${measuredHeight}`);
        }
      });
    }

    function scheduleHeightSync(durationMs) {
      heightSyncUntil = Math.max(heightSyncUntil, Date.now() + durationMs);
      if (heightSyncHandle) {
        return;
      }

      const tick = () => {
        postHeight();
        if (Date.now() < heightSyncUntil) {
          heightSyncHandle = window.requestAnimationFrame(tick);
          return;
        }

        heightSyncHandle = 0;
      };

      heightSyncHandle = window.requestAnimationFrame(tick);
    }

    function watchHeight() {
      if (observer) {
        observer.disconnect();
      }
      if (mutationObserver) {
        mutationObserver.disconnect();
      }

      observer = new ResizeObserver(() => postHeight());
      observer.observe(document.body);
      observer.observe(document.documentElement);
      observer.observe(root);
      if (root.firstElementChild) {
        observer.observe(root.firstElementChild);
      }

      mutationObserver = new MutationObserver(() => scheduleHeightSync(1200));
      mutationObserver.observe(root, { childList: true, subtree: true, attributes: true });
    }

    window.addEventListener('error', (event) => {
      const detail = event?.error?.message || event?.message || '__MERMAID_SCRIPT_ERROR__';
      fail(detail);
    });
    window.addEventListener('resize', () => scheduleHeightSync(1200));

    (async () => {
      try {
        const code = decodeCode();
        const mermaid = resolveMermaidApi();
        if (!mermaid) {
          throw new Error('Bundle local do Mermaid não foi carregado.');
        }

        mermaid.initialize({
          startOnLoad: false,
          securityLevel: 'loose',
          theme: 'base',
          themeVariables: {
            background: 'transparent',
            fontFamily: 'Segoe UI',
            primaryColor: '#21252c',
            primaryTextColor: '#ffffff',
            primaryBorderColor: '#d8d8d8',
            lineColor: '#ffffff',
            secondaryColor: '#21252c',
            secondaryTextColor: '#ffffff',
            secondaryBorderColor: '#d8d8d8',
            tertiaryColor: '#21252c',
            tertiaryTextColor: '#ffffff',
            tertiaryBorderColor: '#d8d8d8',
            mainBkg: '#21252c',
            nodeBorder: '#d8d8d8',
            clusterBkg: 'transparent',
            edgeLabelBackground: '#6b7f95'
          },
          flowchart: {
            useMaxWidth: true,
            htmlLabels: true
          }
        });

        const renderResult = await mermaid.render(`mermaid-${Date.now()}`, code);
        root.innerHTML = renderResult.svg;
        renderResult.bindFunctions?.(root);

        const svg = root.querySelector('svg');
        if (svg) {
          svg.removeAttribute('width');
          svg.style.maxWidth = '100%';
          svg.style.height = 'auto';
        }

        if (isFinished) {
          return;
        }

        isFinished = true;
        window.clearTimeout(timeoutId);
        watchHeight();
        scheduleHeightSync(2000);
        postMessage('ready');
      } catch (error) {
        const detail = error?.message || '__MERMAID_RENDER_FAILED__';
        fail(detail);
      }
    })();
  </script>
</body>
</html>
""";

        return htmlTemplate
            .Replace("__BOOTSTRAP_TIMEOUT_MS__", MermaidBootstrapTimeoutMs.ToString(CultureInfo.InvariantCulture))
            .Replace("__MERMAID_SCRIPT_ERROR__", new LocalizationService().MermaidPreviewScriptError)
            .Replace("__MERMAID_RENDER_FAILED__", new LocalizationService().MermaidRenderFailed);
    }

    private static string EnsureLocalAssets()
    {
        string assetsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexVsix",
            "WebView2Assets");
        _ = Directory.CreateDirectory(assetsFolder);

        string mermaidBundlePath = Path.Combine(assetsFolder, MermaidAssetFileName);
        WriteIfDifferent(mermaidBundlePath, MermaidBundle.Value);

        string mermaidHostPath = Path.Combine(assetsFolder, MermaidHostFileName);
        WriteIfDifferent(mermaidHostPath, MermaidHostHtml.Value);

        return assetsFolder;
    }

    private static void WriteIfDifferent(string path, string content)
    {
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path);
            if (string.Equals(existing, content, StringComparison.Ordinal))
            {
                return;
            }
        }

        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string LoadMermaidBundle()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(MermaidBundleResourceName);
        if (stream is null)
        {
            LocalizationService localization = new();
            throw new InvalidOperationException(string.Format(localization.Culture, localization.MermaidBundleNotFoundFormat, MermaidBundleResourceName));
        }

        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
