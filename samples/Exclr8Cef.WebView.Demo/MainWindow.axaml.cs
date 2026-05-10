using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Exclr8Cef.WebView.Demo;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Load the bundled test page from disk. data: URLs are too fragile
        // for any non-trivial HTML/JS due to URI-reserved char escaping
        // (`?`, `|`, etc. would silently break the parser).
        var htmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, "test-page.html");
        if (System.IO.File.Exists(htmlPath))
        {
            Browser.Url = "file://" + htmlPath;
        }

        // Track WebView property changes to drive the address bar, title,
        // and nav-button enablement.
        Browser.PropertyChanged += OnBrowserPropertyChanged;

        // Initial state.
        AddressBox.Text = Browser.Url;
        BackButton.IsEnabled = Browser.CanGoBack;
        ForwardButton.IsEnabled = Browser.CanGoForward;
    }

    private void OnBrowserPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Exclr8Cef.WebView.WebView.UrlProperty)
        {
            var url = e.GetNewValue<string>();
            if (AddressBox.Text != url) AddressBox.Text = url;
        }
        else if (e.Property == Exclr8Cef.WebView.WebView.TitleProperty)
        {
            var title = e.GetNewValue<string>();
            TitleText.Text = string.IsNullOrEmpty(title) ? "(loading…)" : title;
            Title = string.IsNullOrEmpty(title)
                ? "Exclr8CEF Avalonia Demo"
                : $"{title} — Exclr8CEF Avalonia Demo";
        }
        else if (e.Property == Exclr8Cef.WebView.WebView.IsLoadingProperty)
        {
            var loading = e.GetNewValue<bool>();
            ReloadButton.Content = loading ? "✕" : "⟳";
            StatusText.Text = loading ? "Loading…" : "";
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
        }
        catch (Exception ex)
        {
            StatusText.Text = $"JS error: {ex.Message}";
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
        }
        finally
        {
            SavePdfButton.IsEnabled = true;
        }
    }
}
