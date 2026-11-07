using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using WF = System.Windows.Forms;

namespace Island
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        IntPtr hwnd;
        bool expanded;
        DateTime? leaveTime;
        int tick;
        string lastArtKey = "";
        GlobalSystemMediaTransportControlsSessionManager? mgr;
        WF.NotifyIcon? tray;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;
            SetClickThrough(true);

            tray = new WF.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "Dynamic Island",
                Visible = true,
                ContextMenuStrip = new WF.ContextMenuStrip()
            };
            tray.ContextMenuStrip.Items.Add("Open Dynamic Island", null, (s, a) => { SetExpanded(true); });
            tray.ContextMenuStrip.Items.Add("Quit", null, (s, a) => { tray!.Visible = false; Application.Current.Shutdown(); });

            UpdateClock();
            var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clock.Tick += (s, a) =>
            {
                UpdateClock();
                if (expanded && ++tick % 2 == 0) _ = RefreshAll();
            };
            clock.Start();

            var mouse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
            mouse.Tick += (s, a) => CheckMouse();
            mouse.Start();

            try { mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); } catch { }
        }

        void SetClickThrough(bool on)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            ex = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
            SetWindowLong(hwnd, GWL_EXSTYLE, ex);
        }

        void UpdateClock()
        {
            var now = DateTime.Now;
            TimeText.Text = now.ToString("HH:mm");
            DateText.Text = now.ToString("dddd, d MMMM");
        }

        void CheckMouse()
        {
            if (!GetCursorPos(out var p)) return;
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double x = p.X / scale, y = p.Y / scale;
            double center = SystemParameters.PrimaryScreenWidth / 2;

            if (!expanded)
            {
                if (y <= 42 && Math.Abs(x - center) <= 125)
                {
                    Topmost = true;
                    SetExpanded(true);
                    leaveTime = null;
                }
            }
            else
            {
                bool inside = y <= 250 && Math.Abs(x - center) <= 285;
                if (inside) leaveTime = null;
                else
                {
                    leaveTime ??= DateTime.Now;
                    if ((DateTime.Now - leaveTime.Value).TotalMilliseconds > 500)
                    {
                        SetExpanded(false);
                        leaveTime = null;
                    }
                }
            }
        }

        void SetExpanded(bool on)
        {
            if (expanded == on) return;
            expanded = on;
            SetClickThrough(!on);

            var ease = new CubicEase { EasingMode = on ? EasingMode.EaseOut : EasingMode.EaseIn };
            var duration = TimeSpan.FromMilliseconds(on ? 420 : 300);

            Pill.BeginAnimation(WidthProperty, new DoubleAnimation(on ? 540 : 190, duration) { EasingFunction = ease });
            Pill.BeginAnimation(HeightProperty, new DoubleAnimation(on ? 225 : 30, duration) { EasingFunction = ease });
            Content.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 260 : 130))
            {
                BeginTime = TimeSpan.FromMilliseconds(on ? 125 : 0),
                EasingFunction = ease
            });
            CompactView.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 0 : 1, TimeSpan.FromMilliseconds(on ? 100 : 170)));
            if (on) _ = RefreshAll();
        }

        async Task RefreshAll()
        {
            await RefreshMedia();
            RefreshBattery();
            var ssid = await Task.Run(GetSsid);
            WifiText.Text = string.IsNullOrEmpty(ssid) ? "Not connected" : ssid;
        }

        async Task RefreshMedia()
        {
            try
            {
                var session = mgr?.GetCurrentSession();
                if (session == null)
                {
                    TitleText.Text = "Nothing playing";
                    ArtistText.Text = "Your music will appear here";
                    Art.Source = null;
                    lastArtKey = "";
                    PlayIcon.Text = "\uE768";
                    return;
                }

                var props = await session.TryGetMediaPropertiesAsync();
                TitleText.Text = string.IsNullOrWhiteSpace(props.Title) ? "Unknown track" : props.Title;
                ArtistText.Text = string.IsNullOrWhiteSpace(props.Artist) ? "Media session active" : props.Artist;
                bool playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                PlayIcon.Text = playing ? "\uE769" : "\uE768";

                string key = props.Title + "|" + props.Artist;
                if (key != lastArtKey)
                {
                    lastArtKey = key;
                    Art.Source = null;
                    if (props.Thumbnail != null)
                    {
                        using var stream = await props.Thumbnail.OpenReadAsync();
                        var memory = new MemoryStream();
                        await stream.AsStreamForRead().CopyToAsync(memory);
                        memory.Position = 0;
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = memory;
                        bitmap.EndInit();
                        Art.Source = bitmap;
                    }
                }
            }
            catch { }
        }

        void RefreshBattery()
        {
            if (GetSystemPowerStatus(out var status) && status.BatteryLifePercent <= 100 && status.BatteryFlag != 128)
            {
                BatChip.Visibility = Visibility.Visible;
                BatText.Text = status.BatteryLifePercent + "%";
                BatIcon.Text = status.ACLineStatus == 1 ? "\uE83E" : "\uE83F";
            }
            else BatChip.Visibility = Visibility.Collapsed;
        }

        static string GetSsid()
        {
            try
            {
                var start = new ProcessStartInfo("netsh", "wlan show interfaces")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(start)!;
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                foreach (var line in output.Split('\n'))
                {
                    var value = line.Trim();
                    if (value.StartsWith("SSID") && !value.StartsWith("SSID name") && value.Contains(':'))
                        return value.Substring(value.IndexOf(':') + 1).Trim();
                }
            }
            catch { }
            return "";
        }

        static void OpenSettings(string uri) =>
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

        async void Prev_Click(object sender, RoutedEventArgs e)
        {
            try { await mgr!.GetCurrentSession().TrySkipPreviousAsync(); } catch { }
            await Task.Delay(250);
            await RefreshMedia();
        }

        async void Play_Click(object sender, RoutedEventArgs e)
        {
            try { await mgr!.GetCurrentSession().TryTogglePlayPauseAsync(); } catch { }
            await Task.Delay(200);
            await RefreshMedia();
        }

        async void Next_Click(object sender, RoutedEventArgs e)
        {
            try { await mgr!.GetCurrentSession().TrySkipNextAsync(); } catch { }
            await Task.Delay(250);
            await RefreshMedia();
        }

        void Wifi_Click(object sender, RoutedEventArgs e) => OpenSettings("ms-settings:network-wifi");
        void Bt_Click(object sender, RoutedEventArgs e) => OpenSettings("ms-settings:bluetooth");

        protected override void OnClosed(EventArgs e)
        {
            if (tray != null) tray.Visible = false;
            base.OnClosed(e);
        }
    }
}