using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChudPresence.Link.Windows;

public sealed partial class SyncSetupDialog : ContentDialog
{
    private readonly SyncSelection selection;
    private readonly Dictionary<string, CheckBox> fields = new();
    private bool updating;

    public SyncSetupDialog(SharedService service, ServicePreference? preference)
    {
        selection = new(service, preference);
        InitializeComponent();
        Title = "Sync " + service.Name;
        PrimaryButtonText = service.SyncRequested ? "Save selection" : "Enable sync";
        foreach (var key in ServiceValues.AllFields)
        {
            var supported = service.SupportedFields.Contains(key);
            var labels = new StackPanel { Spacing = 2 };
            labels.Children.Add(new TextBlock { Text = FieldName(key), FontSize = 14 });
            labels.Children.Add(new TextBlock
            {
                Text = supported ? FieldDescription(key) : "Not supported by every device",
                Style = (Style)Application.Current.Resources["CaptionTextStyle"],
                TextWrapping = TextWrapping.Wrap
            });
            var check = new CheckBox
            {
                Name = "sync_" + key,
                Content = labels,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 7, 0, 7),
                IsChecked = selection.SelectedFields.Contains(key)
            };
            AutomationProperties.SetName(check, "Sync " + FieldName(key));
            ToolTipService.SetToolTip(check, supported ? FieldDescription(key) : "Every participating device must support this setting.");
            fields.Add(key, check);
            check.Checked += (_, _) => ChangeField(key, true);
            check.Unchecked += (_, _) => ChangeField(key, false);
            FieldRows.Children.Add(check);
        }
        updating = true;
        SourceSelector.ItemsSource = selection.Sources;
        SourceSelector.DisplayMemberPath = nameof(SyncSource.Title);
        SourceSelector.SelectedIndex = selection.SourceIndex;
        AllSettings.IsChecked = selection.AllSettings;
        SelectedSettings.IsChecked = !selection.AllSettings;
        updating = false;
        RefreshSelection();
    }

    public ServiceSyncRequest? Selection => selection.Build();

    private void ChangeField(string key, bool value)
    {
        if (updating) return;
        selection.SetField(key, value);
        RefreshSelection();
    }

    private void ScopeChanged(object sender, RoutedEventArgs e)
    {
        if (fields.Count == 0 || updating) return;
        selection.SetAllSettings(AllSettings.IsChecked == true);
        RefreshSelection();
    }

    private void SourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (fields.Count == 0 || updating) return;
        selection.SourceIndex = SourceSelector.SelectedIndex;
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        updating = true;
        try
        {
            foreach (var (key, check) in fields)
            {
                check.IsChecked = selection.SelectedFields.Contains(key);
                check.IsEnabled = !selection.AllSettings && selection.Service.SupportedFields.Contains(key);
            }
            IsPrimaryButtonEnabled = Selection is not null;
            SelectionHelp.Text = IsPrimaryButtonEnabled
                ? $"Shares these settings with {selection.Service.Participants.Length} devices. Unselected settings stay local."
                : "Choose at least one setting and a source that provides all its starting values.";
            if (selection.SourceIndex >= 0 && selection.SourceIndex < selection.Sources.Count)
            {
                var source = selection.Sources[selection.SourceIndex];
                SourceSummary.Text = string.Join(" · ", ServiceValues.AllFields
                    .Where(key => selection.SelectedFields.Contains(key))
                    .Select(key => FieldName(key) + ": " + ValueName(source.Values.Value(key))));
                SourceReported.Text = source.ReportedAt is { } reported
                    ? "Last reported " + reported.ToLocalTime().ToString("g")
                    : source.DeviceId is null && source.Values.Fields.Length > 1
                        ? "Uses the values currently shared by Link."
                        : "Uses these starting values for the selected settings.";
            }
            else
            {
                SourceSummary.Text = "No starting source available.";
                SourceReported.Text = "Pair a browser and phone, then let them report their installed services.";
            }
        }
        finally { updating = false; }
    }

    private void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = Selection is null;
    }

    private static string ValueName(object? value) => value switch
    {
        bool enabled => enabled ? "On" : "Off",
        string display => DisplayName(display),
        _ => "Not reported"
    };

    public static string DisplayName(string value) => SyncSelection.DisplayName(value);
    public static string FieldName(string key) => key switch
    {
        "display" => "Discord bar", "enabled" => "Service enabled", "showWhilePaused" => "Show while paused",
        "artwork" => "Artwork", "playbackProgress" => "Playback progress", "actionButtons" => "Action buttons", _ => key
    };
    public static string FieldDescription(string key) => key switch
    {
        "display" => "App, creator, or media title", "enabled" => "Enable or disable this service",
        "showWhilePaused" => "Keep activity visible when paused", "artwork" => "Cover art or video thumbnail",
        "playbackProgress" => "Elapsed and remaining media time", "actionButtons" => "Watch, play, or view channel", _ => key
    };
}
