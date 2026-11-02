using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace G5Control;

public partial class App : System.Windows.Application
{
    Mutex? _singleInstance;
    EventWaitHandle? _showSignal;
    Forms.NotifyIcon? _tray;
    MainWindow? _window;

    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeMode = ThemeMode.System;

        // A second start just brings the running instance's window to the front.
        _singleInstance = new Mutex(true, "G5Control.SingleInstance", out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "G5Control.Show");
        if (!isFirst)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        new Thread(() =>
        {
            while (_showSignal.WaitOne())
            {
                Dispatcher.Invoke(ShowWindow);
            }
        }) { IsBackground = true }.Start();

        _window = new MainWindow();
        _window.ActivePresetChanged += name => _tray!.Text = name == null ? "LG G5" : $"LG G5 – {name}";
        CreateTray();

        if (!e.Args.Contains("--tray"))
        {
            ShowWindow();
        }
    }

    void CreateTray()
    {
        try
        {
            Forms.Application.SetColorMode(Forms.SystemColorMode.System);
        }
        catch (InvalidOperationException)
        {
            // Dark context menu is cosmetic only.
        }

        var menu = new Forms.ContextMenuStrip();
        foreach (var preset in Presets.All)
        {
            menu.Items.Add(preset.Name, null, async (_, _) => await _window!.ApplyAsync(preset));
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Öffnen", null, (_, _) => ShowWindow());
        menu.Items.Add("Beenden", null, (_, _) => Quit());

        // Pick the frame matching the tray size at the current DPI, so the icon stays sharp.
        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream;
        var icon = new Icon(iconStream, Forms.SystemInformation.SmallIconSize);

        _tray = new Forms.NotifyIcon { Icon = icon, Text = "LG G5", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                if (_window!.IsVisible && _window.IsActive)
                {
                    _window.Hide();
                }
                else
                {
                    ShowWindow();
                }
            }
        };
    }

    void ShowWindow()
    {
        var window = _window!;
        window.Show();
        window.UpdateLayout();
        // Sit above the tray like a flyout.
        var workArea = SystemParameters.WorkArea;
        window.Left = workArea.Right - window.ActualWidth - 12;
        window.Top = workArea.Bottom - window.ActualHeight - 12;
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Activate();
        _ = window.RefreshStatusAsync();
    }

    void Quit()
    {
        IsExiting = true;
        _tray!.Visible = false;
        _tray.Dispose();
        _window?.Close();
        Shutdown();
    }
}
