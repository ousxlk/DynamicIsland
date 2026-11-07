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
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Diagnostics;

namespace Island
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] struct FILETIME { public uint Low; public uint High; public ulong Value => ((ulong)High << 32) | Low; }
        [StructLayout(LayoutKind.Sequential)] struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

        const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        IntPtr hwnd;
        bool expanded, changingVolume;
        DateTime? leaveTime;
        int tick;
        string lastArtKey = "";
        ulong lastIdle, lastKernel, lastUser;
        GlobalSystemMediaTransportControlsSessionManager? mgr;
        WF.NotifyIcon? tray;
        MMDeviceEnumerator? audioEnumerator;
        MMDevice? audioDevice;

        public MainWindow() { InitializeComponent(); Loaded += OnLoaded; }

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;
            SetClickThrough(true);
            tray = new WF.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Dynamic Island", Visible = true, ContextMenuStrip = new WF.ContextMenuStrip() };
            tray.ContextMenuStrip.Items.Add("Show Dynamic Island", null, (s, a) => SetExpanded(true));
            tray.ContextMenuStrip.Items.Add("Quit", null, (s, a) => { tray!.Visible = false; Application.Current.Shutdown(); });

            try { audioEnumerator = new MMDeviceEnumerator(); audioDevice = audioEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { }
            try { mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); } catch { }

            UpdateClock();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async (s, a) =>
            {
                UpdateClock();
                if (expanded && ++tick % 2 == 0) await RefreshAll();
            };
            timer.Start();

            var mouse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            mouse.Tick += (s, a) => CheckMouse();
            mouse.Start();
            RefreshCpu();
        }

        void SetClickThrough(bool on)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLong(hwnd, GWL_EXSTYLE, on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
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
            double x = p.X / scale, y = p.Y / scale, center = SystemParameters.PrimaryScreenWidth / 2;
            if (!expanded)
            {
                if (y <= 34 && Math.Abs(x - center) <= 105) { SetExpanded(true); leaveTime = null; }
            }
            else
            {
                bool inside = y <= 345 && Math.Abs(x - center) <= 320;
                if (inside) leaveTime = null;
                else { leaveTime ??= DateTime.Now; if ((DateTime.Now - leaveTime.Value).TotalMilliseconds > 650) { SetExpanded(false); leaveTime = null; } }
            }
        }

        void SetExpanded(bool on)
        {
            if (expanded == on) return;
            expanded = on;
            SetClickThrough(!on);
            var ease = new CubicEase { EasingMode = on ? EasingMode.EaseOut : EasingMode.EaseIn };
            Pill.BeginAnimation(WidthProperty, new DoubleAnimation(on ? 600 : 154, TimeSpan.FromMilliseconds(on ? 380 : 260)) { EasingFunction = ease });
            Pill.BeginAnimation(HeightProperty, new DoubleAnimation(on ? 315 : 25, TimeSpan.FromMilliseconds(on ? 380 : 260)) { EasingFunction = ease });
            Panel.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 230 : 100)) { BeginTime = TimeSpan.FromMilliseconds(on ? 120 : 0), EasingFunction = ease });
            CompactView.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 0 : 1, TimeSpan.FromMilliseconds(on ? 90 : 150)));
            if (on) _ = RefreshAll();
        }

        async Task RefreshAll()
        {
            await RefreshMedia();
            RefreshBattery();
            RefreshCpu();
            RefreshGpu();
            RefreshVolume();
            string ssid = await Task.Run(GetSsid);
            WifiText.Text = string.IsNullOrWhiteSpace(ssid) ? "Wi-Fi off" : ssid;
        }

        void RefreshCpu()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return;
            ulong di = idle.Value - lastIdle, dk = kernel.Value - lastKernel, du = user.Value - lastUser;
            lastIdle = idle.Value; lastKernel = kernel.Value; lastUser = user.Value;
            ulong total = dk + du;
            if (total == 0) return;
            double usage = Math.Clamp((double)(total - Math.Min(di, total)) * 100.0 / total, 0, 100);
            CpuText.Text = $"{usage:0}%";
            CpuBar.Value = usage;
        }

        void RefreshGpu()
        {
            try
            {
                double total = 0;
                var category = new PerformanceCounterCategory("GPU Engine");
                foreach (string instance in category.GetInstanceNames())
                {
                    if (!instance.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
                    using var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                    total += Math.Max(0, counter.NextValue());
                }
                double usage = Math.Clamp(total, 0, 100);
                GpuText.Text = $"{usage:0}%";
                GpuBar.Value = usage;
            }
            catch { GpuText.Text = "N/A"; GpuBar.Value = 0; }
        }

        void RefreshVolume()
        {
            try
            {
                if (audioDevice == null) return;
                changingVolume = true;
                double value = audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100;
                VolumeSlider.Value = value;
                VolumeText.Text = $"{value:0}%";
                changingVolume = false;
            }
            catch { changingVolume = false; }
        }

        void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (changingVolume) return;
            try
            {
                if (audioDevice != null) audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(VolumeSlider.Value / 100.0);
                VolumeText.Text = $"{VolumeSlider.Value:0}%";
            }
            catch { }
        }

        async Task RefreshMedia()
        {
            try
            {
                var session = mgr?.GetCurrentSession();
                if (session == null)
                {
                    TitleText.Text = "Nothing playing"; ArtistText.Text = "Play something to see media controls";
                    Art.Source = null; lastArtKey = ""; PlayIcon.Text = "\uE768"; return;
                }
                var props = await session.TryGetMediaPropertiesAsync();
                TitleText.Text = string.IsNullOrWhiteSpace(props.Title) ? "Unknown track" : props.Title;
                ArtistText.Text = string.IsNullOrWhiteSpace(props.Artist) ? "Media session active" : props.Artist;
                bool playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                PlayIcon.Text = playing ? "\uE769" : "\uE768";
                string key = props.Title + "|" + props.Artist;
                if (key != lastArtKey)
                {
                    lastArtKey = key; Art.Source = null;
                    if (props.Thumbnail != null)
                    {
                        using var input = await props.Thumbnail.OpenReadAsync();
                        var memory = new MemoryStream();
                        await input.AsStreamForRead().CopyToAsync(memory);
                        memory.Position = 0;
                        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = memory; bitmap.EndInit(); Art.Source = bitmap;
                    }
                }
            }
            catch { }
        }

        void RefreshBattery()
        {
            if (GetSystemPowerStatus(out var status) && status.BatteryLifePercent <= 100 && status.BatteryFlag != 128)
                BatText.Text = status.BatteryLifePercent + "%";
            else BatText.Text = "AC";
        }

        static string GetSsid()
        {
            try
            {
                var info = new ProcessStartInfo("netsh", "wlan show interfaces") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(info)!;
                string output = p.StandardOutput.ReadToEnd(); p.WaitForExit();
                foreach (var line in output.Split('\n'))
                {
                    string value = line.Trim();
                    if (value.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("SSID name", StringComparison.OrdinalIgnoreCase) && value.Contains(':'))
                        return value[(value.IndexOf(':') + 1)..].Trim();
                }
            }
            catch { }
            return "";
        }

        static void OpenSettings(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        async void Prev_Click(object sender, RoutedEventArgs e) { try { var s = mgr?.GetCurrentSession(); if (s != null) await s.TrySkipPreviousAsync(); } catch { } await RefreshMedia(); }
        async void Play_Click(object sender, RoutedEventArgs e) { try { var s = mgr?.GetCurrentSession(); if (s != null) await s.TryTogglePlayPauseAsync(); } catch { } await RefreshMedia(); }
        async void Next_Click(object sender, RoutedEventArgs e) { try { var s = mgr?.GetCurrentSession(); if (s != null) await s.TrySkipNextAsync(); } catch { } await RefreshMedia(); }
        void Wifi_Click(object sender, RoutedEventArgs e) => OpenSettings("ms-settings:network-wifi");
        void Bt_Click(object sender, RoutedEventArgs e) => OpenSettings("ms-settings:bluetooth");
        void Sound_Click(object sender, RoutedEventArgs e) => OpenSettings("ms-settings:sound");

        protected override void OnClosed(EventArgs e)
        {
            if (tray != null) tray.Visible = false;
            audioDevice?.Dispose(); audioEnumerator?.Dispose();
            base.OnClosed(e);
        }
    }
}