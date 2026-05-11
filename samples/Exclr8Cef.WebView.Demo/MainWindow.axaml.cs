using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Exclr8Cef.WebView.Demo;

public partial class MainWindow : Window
{
    /// <summary>
    /// Backing collection for the host-side event console pane. Every CEF
    /// callback we surface to .NET (navigation, title, loading, cursor, …)
    /// pushes a row through <see cref="LogEvent"/>. As phases land, more
    /// categories (Console, Load, Download, Dialog, …) will appear here.
    /// </summary>
    public ObservableCollection<EventEntry> Events { get; } = new();

    private const int MaxEvents = 500;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        var htmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, "test-page.html");
        if (System.IO.File.Exists(htmlPath))
        {
            Browser.Url = "file://" + htmlPath;
        }

        Browser.PropertyChanged += OnBrowserPropertyChanged;

        // Subscribe to the underlying CefBrowser's event stack as soon as it
        // exists (creation is lazy — happens on the first arrange with size).
        Browser.AttachedToVisualTree += OnBrowserAttachedForEvents;

        AddressBox.Text = Browser.Url;
        BackButton.IsEnabled = Browser.CanGoBack;
        ForwardButton.IsEnabled = Browser.CanGoForward;

        LogEvent("init", "Demo started");
    }

    // Hook events on the underlying tech-neutral CefBrowser. Done lazily
    // because the WebView creates its CefBrowser on first arrange.
    private bool _browserEventsHooked;
    private void OnBrowserAttachedForEvents(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(TryHookBrowserEvents,
            Avalonia.Threading.DispatcherPriority.Background);
    }
    private void TryHookBrowserEvents()
    {
        if (_browserEventsHooked) return;
        if (Browser.Browser is not { } b)
        {
            // Try again after the next layout pass.
            Avalonia.Threading.Dispatcher.UIThread.Post(TryHookBrowserEvents,
                Avalonia.Threading.DispatcherPriority.Background);
            return;
        }
        _browserEventsHooked = true;
        b.ConsoleMessage += OnBrowserConsoleMessage;
    }

    private void OnBrowserConsoleMessage(object? sender, Exclr8Cef.ConsoleMessageEventArgs e)
    {
        // Truncate source URL to the last path segment for readability.
        var src = e.Source;
        var slash = src.LastIndexOf('/');
        if (slash >= 0 && slash < src.Length - 1) src = src[(slash + 1)..];
        var loc = string.IsNullOrEmpty(src) ? "" : $"  ({src}:{e.Line})";
        LogEvent("console." + e.Level.ToString().ToLowerInvariant(), e.Message + loc);
    }

    private void LogEvent(string category, string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => LogEvent(category, message));
            return;
        }

        Events.Add(new EventEntry(DateTime.Now.ToString("HH:mm:ss.fff"), category, message));
        while (Events.Count > MaxEvents) Events.RemoveAt(0);

        EventScroller.ScrollToEnd();
    }

    private void OnClearEventsClick(object? sender, RoutedEventArgs e) => Events.Clear();

    private void OnBrowserPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Exclr8Cef.WebView.WebView.UrlProperty)
        {
            var url = e.GetNewValue<string>();
            if (AddressBox.Text != url) AddressBox.Text = url;
            LogEvent("url", url ?? "");
        }
        else if (e.Property == Exclr8Cef.WebView.WebView.TitleProperty)
        {
            var title = e.GetNewValue<string>();
            TitleText.Text = string.IsNullOrEmpty(title) ? "(loading…)" : title;
            Title = string.IsNullOrEmpty(title)
                ? "Exclr8CEF Avalonia Demo"
                : $"{title} — Exclr8CEF Avalonia Demo";
            LogEvent("title", title ?? "");
        }
        else if (e.Property == Exclr8Cef.WebView.WebView.IsLoadingProperty)
        {
            var loading = e.GetNewValue<bool>();
            ReloadButton.Content = loading ? "✕" : "⟳";
            StatusText.Text = loading ? "Loading…" : "";
            LogEvent("loading", loading ? "started" : "finished");
        }
        else if (e.Property == Exclr8Cef.WebView.WebView.CanGoBackProperty)
        {
            BackButton.IsEnabled = e.GetNewValue<bool>();
        }
        else if (e.Property == Exclr8Cef.WebView.WebView.CanGoForwardProperty)
        {
            ForwardButton.IsEnabled = e.GetNewValue<bool>();
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => Browser.GoBack();
    private void OnForwardClick(object? sender, RoutedEventArgs e) => Browser.GoForward();
    private void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        if (Browser.IsLoading) Browser.StopLoad();
        else Browser.Reload();
    }
    private void OnStopClick(object? sender, RoutedEventArgs e) => Browser.StopLoad();
    private void OnDevToolsClick(object? sender, RoutedEventArgs e) => Browser.ShowDevTools();

    private async void OnRunJsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var result = await Browser.EvaluateJavaScriptAsync(
                "({ title: document.title, h1: document.querySelector('h1')?.innerText, " +
                "buttonText: document.querySelector('button')?.innerText, " +
                "url: location.href, " +
                "now: new Date().toISOString() })");
            StatusText.Text = $"JS → {result}";
            LogEvent("js", result);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"JS error: {ex.Message}";
            LogEvent("js-err", ex.Message);
        }
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            var url = AddressBox.Text ?? "";
            if (!url.Contains("://") && !url.StartsWith("data:") && !url.StartsWith("chrome:"))
            {
                url = "https://" + url;
            }
            Browser.Url = url;
            e.Handled = true;
        }
    }

    private async void OnSavePdfClick(object? sender, RoutedEventArgs e)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"exclr8cef-{DateTime.Now:yyyyMMdd-HHmmss}.pdf");
        StatusText.Text = $"Writing {path}…";
        SavePdfButton.IsEnabled = false;
        try
        {
            bool ok = await Browser.PrintToPdfAsync(path);
            StatusText.Text = ok ? $"Saved: {path}" : "PDF export failed.";
            LogEvent("pdf", ok ? path : "failed");
        }
        finally
        {
            SavePdfButton.IsEnabled = true;
        }
    }
}

/// <summary>A single row in the event console.</summary>
public sealed record EventEntry(string Time, string Category, string Message);

/// <summary>
/// Maps an event category to a coloured badge background. Adding a new
/// category? Add a colour here; unmapped categories fall back to the
/// neutral pill colour.
/// </summary>
public sealed class CategoryToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, IBrush> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["init"]            = SolidColorBrush.Parse("#94e2d5"),
        ["url"]             = SolidColorBrush.Parse("#89b4fa"),
        ["title"]           = SolidColorBrush.Parse("#cba6f7"),
        ["loading"]         = SolidColorBrush.Parse("#f9e2af"),
        ["js"]              = SolidColorBrush.Parse("#a6e3a1"),
        ["js-err"]          = SolidColorBrush.Parse("#f38ba8"),
        ["pdf"]             = SolidColorBrush.Parse("#fab387"),
        ["console.verbose"] = SolidColorBrush.Parse("#6c7086"),
        ["console.info"]    = SolidColorBrush.Parse("#a6e3a1"),
        ["console.warning"] = SolidColorBrush.Parse("#f9e2af"),
        ["console.error"]   = SolidColorBrush.Parse("#f38ba8"),
        ["console.fatal"]   = SolidColorBrush.Parse("#f38ba8"),
        ["console.default"] = SolidColorBrush.Parse("#a6adc8"),
    };

    private static readonly IBrush Fallback = SolidColorBrush.Parse("#a6adc8");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string cat && Map.TryGetValue(cat, out var b) ? b : Fallback;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
