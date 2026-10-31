using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace ChudPresence.Link.Windows;

public partial class App : Application
{
    private readonly string[] arguments;
    private readonly CancellationTokenSource stopping = new();
    private WebApplication? server;
    private PresenceCoordinator? presence;
    private LinkWindow? window;
    private NativeTray? tray;
    private NativeWindowConstraints? constraints;
    private Task? presencePump;
    private bool exiting;

    public App(string[] args)
    {
        arguments = args;
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var options = LaunchOptions.Parse(arguments,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChudPresence Link"),
                LinkServer.DefaultPort);
            var directory = options.DataDirectory;
            var port = options.Port;

            var store = new LinkStore(directory);
            var coordinator = new PresenceCoordinator(new DiscordRpc());
            presence = coordinator;
            server = LinkServer.Create(store, coordinator, port);
            await server.StartAsync(stopping.Token);
            presencePump = Task.Run(() => RunPresenceAsync(coordinator, stopping.Token));
            window = new LinkWindow(store, coordinator, port, directory);
            constraints = new NativeWindowConstraints(WinRT.Interop.WindowNative.GetWindowHandle(window), 640, 520);
            window.AppWindow.Closing += (_, eventArgs) =>
            {
                if (exiting) return;
                eventArgs.Cancel = true;
                window.AppWindow.Hide();
            };
            window.AppWindow.Changed += (_, _) =>
            {
                if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } && window.AppWindow.IsVisible)
                    window.AppWindow.Hide();
            };
            tray = new NativeTray(window.OpenWindow, () => Pair("browser"), () => Pair("mobile"), RequestExit);
            if (options.Minimized) window.AppWindow.Hide();
            else window.OpenWindow();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Program.ShowMessage($"ChudPresence Link could not start.\n\n{error.Message}\n\nIf the address is in use, close the other copy or choose another port with --port.", true);
            RequestExit();
        }
    }

    private void Pair(string kind)
    {
        if (window is null) return;
        window.OpenWindow();
        window.CreateCode(kind);
    }

    private static async Task RunPresenceAsync(PresenceCoordinator coordinator, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token)) await coordinator.TickAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async void RequestExit()
    {
        if (exiting) return;
        exiting = true;
        tray?.Dispose();
        stopping.Cancel();
        try
        {
            if (presencePump is not null) await presencePump;
            if (server is not null) await server.StopAsync(TimeSpan.FromSeconds(3));
        }
        catch (OperationCanceledException) { }
        finally
        {
            try
            {
                if (presence is not null) await presence.DisposeAsync();
            }
            finally
            {
                if (server is not null) await server.DisposeAsync();
                stopping.Dispose();
                constraints?.Dispose();
                window?.Close();
                Exit();
            }
        }
    }
}
