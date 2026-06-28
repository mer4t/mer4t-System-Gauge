using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace SystemGauge
{
    public partial class App : System.Windows.Application
    {
        private const string StartupArgument = "--startup";
        private const string StartupRegistryPath =
            @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupRegistryValueName =
            "SystemGauge";

        private static readonly string SettingsDirectory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "SystemGauge");

        private static readonly string SettingsFilePath =
            Path.Combine(
                SettingsDirectory,
                "settings.json");

        private static readonly string WindowPlacementFilePath =
            Path.Combine(
                SettingsDirectory,
                "window-placement.json");

        private System.Windows.Forms.NotifyIcon? notifyIcon;
        private bool isExiting;
        private bool startedHidden;
        private WindowState lastVisibleWindowState =
            WindowState.Normal;

        public bool StartWithWindows { get; private set; }

        public bool CloseToTrayOnClose { get; private set; }

        protected override void OnStartup(
            StartupEventArgs e)
        {
            base.OnStartup(e);

            LoadSettings();

            if (StartWithWindows)
            {
                TryWriteStartupRegistryEntry();
            }

            CreateTrayIcon();

            MainWindow window =
                new();

            RestoreWindowPlacement(window);

            MainWindow =
                window;

            window.Closing +=
                MainWindow_Closing;

            window.Closed +=
                MainWindow_Closed;

            window.StateChanged +=
                MainWindow_StateChanged;

            startedHidden =
                StartWithWindows &&
                e.Args.Any(argument =>
                    string.Equals(
                        argument,
                        StartupArgument,
                        StringComparison.OrdinalIgnoreCase));

            if (startedHidden)
            {
                notifyIcon!.Visible = true;
                return;
            }

            window.Show();
            window.Activate();
        }

        protected override void OnSessionEnding(
            SessionEndingCancelEventArgs e)
        {
            isExiting = true;

            if (MainWindow is Window window)
            {
                SaveWindowPlacement(window);
            }

            DisposeTrayIcon();

            base.OnSessionEnding(e);
        }

        protected override void OnExit(
            ExitEventArgs e)
        {
            DisposeTrayIcon();
            base.OnExit(e);
        }

        public bool SetStartWithWindows(
            bool enabled)
        {
            bool registryOperationSucceeded =
                enabled
                    ? TryWriteStartupRegistryEntry()
                    : TryDeleteStartupRegistryEntry();

            if (!registryOperationSucceeded)
            {
                return false;
            }

            StartWithWindows =
                enabled;

            SaveSettings();
            return true;
        }

        public void SetCloseToTrayOnClose(
            bool enabled)
        {
            CloseToTrayOnClose =
                enabled;

            SaveSettings();
        }

        private void MainWindow_StateChanged(
            object? sender,
            EventArgs e)
        {
            if (sender is not Window window ||
                window.WindowState == WindowState.Minimized)
            {
                return;
            }

            lastVisibleWindowState =
                window.WindowState;
        }

        private void MainWindow_Closing(
            object? sender,
            CancelEventArgs e)
        {
            if (sender is not Window window)
            {
                return;
            }

            SaveWindowPlacement(window);

            if (isExiting ||
                !CloseToTrayOnClose)
            {
                isExiting = true;
                return;
            }

            e.Cancel = true;
            window.Hide();

            if (notifyIcon is not null)
            {
                notifyIcon.Visible = true;
            }
        }

        private void MainWindow_Closed(
            object? sender,
            EventArgs e)
        {
            DisposeTrayIcon();
            Shutdown();
        }

        private void CreateTrayIcon()
        {
            System.Windows.Forms.ContextMenuStrip menu =
                new();

            System.Windows.Forms.ToolStripMenuItem openItem =
                new("Aç");

            System.Windows.Forms.ToolStripMenuItem exitItem =
                new("Çık");

            openItem.Click +=
                (_, _) =>
                Dispatcher.Invoke(
                    RestoreWindowFromTray);

            exitItem.Click +=
                (_, _) =>
                Dispatcher.Invoke(
                    ExitApplication);

            menu.Items.Add(openItem);
            menu.Items.Add(
                new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(exitItem);

            notifyIcon =
                new System.Windows.Forms.NotifyIcon
                {
                    Text = "mer4t - System Gauge",
                    Icon = GetApplicationIcon(),
                    ContextMenuStrip = menu,
                    Visible = true
                };

            notifyIcon.DoubleClick +=
                (_, _) =>
                Dispatcher.Invoke(
                    RestoreWindowFromTray);
        }

        private static System.Drawing.Icon GetApplicationIcon()
        {
            try
            {
                string? executablePath =
                    Environment.ProcessPath;

                if (!string.IsNullOrWhiteSpace(executablePath) &&
                    File.Exists(executablePath))
                {
                    System.Drawing.Icon? extractedIcon =
                        System.Drawing.Icon.ExtractAssociatedIcon(
                            executablePath);

                    if (extractedIcon is not null)
                    {
                        return extractedIcon;
                    }
                }
            }
            catch
            {
            }

            return System.Drawing.SystemIcons.Application;
        }

        private void RestoreWindowFromTray()
        {
            if (MainWindow is not Window window)
            {
                return;
            }

            if (!window.IsVisible)
            {
                window.Show();
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState =
                    lastVisibleWindowState == WindowState.Maximized
                        ? WindowState.Maximized
                        : WindowState.Normal;
            }

            window.Activate();

            window.Topmost = true;
            window.Topmost = false;
            window.Focus();

            startedHidden = false;
        }

        private void ExitApplication()
        {
            if (isExiting)
            {
                return;
            }

            isExiting = true;

            if (MainWindow is Window window)
            {
                SaveWindowPlacement(window);
                window.Close();
                return;
            }

            DisposeTrayIcon();
            Shutdown();
        }

        private void DisposeTrayIcon()
        {
            if (notifyIcon is null)
            {
                return;
            }

            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            notifyIcon = null;
        }

        private void LoadSettings()
        {
            StartWithWindows = false;
            CloseToTrayOnClose = false;

            try
            {
                Directory.CreateDirectory(
                    SettingsDirectory);

                if (!File.Exists(SettingsFilePath))
                {
                    return;
                }

                string json =
                    File.ReadAllText(
                        SettingsFilePath);

                ApplicationSettingsData? settings =
                    JsonSerializer.Deserialize<ApplicationSettingsData>(
                        json);

                if (settings is null)
                {
                    return;
                }

                StartWithWindows =
                    settings.StartWithWindows;

                CloseToTrayOnClose =
                    settings.CloseToTrayOnClose;
            }
            catch
            {
                StartWithWindows = false;
                CloseToTrayOnClose = false;
            }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(
                    SettingsDirectory);

                ApplicationSettingsData settings =
                    new()
                    {
                        StartWithWindows =
                            StartWithWindows,

                        CloseToTrayOnClose =
                            CloseToTrayOnClose
                    };

                string json =
                    JsonSerializer.Serialize(
                        settings,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                File.WriteAllText(
                    SettingsFilePath,
                    json);
            }
            catch
            {
            }
        }

        private static bool TryWriteStartupRegistryEntry()
        {
            try
            {
                string? executablePath =
                    Environment.ProcessPath;

                if (string.IsNullOrWhiteSpace(executablePath) ||
                    !File.Exists(executablePath))
                {
                    return false;
                }

                using RegistryKey? key =
                    Registry.CurrentUser.CreateSubKey(
                        StartupRegistryPath,
                        true);

                if (key is null)
                {
                    return false;
                }

                string command =
                    $"\"{executablePath}\" {StartupArgument}";

                key.SetValue(
                    StartupRegistryValueName,
                    command,
                    RegistryValueKind.String);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryDeleteStartupRegistryEntry()
        {
            try
            {
                using RegistryKey? key =
                    Registry.CurrentUser.OpenSubKey(
                        StartupRegistryPath,
                        true);

                key?.DeleteValue(
                    StartupRegistryValueName,
                    false);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void RestoreWindowPlacement(
            Window window)
        {
            try
            {
                if (!File.Exists(WindowPlacementFilePath))
                {
                    return;
                }

                string json =
                    File.ReadAllText(
                        WindowPlacementFilePath);

                WindowPlacementData? placement =
                    JsonSerializer.Deserialize<WindowPlacementData>(
                        json);

                if (placement is null ||
                    !IsValidPlacement(placement, window))
                {
                    return;
                }

                window.WindowStartupLocation =
                    WindowStartupLocation.Manual;

                window.Left =
                    placement.Left;

                window.Top =
                    placement.Top;

                window.Width =
                    placement.Width;

                window.Height =
                    placement.Height;

                Rect savedArea =
                    new(
                        placement.Left,
                        placement.Top,
                        placement.Width,
                        placement.Height);

                Rect virtualScreenArea =
                    new(
                        SystemParameters.VirtualScreenLeft,
                        SystemParameters.VirtualScreenTop,
                        SystemParameters.VirtualScreenWidth,
                        SystemParameters.VirtualScreenHeight);

                Rect visibleArea =
                    Rect.Intersect(
                        savedArea,
                        virtualScreenArea);

                if (visibleArea.Width < 100 ||
                    visibleArea.Height < 100)
                {
                    Rect workArea =
                        SystemParameters.WorkArea;

                    window.Left =
                        workArea.Left +
                        ((workArea.Width - window.Width) / 2);

                    window.Top =
                        workArea.Top +
                        ((workArea.Height - window.Height) / 2);
                }

                window.WindowState =
                    placement.IsMaximized
                        ? WindowState.Maximized
                        : WindowState.Normal;

                lastVisibleWindowState =
                    window.WindowState;
            }
            catch
            {
            }
        }

        private void SaveWindowPlacement(
            Window window)
        {
            try
            {
                Directory.CreateDirectory(
                    SettingsDirectory);

                Rect bounds =
                    window.RestoreBounds;

                if (bounds.Width <= 0 ||
                    bounds.Height <= 0 ||
                    double.IsNaN(bounds.Left) ||
                    double.IsNaN(bounds.Top) ||
                    double.IsNaN(bounds.Width) ||
                    double.IsNaN(bounds.Height) ||
                    double.IsInfinity(bounds.Left) ||
                    double.IsInfinity(bounds.Top) ||
                    double.IsInfinity(bounds.Width) ||
                    double.IsInfinity(bounds.Height))
                {
                    return;
                }

                WindowPlacementData placement =
                    new()
                    {
                        Left = bounds.Left,
                        Top = bounds.Top,
                        Width = bounds.Width,
                        Height = bounds.Height,
                        IsMaximized =
                            window.WindowState == WindowState.Maximized ||
                            lastVisibleWindowState == WindowState.Maximized
                    };

                string json =
                    JsonSerializer.Serialize(
                        placement,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                File.WriteAllText(
                    WindowPlacementFilePath,
                    json);
            }
            catch
            {
            }
        }

        private static bool IsValidPlacement(
            WindowPlacementData placement,
            Window window)
        {
            return
                !double.IsNaN(placement.Left) &&
                !double.IsNaN(placement.Top) &&
                !double.IsNaN(placement.Width) &&
                !double.IsNaN(placement.Height) &&
                !double.IsInfinity(placement.Left) &&
                !double.IsInfinity(placement.Top) &&
                !double.IsInfinity(placement.Width) &&
                !double.IsInfinity(placement.Height) &&
                placement.Width >= window.MinWidth &&
                placement.Height >= window.MinHeight;
        }

        private sealed class ApplicationSettingsData
        {
            public bool StartWithWindows { get; set; }

            public bool CloseToTrayOnClose { get; set; }
        }

        private sealed class WindowPlacementData
        {
            public double Left { get; set; }

            public double Top { get; set; }

            public double Width { get; set; }

            public double Height { get; set; }

            public bool IsMaximized { get; set; }
        }
    }
}
