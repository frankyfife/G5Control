using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace G5Control;

public partial class MainWindow : Window
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Explorer skips Run entries that have no "enabled" record here (the same flag Task Manager toggles).
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string RunValue = "G5Control";
    static readonly byte[] ApprovedEnabled = [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    readonly PresetItem[] _items;
    bool _busy;

    public event Action<string?>? ActivePresetChanged;

    public MainWindow()
    {
        InitializeComponent();
        _items = Presets.All.Select(p => new PresetItem(p)).ToArray();
        PresetList.ItemsSource = _items;
        MarkActive(Presets.LoadActive());

        AutostartBox.IsChecked = IsAutostartEnabled();
    }

    static bool IsAutostartEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        // An odd first byte means the entry was disabled, e.g. in Task Manager.
        return run?.GetValue(RunValue) != null && approved?.GetValue(RunValue) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 0;
    }

    async void Preset_Click(object sender, RoutedEventArgs e)
    {
        await ApplyAsync(((PresetItem)((Button)sender).Tag).Preset);
    }

    public async Task ApplyAsync(Preset preset)
    {
        if (_busy)
        {
            return;
        }
        SetBusy(true, $"{preset.Name} wird angewendet …");
        try
        {
            await preset.ApplyAsync();
            Presets.SaveActive(preset.Name);
            MarkActive(preset.Name);
            await ReadStatusAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Fehler: {Describe(ex)}";
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    public async Task RefreshStatusAsync()
    {
        if (_busy)
        {
            return;
        }
        SetBusy(true, "Verbinde …");
        try
        {
            await ReadStatusAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = Describe(ex);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    async Task ReadStatusAsync()
    {
        await using var tv = await LgTv.ConnectAsync();
        var settings = await tv.GetPictureSettingsAsync();
        var mode = (string?)settings["pictureMode"] switch
        {
            "game" => "Spiel",
            "eco" => "Eco",
            var other => other,
        };
        var saving = (string?)settings["energySaving"] == "off" ? "aus" : (string?)settings["energySaving"];
        StatusText.Text = $"Helligkeit {settings["backlight"]} · Bildmodus {mode} · Energiesparen {saving}";
    }

    void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        PresetList.IsEnabled = !busy;
        if (status != null)
        {
            StatusText.Text = status;
        }
    }

    void MarkActive(string? name)
    {
        foreach (var item in _items)
        {
            item.IsActive = item.Name == name;
        }
        ActivePresetChanged?.Invoke(name);
    }

    static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException or System.Net.WebSockets.WebSocketException =>
            "Fernseher nicht erreichbar – ist der G5 an?",
        _ => ex.Message,
    };

    void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
        if (AutostartBox.IsChecked == true)
        {
            run.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
            approved.SetValue(RunValue, ApprovedEnabled, RegistryValueKind.Binary);
        }
        else
        {
            run.DeleteValue(RunValue, throwOnMissingValue: false);
            approved.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The window only hides; the app keeps running in the tray until "Beenden".
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}

public sealed class PresetItem(Preset preset) : INotifyPropertyChanged
{
    bool _isActive;

    public Preset Preset { get; } = preset;
    public string Name => Preset.Name;
    public string Description => Preset.Description;
    public string Glyph => Preset.Glyph;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
