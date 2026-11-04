using System;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace G5Control;

public partial class MainWindow : Window
{
    readonly PresetItem[] _items;
    bool _busy;

    public event Action<string?>? ActivePresetChanged;

    public MainWindow()
    {
        InitializeComponent();
        _items = Presets.All.Select(p => new PresetItem(p)).ToArray();
        PresetList.ItemsSource = _items;
        MarkActive(Presets.LoadActive());

        IpBox.Text = LgTv.SavedIp ?? "";
        Autostart.MigrateLegacy();
        AutostartBox.IsChecked = Autostart.IsEnabled;
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
        IpBox.Text = LgTv.SavedIp ?? "";
        var settings = await tv.GetPictureSettingsAsync();
        var mode = PictureModeName((string?)settings["pictureMode"]);
        var saving = (string?)settings["energySaving"] == "off" ? "aus" : (string?)settings["energySaving"];
        StatusText.Text = $"Helligkeit {settings["backlight"]} · Bildmodus {mode} · Energiesparen {saving}";

        // The TV keeps values per picture mode, so after an SDR/HDR switch (or a change with the remote)
        // the last applied preset may no longer be what is on screen.
        if (_items.FirstOrDefault(i => i.IsActive) is { } active && !active.Preset.Matches(settings))
        {
            MarkActive(null);
            StatusText.Text += $"\nPasst nicht mehr zu „{active.Name}“ – z. B. nach Wechsel zwischen SDR und HDR. Preset erneut wählen.";
        }
    }

    // HDR signals use their own modes ("hdrGame", "hdrStandard"); energy saving "max" switches to standard.
    static string? PictureModeName(string? mode)
    {
        if (mode is { Length: > 3 } && mode.StartsWith("hdr", StringComparison.Ordinal))
        {
            return PictureModeName(char.ToLowerInvariant(mode[3]) + mode[4..]) + " (HDR)";
        }
        return mode switch
        {
            "game" => "Spiel",
            "standard" => "Standard",
            "eco" => "Eco",
            var other => other,
        };
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
            LgTv.SavedIp is { } ip
                ? $"Fernseher unter {ip} nicht erreichbar und im Netzwerk nicht gefunden – ist der G5 an?"
                : "Kein Fernseher im Netzwerk gefunden – ist der G5 an? Die IP lässt sich unter „Verbindung“ eintragen.",
        _ => ex.Message,
    };

    async void SaveIp_Click(object sender, RoutedEventArgs e) => await SaveIpAsync();

    async void IpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SaveIpAsync();
        }
    }

    async Task SaveIpAsync()
    {
        var text = IpBox.Text.Trim();
        if (!IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            StatusText.Text = $"„{text}“ ist keine gültige IP-Adresse (z. B. 192.168.178.154).";
            return;
        }
        LgTv.SaveIp(ip.ToString());
        await RefreshStatusAsync();
    }

    async void SearchIp_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        SetBusy(true, "Suche Fernseher im Netzwerk …");
        string? ip;
        try
        {
            ip = await LgTv.SearchAsync();
        }
        finally
        {
            SetBusy(false, null);
        }
        if (ip == null)
        {
            StatusText.Text = "Kein Fernseher im Netzwerk gefunden – ist der G5 an?";
            return;
        }
        IpBox.Text = ip;
        await RefreshStatusAsync();
    }

    void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (AutostartBox.IsChecked == true)
        {
            Autostart.Enable();
        }
        else
        {
            Autostart.Disable();
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
