using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using USBIPD_Helper.helpers; // assuming this is where UsbipdHelper is located

// ReSharper disable once CheckNamespace   (keeps XAML namespace simple)
namespace USBIPD_Helper
{
    public sealed partial class MainWindow : Window
    {
        private bool _initialised;                         // makes sure we run only once

        public MainWindow()
        {
            InitializeComponent();
            Activated += MainWindow_Activated;             // ✅  instead of “Loaded”
        }

        private async void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
        {
            if (_initialised) return;                      // first activation only
            _initialised = true;

            Debug.WriteLine("[MainWindow] Window activated – loading usbipd list…");
            await LoadAndShowDevicesAsync();
        }

        // --------------------------------------------------------------------
        private async Task LoadAndShowDevicesAsync()
        {
            try
            {
                var devices = await UsbipdHelper.GetDevicesAsync();
                Debug.WriteLine($"[MainWindow] Found {devices.Count} devices:");
                foreach (var d in devices)
                    Debug.WriteLine($"  • {d.BusId}  {d.VidPid}  {d.Description}  ({d.State})");

                DevicesListView.ItemsSource = devices;
                
                foreach (var dev in devices)
                    dev.PropertyChanged += Device_PropertyChanged;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] ERROR: {ex}");
                await new ContentDialog
                {
                    Title = "usbipd list failed",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot
                }.ShowAsync();
            }
        }

        private async void Device_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(UsbDeviceInfo.IsChecked)) return;

            var dev = (UsbDeviceInfo)sender!;
            try
            {
                if (dev.IsChecked)
                {
                    // Ticked  → bind + attach
                    await UsbipdHelper.BindAndAttachAsync(dev.BusId);
                    Debug.WriteLine($"[MainWindow] {dev.BusId} attached to WSL.");
                }
                else
                {
                    // Unticked → detach
                    await UsbipdHelper.DetachAsync(dev.BusId);
                    Debug.WriteLine($"[MainWindow] {dev.BusId} detached.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] ERROR with {dev.BusId}: {ex}");

                await new ContentDialog
                {
                    Title = $"usbipd error ({dev.BusId})",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot
                }.ShowAsync();

                // Roll the checkbox back so the UI reflects reality
                dev.PropertyChanged -= Device_PropertyChanged;   // avoid recursion
                dev.IsChecked = !dev.IsChecked;                  // revert tick
                dev.PropertyChanged += Device_PropertyChanged;
            }
        }
    }

    // ─── Model ─────────────────────────────────────────────────────────────
    public partial class UsbDeviceInfo : INotifyPropertyChanged
    {
        private bool _isChecked;

        public required string BusId { get; init; }
        public required string VidPid { get; init; }
        public required string Description { get; init; }
        public required string State { get; init; }


        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) {_isChecked = value; OnPropertyChanged(); Debug.WriteLine("Checked!");  } }
        }

        public string ToolTipText => $"{BusId}  ({VidPid})  –  {State}";

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

                Debug.WriteLine($"[UsbipdHelper] Parsed: [{string.Join("] [", parts)}]");

                devices.Add(new UsbDeviceInfo
                {
                    BusId = parts[0],
                    VidPid = parts[1],
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

        public static Task BindAndAttachAsync(string busId)
        {
            return RunUsbipdAsync($"bind --busid {busId}")  // run bind first…
            .ContinueWith(_ => RunUsbipdAsync($"attach --wsl --busid {busId}")) // …then attach
            .Unwrap();
        }

        public static Task DetachAsync(string busId)
        {
            return RunUsbipdAsync($"detach --busid {busId}");
        }
    }
}
