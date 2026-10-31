using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChudPresence.Link.Windows;

public sealed partial class ServiceCard : UserControl
{
    private readonly SharedService service;
    private readonly LinkStore store;
    private readonly Action<string, bool> notify;
    private readonly Action refresh;
    private readonly Func<SharedService, bool, Task> configure;
    private long version;
    private int openMenus;

    public bool IsInteracting => openMenus > 0;
    public string ServiceId => service.Id;
    public bool IsExpanded
    {
        get => ServiceDetails.IsExpanded;
        set => ServiceDetails.IsExpanded = value;
    }

    public ServiceCard(SharedService service, ServicePreference? preference, LinkStore store,
        Func<SharedService, bool, Task> configure, Action<string, bool> notify, Action refresh)
    {
        InitializeComponent();
        this.service = service;
        this.store = store;
        this.configure = configure;
        this.notify = notify;
        this.refresh = refresh;
        version = preference?.Version ?? 0;
        ServiceName.Text = service.Name;
        AutomationProperties.SetName(ServiceDetails, service.Name + " settings");
        ServiceStatus.Text = $"{service.Participants.Length} devices · {(service.SyncEnabled ? "Sync on" : service.SyncRequested ? "Sync paused" : "Sync off")}";
        ToolTipService.SetToolTip(ServiceName, string.Join("\n", service.Participants.Select(p =>
            p.Name + (p.Kind == "mobile" ? " · Phone" : " · Browser"))));
        Configure.Content = service.SyncRequested ? "Settings" : "Enable sync";
        AutomationProperties.SetName(Configure, (service.SyncRequested ? "Choose synced settings for " : "Enable sync for ") + service.Name);
        More.Visibility = service.SyncRequested ? Visibility.Visible : Visibility.Collapsed;
        Summary.Text = service.SyncEnabled
            ? service.SyncFields.Length == service.SupportedFields.Length ? "Syncing all supported settings" : $"Syncing {service.SyncFields.Length} of {service.SupportedFields.Length} settings"
            : service.SyncRequested ? "A selected setting is no longer supported by every device. Update the selection to resume."
            : "Choose all supported settings, or just the ones you want to share.";
        if (!service.SyncEnabled) return;

        foreach (var key in ServiceValues.AllFields)
        {
            var synced = service.SyncFields.Contains(key);
            var row = new Grid { MinHeight = 48, ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var title = new TextBlock { Text = SyncSetupDialog.FieldName(key), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            ToolTipService.SetToolTip(title, key == "playbackProgress"
                ? "Off uses Discord's green activity timer instead of media elapsed/remaining time."
                : SyncSetupDialog.FieldDescription(key));
            row.Children.Add(title);
            FrameworkElement control;
            if (!synced)
            {
                control = new TextBlock { Text = "Local to each device", Style = (Style)Application.Current.Resources["CaptionTextStyle"], VerticalAlignment = VerticalAlignment.Center };
            }
            else if (key == "display")
            {
                var combo = new ComboBox { MinWidth = 160, MaxWidth = 210, SelectedValuePath = "Tag", FontSize = 13 };
                foreach (var choice in service.DisplayChoices) combo.Items.Add(new ComboBoxItem { Content = SyncSetupDialog.DisplayName(choice), Tag = choice });
                combo.SelectedValue = preference?.Display;
                combo.SelectionChanged += (_, _) => { if (combo.SelectedValue is string value) Save(key, value); };
                combo.DropDownOpened += (_, _) => openMenus++;
                combo.DropDownClosed += (_, _) => openMenus = Math.Max(0, openMenus - 1);
                control = combo;
            }
            else
            {
                var toggle = new ToggleSwitch { IsOn = preference?.Value(key) is true, MinWidth = 100, OnContent = "On", OffContent = "Off" };
                toggle.Toggled += (_, _) => Save(key, toggle.IsOn);
                control = toggle;
            }
            AutomationProperties.SetName(control, service.Name + " " + SyncSetupDialog.FieldName(key) + (synced ? "" : ", local to each device"));
            control.Tag = service.Id + "/" + key;
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            Fields.Children.Add(row);
        }
    }

    private void Save(string key, object value)
    {
        try
        {
            var on = value is true;
            var change = new PreferenceChange(version, Display: key == "display" ? (string)value : null,
                Enabled: key == "enabled" ? on : null, ShowWhilePaused: key == "showWhilePaused" ? on : null,
                Artwork: key == "artwork" ? on : null, PlaybackProgress: key == "playbackProgress" ? on : null,
                ActionButtons: key == "actionButtons" ? on : null);
            var result = store.Update(new() { [service.Id] = change });
            var conflict = result.Conflicts.Length > 0;
            notify(conflict ? "Another device changed this service. The latest settings have been loaded." : "Saved. Paired clients receive this setting on sync.", conflict);
            if (result.Services.TryGetValue(service.Id, out var saved)) version = saved.Version;
        }
        catch (Exception error) when (error is LinkException or IOException or UnauthorizedAccessException)
        {
            notify(error.Message, true);
        }
        refresh();
    }

    private async void Configure_Click(object sender, RoutedEventArgs e) => await configure(service, false);
    private async void Disable_Click(object sender, RoutedEventArgs e) => await configure(service, true);
}
