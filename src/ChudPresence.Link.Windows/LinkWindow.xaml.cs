using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using global::Windows.ApplicationModel.DataTransfer;

namespace ChudPresence.Link.Windows;

public sealed partial class LinkWindow : Window
{
    private readonly LinkStore store;
    private readonly PresenceCoordinator presence;
    private readonly int port;
    private readonly string dataDirectory;
    private readonly DispatcherQueueTimer timer;
    private readonly DispatcherQueueTimer feedbackTimer;
    private readonly List<ServiceCard> serviceCards = [];
    private string[] knownAddresses = [];
    private DeviceItem[] knownDevices = [];
    private long displayedRevision = -1;
    private DateTimeOffset nextAddressRefresh;
    private bool configuringSync;
    private bool startupReady;
    private bool initialized;
    private bool pairingActive;
    private bool? wideLayout;

    public LinkWindow(LinkStore store, PresenceCoordinator presence, int port, string dataDirectory, bool showTrayIcon = true)
    {
        this.store = store;
        this.presence = presence;
        this.port = port;
        this.dataDirectory = dataDirectory;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop { Kind = global::Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Link.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        AppWindow.ResizeClient(new((int)Math.Min(1160 * scale, (display?.WorkArea.Width ?? 1920) - 48 * scale),
            (int)Math.Min(900 * scale, (display?.WorkArea.Height ?? 1080) - 48 * scale)));
        if (display is not null)
            AppWindow.Move(new(display.WorkArea.X + Math.Max(0, (display.WorkArea.Width - AppWindow.Size.Width) / 2),
                display.WorkArea.Y + Math.Max(0, (display.WorkArea.Height - AppWindow.Size.Height) / 2)));
        BrowserAddress.Text = $"http://127.0.0.1:{port}";

        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => RefreshView();
        feedbackTimer = DispatcherQueue.CreateTimer();
        feedbackTimer.Interval = TimeSpan.FromSeconds(6);
        feedbackTimer.IsRepeating = false;
        feedbackTimer.Tick += (_, _) => Feedback.IsOpen = false;
        Root.Loaded += (_, _) => InitializeWindow();
        Root.SizeChanged += (_, _) => UpdateAdaptiveLayout();
        Root.ActualThemeChanged += (_, _) => UpdateTitleBar();
        Closed += (_, _) => { timer.Stop(); feedbackTimer.Stop(); };
    }

    private void InitializeWindow()
    {
        if (initialized) return;
        initialized = true;
        UpdateAdaptiveLayout();
        try
        {
            var savedTheme = File.Exists(ThemePath) ? File.ReadAllText(ThemePath).Trim() : "Default";
            if (Enum.TryParse<ElementTheme>(savedTheme, out var theme) && Enum.IsDefined(theme)) ApplyTheme(theme);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Notify("The saved appearance could not be loaded. Using your Windows setting.", true); }
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            Startup.IsOn = run?.GetValue("ChudPresence Link") is string;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { Notify("Windows startup preferences could not be read. " + error.Message, true); }
        startupReady = true;
        UpdateTitleBar();
        RefreshView();
        timer.Start();
    }

    private void UpdateAdaptiveLayout()
    {
        var wide = Root.ActualWidth >= 1050;
        if (wideLayout == wide) return;
        wideLayout = wide;
        PageContent.Padding = wide ? new Thickness(32, 20, 32, 28) : new Thickness(24, 16, 24, 28);
        ServicesPanel.MaxWidth = wide ? 856 : double.PositiveInfinity;
        Grid.SetRow(SidePanel, wide ? 0 : 1);
        Grid.SetColumn(SidePanel, wide ? 1 : 0);
        SidePanel.Margin = wide ? new Thickness(24, 0, 0, 0) : new Thickness(0, 24, 0, 0);
        SideColumn.Width = new GridLength(wide ? 344 : 0);
        Grid.SetRow(PhoneEndpoint, wide ? 0 : 1);
        Grid.SetColumn(PhoneEndpoint, wide ? 1 : 0);
        PhoneEndpoint.Margin = wide ? new Thickness(24, 0, 0, 0) : new Thickness(0, 16, 0, 0);
        EndpointColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    public void OpenWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        Activate();
    }

    public void Exit() => ((App)Application.Current).RequestExit();

    public void CreateCode(string kind)
    {
        try
        {
            store.CreatePairingCode(kind);
            RefreshView();
            Notify(kind == "mobile" ? "Phone code created. Enter it in your phone's Link settings." : "Browser code created. Enter it in the extension's Link settings.");
        }
        catch (Exception error) when (error is LinkException or IOException or UnauthorizedAccessException)
        { Notify("Could not create a pairing code. " + error.Message, true); }
    }

    private void RefreshView()
    {
        // A dialog owns a consistent inventory snapshot until it is saved or dismissed.
        try
        {
            var status = presence.Status;
            ConnectionStatus.Text = status.DeviceId is null ? "Link is running" : status.Published ? "Publishing to Discord" : "Browser linked";
            PresenceDetails.Text = status.Details ?? status.Message;
            PresenceDetails.Visibility = status.DeviceId is null ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetToolTip(PresenceDetails, PresenceDetails.Text);
            StatusBadge.Text = status.Published ? "Publishing" : "Local network";
            if (DateTimeOffset.UtcNow >= nextAddressRefresh)
            {
                RefreshAddresses();
                nextAddressRefresh = DateTimeOffset.UtcNow.AddSeconds(10);
            }
            RefreshPairing();
            var discovery = store.Discover();
            var snapshot = store.Settings();
            // A network update can occur between these reads. Retry next tick instead
            // of marking an older discovery snapshot as the latest displayed revision.
            if (discovery.Revision == snapshot.Revision && snapshot.Revision != displayedRevision && !configuringSync && !serviceCards.Any(c => c.IsInteracting))
            {
                var focused = FocusedSetting();
                var expanded = serviceCards.Where(card => card.IsExpanded).Select(card => card.ServiceId).ToHashSet();
                ServiceList.Children.Clear();
                serviceCards.Clear();
                var shared = discovery.Services.Where(s => s.Shared).ToArray();
                ServiceCount.Text = $"{shared.Length} {(shared.Length == 1 ? "service" : "services")}";
                EmptyServices.Visibility = shared.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                EmptyServicesHelp.Text = discovery.Services.Length == 0
                    ? "Pair your browser and phone. Services installed on both will appear here."
                    : $"Your devices have reported {discovery.Services.Length} {(discovery.Services.Length == 1 ? "service" : "services")}. Matches appear when both sides report them.";
                foreach (var service in shared)
                {
                    snapshot.Services.TryGetValue(service.Id, out var preference);
                    var card = new ServiceCard(service, preference, store, ConfigureSyncAsync, Notify, QueueRefresh)
                    { IsExpanded = expanded.Contains(service.Id) };
                    serviceCards.Add(card);
                    ServiceList.Children.Add(card);
                }
                displayedRevision = snapshot.Revision;
                if (focused is not null)
                    DispatcherQueue.TryEnqueue(() => FindSetting(ServiceList, focused)?.Focus(FocusState.Programmatic));
            }
            var list = store.Devices().Select(d => new DeviceItem(d.Id, d.Name, d.Kind,
                discovery.Services.Count(s => s.Participants.Any(p => p.DeviceId == d.Id)))).ToArray();
            if (!list.SequenceEqual(knownDevices))
            {
                var selectedId = (Devices.SelectedItem as DeviceItem)?.Id;
                Devices.ItemsSource = list;
                Devices.SelectedItem = list.FirstOrDefault(d => d.Id == selectedId);
                knownDevices = list;
            }
            DeviceCount.Text = list.Length.ToString();
            EmptyDevices.Visibility = list.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Devices.Visibility = list.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            RemoveDevice.Visibility = list.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            RemoveDevice.IsEnabled = Devices.SelectedItem is DeviceItem;
        }
        catch (Exception error) when (error is LinkException or IOException or UnauthorizedAccessException or System.Net.NetworkInformation.NetworkInformationException)
        { Notify("Could not refresh Link. " + error.Message, true); }
    }

    private void RefreshAddresses()
    {
        var urls = LocalNetwork.Addresses().Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => $"http://{a}:{port}").ToArray();
        if (urls.SequenceEqual(knownAddresses) && PhoneAddress.Items.Count > 0) return;
        var selected = PhoneAddress.SelectedItem as string;
        PhoneAddress.ItemsSource = urls.Length == 0 ? ["No local network"] : urls;
        PhoneAddress.SelectedItem = urls.Contains(selected) ? selected : urls.FirstOrDefault() ?? "No local network";
        PhoneAddress.IsEnabled = urls.Length > 0;
        knownAddresses = urls;
    }

    private void RefreshPairing()
    {
        var active = store.CurrentCode;
        var isActive = active is not null;
        if (pairingActive != isActive)
        {
            pairingActive = isActive;
            PairingCode.FontFamily = new(isActive ? "Cascadia Mono, Consolas" : "Segoe UI Variable Text");
            PairingCode.FontSize = isActive ? 27 : 16;
            PairingCode.FontWeight = isActive ? global::Microsoft.UI.Text.FontWeights.SemiBold : global::Microsoft.UI.Text.FontWeights.Normal;
        }
        PairingCode.Text = active is null ? "No active code" : active.Code[..4] + " " + active.Code[4..];
        PairingLabel.Text = active is null ? "PAIRING CODE" : active.Kind == "mobile" ? "PHONE PAIRING CODE" : "BROWSER PAIRING CODE";
        CopyCode.Visibility = CancelCode.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        var remaining = Math.Max(0, (int)((active?.ExpiresAt ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalSeconds);
        CodeHelp.Text = active is null ? "Codes expire after 5 minutes." : $"One-time code · {remaining / 60}:{remaining % 60:00} left";
    }

    private async Task ConfigureSyncAsync(SharedService service, bool disable)
    {
        if (configuringSync) return;
        if (!disable && service.SupportedFields.Length == 0)
        { Notify("These clients have no settings in common.", true); return; }
        configuringSync = true;
        try
        {
            ServiceSyncRequest? request;
            if (disable) request = new(false, service.SyncVersion);
            else
            {
                store.Settings().Services.TryGetValue(service.Id, out var preference);
                var dialog = new SyncSetupDialog(service, preference) { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme };
                request = await dialog.ShowAsync() == ContentDialogResult.Primary ? dialog.Selection : null;
            }
            if (request is not null)
            {
                store.SetServiceSync(service.Id, request);
                Notify(request.Enabled ? "Sync selection saved. Your devices can apply the selected settings." : "Sync is off. Each device keeps its local settings.");
            }
        }
        catch (Exception error) when (error is LinkException or IOException or UnauthorizedAccessException)
        { Notify("Could not save sync settings. " + error.Message, true); }
        finally { configuringSync = false; QueueRefresh(); }
    }

    private async void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (Devices.SelectedItem is not DeviceItem item) return;
        RemoveDevice.IsEnabled = false;
        try
        {
            store.Revoke(item.Id);
            await presence.RevokeAsync(item.Id, CancellationToken.None);
            Notify(item.Name + " removed. Its pairing token has been revoked.");
            QueueRefresh();
        }
        catch (Exception error) when (error is LinkException or IOException or UnauthorizedAccessException)
        { Notify("Could not remove device. " + error.Message, true); }
        finally { RemoveDevice.IsEnabled = Devices.SelectedItem is DeviceItem; }
    }

    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (!startupReady) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (Startup.IsOn) key.SetValue("ChudPresence Link", new LaunchOptions(dataDirectory, port, true).StartupCommand(
                Environment.ProcessPath ?? throw new IOException("The application executable path is unavailable.")));
            else key.DeleteValue("ChudPresence Link", false);
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            startupReady = false;
            Startup.IsOn = !Startup.IsOn;
            startupReady = true;
            Notify("Could not change Windows startup. " + error.Message, true);
        }
    }

    private void Copy(string value, string message)
    {
        try
        {
            var content = new DataPackage();
            content.SetText(value);
            Clipboard.SetContent(content);
            Clipboard.Flush();
            Notify(message);
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        { Notify("The clipboard is busy. Try copying again.", true); }
    }

    private void Notify(string message, bool error = false)
    {
        feedbackTimer.Stop();
        Feedback.Severity = error ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        Feedback.Message = message;
        Feedback.IsOpen = true;
        if (!error) feedbackTimer.Start();
    }

    private void QueueRefresh()
    {
        displayedRevision = -1;
        DispatcherQueue.TryEnqueue(RefreshView);
    }

    private string? FocusedSetting()
    {
        if (Root.XamlRoot is null) return null;
        var element = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        while (element is not null)
        {
            if (element is FrameworkElement { Tag: string tag }) return tag;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static Control? FindSetting(DependencyObject root, string tag)
    {
        if (root is Control { Tag: string value } control && value == tag) return control;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindSetting(VisualTreeHelper.GetChild(root, i), tag) is { } found) return found;
        return null;
    }

    private string ThemePath => Path.Combine(dataDirectory, "appearance.txt");
    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string value } || !Enum.TryParse<ElementTheme>(value, out var theme)) return;
        ApplyTheme(theme);
        try { Directory.CreateDirectory(dataDirectory); File.WriteAllText(ThemePath, value); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Notify("Appearance changed, but could not be saved for next time. " + error.Message, true); }
    }

    private void ApplyTheme(ElementTheme theme)
    {
        Root.RequestedTheme = theme;
        ThemeSystem.IsChecked = theme == ElementTheme.Default;
        ThemeLight.IsChecked = theme == ElementTheme.Light;
        ThemeDark.IsChecked = theme == ElementTheme.Dark;
        UpdateTitleBar();
    }

    private void UpdateTitleBar()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = dark ? Colors.Gray : Colors.DarkGray;
    }

    private void PairBrowser_Click(object sender, RoutedEventArgs e) => CreateCode("browser");
    private void PairPhone_Click(object sender, RoutedEventArgs e) => CreateCode("mobile");
    private void CopyBrowser_Click(object sender, RoutedEventArgs e) => Copy(BrowserAddress.Text, "Browser connection address copied.");
    private void CopyPhone_Click(object sender, RoutedEventArgs e)
    {
        if (PhoneAddress.SelectedItem is string address && address.StartsWith("http://", StringComparison.Ordinal)) Copy(address, "Phone connection address copied.");
        else Notify("Connect to Wi-Fi or Ethernet to get a phone address.", true);
    }
    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (store.CurrentCode is { } code) Copy(code.Code, "Pairing code copied.");
        else { RefreshPairing(); Notify("This pairing code has expired. Create a new one.", true); }
    }
    private void CancelCode_Click(object sender, RoutedEventArgs e) { store.CancelPairing(); RefreshPairing(); }
    private void Devices_SelectionChanged(object sender, SelectionChangedEventArgs e) => RemoveDevice.IsEnabled = Devices.SelectedItem is DeviceItem;
    private void Exit_Click(object sender, RoutedEventArgs e) => Exit();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    public sealed record DeviceItem(string Id, string Name, string Kind, int Services)
    {
        public string Glyph => Kind == "mobile" ? "\uE8EA" : "\uE774";
        public string Description => $"{(Kind == "mobile" ? "Phone" : "Browser extension")} · {Services} {(Services == 1 ? "service" : "services")}";
    }
}
