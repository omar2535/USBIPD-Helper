using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using USBIPD_Helper.Helpers;
using Windows.Graphics;
using WinRT.Interop;
using Microsoft.UI.Windowing;

// ReSharper disable once CheckNamespace   (keeps XAML namespace simple)
namespace USBIPD_Helper
{
    public sealed partial class MainWindow : Window
    {
        private bool _initialised;                         // makes sure we run only once
        private bool _refreshInProgress;

        public MainWindow()
        {
            InitializeComponent();
            Activated += MainWindow_Activated;             // ✅  instead of “Loaded”
        }

        private async void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
        {
            if (_initialised) return;                      // first activation only
            _initialised = true;

            // ── Safe to grab AppWindow here ─────────────────────────────
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId winId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow? app = AppWindow.GetFromWindowId(winId);

            if (app is not null)
                app.Resize(new SizeInt32(1200, 600));    // width, height

            // Show warning if not running as admin
            AdminWarning.IsOpen = !Utils.IsRunningAsAdministrator();

            Debug.WriteLine("[MainWindow] Window activated – loading usbipd list…");
            await RefreshDeviceListAsync();
        }

        // --------------------------------------------------------------------
        private async Task RefreshDeviceListAsync()
        {
            if (_refreshInProgress) return;             // simple re-entrancy guard
            _refreshInProgress = true;

            try
            {
                var devices = await UsbipdHelper.GetDevicesAsync();

                // Optional: preserve scroll position or selection here
                DevicesListView.ItemsSource = devices;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] Refresh failed: {ex}");
            }
            finally
            {
                _refreshInProgress = false;
            }
        }

        // Refresh button
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDeviceListAsync();
        }

        // Bind / Unbind
        private async void BindButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: UsbDeviceInfo dev }) return;

            try
            {
                if (dev.IsBound)
                {
                    await UsbipdHelper.UnbindAsync(dev.BusId);
                }
                else
                {
                    await UsbipdHelper.BindAsync(dev.BusId);
                }
                await Task.Delay(1000);
                await RefreshDeviceListAsync();
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(dev.BusId, ex);
            }
        }

        // Attach / Detach
        private async void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: UsbDeviceInfo dev }) return;

            try
            {
                if (dev.IsAttached)
                {
                    await UsbipdHelper.DetachAsync(dev.BusId);
                }
                else
                {
                    // Auto-bind if needed
                    if (!dev.IsBound)
                        await UsbipdHelper.BindAsync(dev.BusId);

                    await UsbipdHelper.AttachAsync(dev.BusId);
                }
                await Task.Delay(1000);
                await RefreshDeviceListAsync();
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(dev.BusId, ex);
            }
        }

        // helper for errors
        private async Task ShowErrorAsync(string busId, Exception ex)
        {
            var dlg = new ContentDialog
            {
                Title = $"usbipd error ({busId})",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };

            await dlg.ShowAsync();          // awaits the IAsyncOperation
        }
    }

    // ─── Model ─────────────────────────────────────────────────────────────
    public partial class UsbDeviceInfo : INotifyPropertyChanged
    {
        // ── incoming raw fields ────────────────────────────────────────────
        private string _state = "Not shared";
        public required string BusId { get; init; }
        public required string Vid { get; init; }
        public required string Pid { get; init; }
        public required string Description { get; init; }

        // ── computed flags ────────────────────────────────────────────────
        public bool IsAttached => _state.ToLower() == "attached";
        public bool IsBound => IsAttached || _state.ToLower() == "shared";


        // ── UI helpers ────────────────────────────────────────────────────
        public string BindActionText => IsBound ? "Unbind" : "Bind";
        public string AttachActionText => IsAttached ? "Detach" : "Attach";

        public bool BindEnabled => !IsAttached;          // Can't unbind while attached
        public bool AttachEnabled => IsBound || IsAttached;

        public string State
        {
            get => _state;
            set
            {
                if (_state != value)
                {
                    _state = value;
                    OnPropertyChanged();                 // State
                    OnPropertyChanged(nameof(IsBound));
                    OnPropertyChanged(nameof(IsAttached));
                    OnPropertyChanged(nameof(BindActionText));
                    OnPropertyChanged(nameof(AttachActionText));
                    OnPropertyChanged(nameof(BindEnabled));
                    OnPropertyChanged(nameof(AttachEnabled));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ─── Helper that calls “usbipd list” ────────────────────────────────────
    public static class UsbipdHelper
    {
        //  BUSID    VID:PID    Description …      State
        private static readonly Regex _row = new(
            @"^\s*(\S+)\s+([\dA-Fa-f]{4}:[\dA-Fa-f]{4})\s+(.+?)\s{2,}(\S+)\s*$",
            RegexOptions.Compiled);

        public static Task BindAsync(string bus) => RunUsbipdAsync($"bind --busid {bus}");
        public static Task UnbindAsync(string bus) => RunUsbipdAsync($"unbind --busid {bus}");
        public static Task AttachAsync(string bus) => RunUsbipdAsync($"attach --wsl --busid {bus}");
        public static Task DetachAsync(string busId) => RunUsbipdAsync($"detach --busid {busId}");

        public static async Task<IReadOnlyList<UsbDeviceInfo>> GetDevicesAsync()
        {
            var psi = new ProcessStartInfo
            {
                FileName = "usbipd",
                Arguments = "list",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)
                             ?? throw new InvalidOperationException("usbipd not found.");
            string output = await proc.StandardOutput.ReadToEndAsync();
            string err = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            Debug.WriteLine($"[UsbipdHelper] ExitCode {proc.ExitCode}");
            if (!string.IsNullOrWhiteSpace(err))
                Debug.WriteLine($"[UsbipdHelper] STDERR: {err.Trim()}");

            var devices = new List<UsbDeviceInfo>();

            foreach (var raw in output.Split(Environment.NewLine))
            {
                var line = raw.TrimEnd();
                if (string.IsNullOrWhiteSpace(line) ||
                    line.StartsWith("BUSID", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Persisted", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("GUID", StringComparison.OrdinalIgnoreCase))
                {
                    continue;   // header / section lines
                }

                // Split on TWO-OR-MORE spaces →  BUSID | VID:PID | Description | State
                var parts = Regex.Split(line, @"\s{2,}");
                if (parts.Length < 4)
                {
                    Debug.WriteLine($"[UsbipdHelper] Unparsed (parts<4): {line}");
                    continue;
                }

                var vp = parts[1].Split(':', 2, StringSplitOptions.RemoveEmptyEntries);

                devices.Add(new UsbDeviceInfo
                {
                    BusId = parts[0],
                    Vid = vp.ElementAtOrDefault(0) ?? "",
                    Pid = vp.ElementAtOrDefault(1) ?? "",
                    Description = parts[2],
                    State = parts[3]
                });
            }

            return devices;
        }

        public static async Task RunUsbipdAsync(string arguments)
        {
            Debug.WriteLine($"[UsbipdHelper] ▶ usbipd {arguments}");

            var psi = new ProcessStartInfo
            {
                FileName = "usbipd",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi)
                             ?? throw new InvalidOperationException("usbipd not found.");

            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            Debug.WriteLine($"[UsbipdHelper] Exit {proc.ExitCode}");
            if (!string.IsNullOrWhiteSpace(stdout)) Debug.WriteLine($"[UsbipdHelper] STDOUT: {stdout.Trim()}");
            if (!string.IsNullOrWhiteSpace(stderr)) Debug.WriteLine($"[UsbipdHelper] STDERR: {stderr.Trim()}");

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"usbipd {arguments} failed ({proc.ExitCode})");
        }

    }
}
