using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Orbita.Core;

namespace Orbita.Desktop;

public partial class StudioWindow : Window
{
    private readonly CancellationTokenSource closing = new();
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(1) };
    private Uri? studio;

    public StudioWindow()
    {
        InitializeComponent();
        Loaded += StartAsync;
        Closed += OnClosed;
    }

    private async void StartAsync(object sender, RoutedEventArgs args)
    {
        Loaded -= StartAsync;
        try
        {
            var ct = closing.Token;
            var configured = Environment.GetEnvironmentVariable("ORBITA_PORT");
            var port = configured == null ? 7784 : int.TryParse(configured, out var value) ? value : 0;
            if (port is < 1024 or > 65535) throw new StudioException("ORBITA_PORT deve estar entre 1024 e 65535.");
            studio = new Uri($"http://127.0.0.1:{port}/");
            await EnsureServiceAsync(studio, ct);
            var profile = Path.Combine(StudioFiles.ResolveRoot(), "orbita", "webview");
            var context = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            ct.ThrowIfCancellationRequested();
            await WorkspaceView.EnsureCoreWebView2Async(context);
            ct.ThrowIfCancellationRequested();
            var view = WorkspaceView.CoreWebView2;
            view.Settings.AreBrowserAcceleratorKeysEnabled = false;
            view.NavigationStarting += (_, e) => { e.Cancel = !IsStudioUrl(e.Uri); };
            view.NewWindowRequested += (_, e) => { e.Handled = true; };
            view.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) { StatusText.Text = "O workspace local não carregou. Você pode tentar abrir no navegador."; return; }
                Title = $"Orbita Studio v{OrbitaRelease.Version}";
                WorkspaceView.Visibility = Visibility.Visible;
                StatusPanel.Visibility = Visibility.Collapsed;
            };
            BrowserButton.Visibility = Visibility.Visible;
            view.Navigate(studio.AbsoluteUri);
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (WebView2RuntimeNotFoundException)
        {
            StatusText.Text = "O WebView2 Runtime é necessário para abrir a janela do Orbita. O workspace também está disponível no navegador.";
            BrowserButton.Visibility = Visibility.Visible;
        }
        catch (StudioException error) { StatusText.Text = error.Message; }
        catch (Exception) { StatusText.Text = "Não foi possível abrir a janela do Orbita. Confira a instalação e tente novamente."; }
    }

    private bool IsStudioUrl(string target) => studio != null && Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttp && uri.Host == studio.Host && uri.Port == studio.Port;

    private async Task EnsureServiceAsync(Uri address, CancellationToken ct)
    {
        var current = await ReadVersionAsync(address, ct);
        if (current != null) { RequireVersion(current); return; }

        var exe = Path.Combine(AppContext.BaseDirectory, "studio", "Orbita.Studio.exe");
        if (!File.Exists(exe)) exe = Path.Combine(AppContext.BaseDirectory, "Orbita.Studio.exe");
        if (!File.Exists(exe)) throw new StudioException("Extraia todo o pacote Windows do Orbita antes de abrir o desktop.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        start.ArgumentList.Add("--sem-navegador");
        using var worker = Process.Start(start) ?? throw new StudioException("Não foi possível iniciar o serviço local do Orbita.");
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (await ReadVersionAsync(address, ct) is { } version) { RequireVersion(version); return; }
            if (worker.HasExited) throw new StudioException("O serviço local encerrou ao iniciar. Confira se outra janela usa a mesma pasta de dados.");
            await Task.Delay(225, ct);
        }
        throw new StudioException("O serviço local demorou para iniciar. Confira se a porta está livre.");
    }

    private async Task<string?> ReadVersionAsync(Uri address, CancellationToken ct)
    {
        try
        {
            using var reply = await http.GetAsync(new Uri(address, "build"), ct);
            return reply.IsSuccessStatusCode ? (await reply.Content.ReadAsStringAsync(ct)).Trim() : "desconhecida";
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private static void RequireVersion(string version)
    {
        if (version != OrbitaRelease.Version)
            throw new StudioException("Outra versão do Orbita está aberta nesta porta. Aguarde a missão terminar e feche a versão anterior antes de atualizar.");
    }

    private void OpenBrowser(object sender, RoutedEventArgs args)
    {
        if (studio != null) Process.Start(new ProcessStartInfo(studio.AbsoluteUri) { UseShellExecute = true });
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        closing.Cancel();
        WorkspaceView.Dispose();
        http.Dispose();
        closing.Dispose();
    }
}
