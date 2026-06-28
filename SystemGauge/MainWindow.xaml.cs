using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Line = System.Windows.Shapes.Line;
using System.Windows.Threading;

namespace SystemGauge
{
    public partial class MainWindow : Window
    {
        private const string CpuTemperatureSensor = "/amdcpu/0/temperature/2";
        private const string CpuClockSensor = "/amdcpu/0/clock/1";
        private const string CpuFanSensor = "/lpc/nct6687d/0/fan/0";
        private const string BoardTemperatureSensor = "/lpc/nct6687d/0/temperature/1";

        private const string GpuLoadSensor = "/gpu-nvidia/0/load/0";
        private const string GpuTemperatureSensor = "/gpu-nvidia/0/temperature/0";
        private const string GpuClockSensor = "/gpu-nvidia/0/clock/0";
        private const string GpuFanSensor = "/gpu-nvidia/0/fan/1";

        private const double CpuMaximumClock = 4450.0;
        private const double CpuMaximumFanSpeed = 2000.0;
        private const double GpuMaximumClock = 2000.0;
        private const double GpuMaximumFanSpeed = 2500.0;

        private const string RamDisplayName = "LD4AU016G-3200ST · 16 GB DDR4-3200";
        private const string SsdDisplayName = "Samsung 980 PRO with Heatsink 1TB";

        private readonly DispatcherTimer timer;
        private readonly Computer hardwareComputer;

        private bool hardwareMonitorOpened;
        private bool hardwareErrorShown;
        private float? cachedBoardTemperature;

        private ulong previousIdleTime;
        private ulong previousKernelTime;
        private ulong previousUserTime;

        private NetworkInterface? activeNetworkInterface;
        private long previousBytesReceived;
        private long previousBytesSent;
        private DateTime previousNetworkSampleTime = DateTime.UtcNow;

        public MainWindow()
        {
            InitializeComponent();
            LoadApplicationSettingsIntoControls();

            ReadCpuTimes();

            hardwareComputer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true
            };

            try
            {
                hardwareComputer.Open();
                hardwareMonitorOpened = true;
                SetHardwareNames();
            }
            catch (Exception exception)
            {
                hardwareMonitorOpened = false;
                ShowHardwareError(
                    "Donanım sensörleri açılamadı. Libre Hardware Monitor açıksa tamamen kapatıp programı yönetici olarak yeniden çalıştır.",
                    exception);
            }

            SetComponentNames();
            InitializeNetworkSample();

            timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };

            timer.Tick += Timer_Tick;
            timer.Start();

            Closed += MainWindow_Closed;
        }

        private void LoadApplicationSettingsIntoControls()
        {
            if (System.Windows.Application.Current is not App app)
            {
                return;
            }

            StartWithWindowsToggle.IsChecked = app.StartWithWindows;
            CloseToTrayToggle.IsChecked = app.CloseToTrayOnClose;
        }

        private void StartWithWindowsToggle_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (System.Windows.Application.Current is not App app)
            {
                return;
            }

            bool requestedValue =
                StartWithWindowsToggle.IsChecked == true;

            if (app.SetStartWithWindows(requestedValue))
            {
                return;
            }

            StartWithWindowsToggle.IsChecked =
                app.StartWithWindows;

            MessageBox.Show(
                "Windows ile başlatma ayarı kaydedilemedi.",
                "System Gauge",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private void CloseToTrayToggle_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (System.Windows.Application.Current is not App app)
            {
                return;
            }

            app.SetCloseToTrayOnClose(
                CloseToTrayToggle.IsChecked == true);
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            double cpuUsage = GetCpuUsage();
            double gpuUsage = UpdateHardwareValues();

            AnimateGauge(CpuNeedle, CpuNeedleBrush, cpuUsage);
            AnimateGauge(GpuNeedle, GpuNeedleBrush, gpuUsage);

            UpdateMotherboardCard();
            UpdateRamCard();
            UpdateSsdCard();
            UpdateInternetCard();
        }

        private void SetHardwareNames()
        {
            IHardware? cpuHardware = hardwareComputer.Hardware
                .FirstOrDefault(hardware => hardware.HardwareType == HardwareType.Cpu);

            IHardware? gpuHardware = hardwareComputer.Hardware
                .FirstOrDefault(hardware =>
                    hardware.HardwareType == HardwareType.GpuNvidia ||
                    hardware.HardwareType == HardwareType.GpuAmd ||
                    hardware.HardwareType == HardwareType.GpuIntel);

            IHardware? boardHardware = hardwareComputer.Hardware
                .FirstOrDefault(hardware => hardware.HardwareType == HardwareType.Motherboard);

            CpuNameText.Text = cpuHardware?.Name ?? "CPU";
            GpuNameText.Text = gpuHardware?.Name ?? "GPU";
            BoardTitleText.Text = boardHardware?.Name ?? "ANAKART";
        }

        private void SetComponentNames()
        {
            RamTitleText.Text = RamDisplayName;
            SsdTitleText.Text = SsdDisplayName;
        }

        private double UpdateHardwareValues()
        {
            if (!hardwareMonitorOpened)
            {
                ShowUnavailableHardwareValues();
                return 0;
            }

            try
            {
                List<ISensor> sensors = new();

                foreach (IHardware hardware in hardwareComputer.Hardware)
                {
                    UpdateAndCollectSensors(hardware, sensors);
                }

                float? cpuTemperature = GetSensorValue(
                    sensors,
                    CpuTemperatureSensor,
                    "/amdcpu/0/",
                    SensorType.Temperature,
                    false,
                    "Core (Tctl/Tdie)",
                    "CPU Package",
                    "Package");

                float? cpuClock = GetSensorValue(
                    sensors,
                    CpuClockSensor,
                    "/amdcpu/0/",
                    SensorType.Clock,
                    false,
                    "Cores (Average)",
                    "Core Average");

                float? cpuFan = GetSensorValue(
                    sensors,
                    CpuFanSensor,
                    "/lpc/nct6687d/0/",
                    SensorType.Fan,
                    false,
                    "CPU Fan");

                float? gpuUsage = GetSensorValue(
                    sensors,
                    GpuLoadSensor,
                    "/gpu-nvidia/0/",
                    SensorType.Load,
                    true,
                    "GPU Core");

                float? gpuTemperature = GetSensorValue(
                    sensors,
                    GpuTemperatureSensor,
                    "/gpu-nvidia/0/",
                    SensorType.Temperature,
                    false,
                    "GPU Core");

                float? gpuClock = GetSensorValue(
                    sensors,
                    GpuClockSensor,
                    "/gpu-nvidia/0/",
                    SensorType.Clock,
                    true,
                    "GPU Core");

                float? gpuFan = GetSensorValue(
                    sensors,
                    GpuFanSensor,
                    "/gpu-nvidia/0/",
                    SensorType.Fan,
                    true,
                    "GPU Fan");

                cachedBoardTemperature = GetSensorValue(
                    sensors,
                    BoardTemperatureSensor,
                    "/lpc/nct6687d/0/",
                    SensorType.Temperature,
                    false,
                    "System",
                    "Motherboard",
                    "Mainboard",
                    "Chipset");

                UpdateCpuValues(cpuTemperature, cpuClock, cpuFan);
                UpdateGpuValues(gpuTemperature, gpuClock, gpuFan);

                return Math.Clamp(gpuUsage ?? 0, 0, 100);
            }
            catch (Exception exception)
            {
                ShowUnavailableHardwareValues();

                ShowHardwareError(
                    "Donanım sensörleri okunurken hata oluştu. Libre Hardware Monitor açıksa tamamen kapatıp programı yönetici olarak yeniden çalıştır.",
                    exception);

                return 0;
            }
        }

        private static void UpdateAndCollectSensors(
            IHardware hardware,
            List<ISensor> sensors)
        {
            hardware.Update();
            sensors.AddRange(hardware.Sensors);

            foreach (IHardware subHardware in hardware.SubHardware)
            {
                UpdateAndCollectSensors(subHardware, sensors);
            }
        }

        private static float? GetSensorValue(
            IEnumerable<ISensor> sensors,
            string exactIdentifier,
            string identifierPrefix,
            SensorType sensorType,
            bool allowZero,
            params string[] preferredNames)
        {
            ISensor? exactSensor = sensors.FirstOrDefault(item =>
                string.Equals(
                    item.Identifier.ToString(),
                    exactIdentifier,
                    StringComparison.OrdinalIgnoreCase));

            if (IsUsableSensorValue(exactSensor?.Value, allowZero))
            {
                return exactSensor!.Value;
            }

            IEnumerable<ISensor> matchingSensors = sensors.Where(item =>
                item.SensorType == sensorType &&
                item.Identifier.ToString().StartsWith(
                    identifierPrefix,
                    StringComparison.OrdinalIgnoreCase) &&
                IsUsableSensorValue(item.Value, allowZero));

            foreach (string preferredName in preferredNames)
            {
                ISensor? exactNameSensor = matchingSensors.FirstOrDefault(item =>
                    string.Equals(
                        item.Name,
                        preferredName,
                        StringComparison.OrdinalIgnoreCase));

                if (exactNameSensor is not null)
                {
                    return exactNameSensor.Value;
                }
            }

            foreach (string preferredName in preferredNames)
            {
                ISensor? partialNameSensor = matchingSensors.FirstOrDefault(item =>
                    item.Name.Contains(
                        preferredName,
                        StringComparison.OrdinalIgnoreCase));

                if (partialNameSensor is not null)
                {
                    return partialNameSensor.Value;
                }
            }

            return matchingSensors.FirstOrDefault()?.Value;
        }

        private static bool IsUsableSensorValue(
            float? value,
            bool allowZero)
        {
            if (!value.HasValue ||
                float.IsNaN(value.Value) ||
                float.IsInfinity(value.Value))
            {
                return false;
            }

            return allowZero
                ? value.Value >= 0
                : value.Value > 0;
        }

        private void UpdateCpuValues(
            float? temperature,
            float? clock,
            float? fan)
        {
            if (temperature.HasValue)
            {
                CpuTemperatureText.Text = $"{temperature.Value:0} °C";
                AnimateLinearBar(CpuTemperatureBar, CpuTemperatureBrush, temperature.Value);
            }
            else
            {
                CpuTemperatureText.Text = "-- °C";
                AnimateLinearBar(CpuTemperatureBar, CpuTemperatureBrush, 0);
            }

            if (clock.HasValue)
            {
                CpuClockText.Text = $"{clock.Value / 1000.0:0.00} GHz";
                AnimateLinearBar(
                    CpuClockBar,
                    CpuClockBrush,
                    clock.Value / CpuMaximumClock * 100.0);
            }
            else
            {
                CpuClockText.Text = "-- GHz";
                AnimateLinearBar(CpuClockBar, CpuClockBrush, 0);
            }

            if (fan.HasValue)
            {
                CpuFanText.Text = $"{fan.Value:0} RPM";
                AnimateLinearBar(
                    CpuFanBar,
                    CpuFanBrush,
                    fan.Value / CpuMaximumFanSpeed * 100.0);
            }
            else
            {
                CpuFanText.Text = "-- RPM";
                AnimateLinearBar(CpuFanBar, CpuFanBrush, 0);
            }
        }

        private void UpdateGpuValues(
            float? temperature,
            float? clock,
            float? fan)
        {
            if (temperature.HasValue)
            {
                GpuTemperatureText.Text = $"{temperature.Value:0} °C";
                AnimateLinearBar(GpuTemperatureBar, GpuTemperatureBrush, temperature.Value);
            }
            else
            {
                GpuTemperatureText.Text = "-- °C";
                AnimateLinearBar(GpuTemperatureBar, GpuTemperatureBrush, 0);
            }

            if (clock.HasValue)
            {
                GpuClockText.Text = $"{clock.Value / 1000.0:0.00} GHz";
                AnimateLinearBar(
                    GpuClockBar,
                    GpuClockBrush,
                    clock.Value / GpuMaximumClock * 100.0);
            }
            else
            {
                GpuClockText.Text = "-- GHz";
                AnimateLinearBar(GpuClockBar, GpuClockBrush, 0);
            }

            if (fan.HasValue)
            {
                GpuFanText.Text = $"{fan.Value:0} RPM";
                AnimateLinearBar(
                    GpuFanBar,
                    GpuFanBrush,
                    fan.Value / GpuMaximumFanSpeed * 100.0);
            }
            else
            {
                GpuFanText.Text = "-- RPM";
                AnimateLinearBar(GpuFanBar, GpuFanBrush, 0);
            }
        }

        private void UpdateMotherboardCard()
        {
            if (cachedBoardTemperature.HasValue)
            {
                BoardValueText.Text = $"{cachedBoardTemperature.Value:0} °C";
                AnimateLinearBar(BoardBar, BoardBrush, cachedBoardTemperature.Value);
            }
            else
            {
                BoardValueText.Text = "-- °C";
                AnimateLinearBar(BoardBar, BoardBrush, 0);
            }
        }

        private void UpdateRamCard()
        {
            MemoryStatusEx memoryStatus = new();

            if (!GlobalMemoryStatusEx(memoryStatus))
            {
                RamValueText.Text = "-- / -- GB";
                AnimateLinearBar(RamBar, RamBrush, 0);
                return;
            }

            double totalGb = memoryStatus.TotalPhysicalMemory / 1024.0 / 1024.0 / 1024.0;
            double availableGb = memoryStatus.AvailablePhysicalMemory / 1024.0 / 1024.0 / 1024.0;
            double usedGb = totalGb - availableGb;
            double percentage = totalGb <= 0 ? 0 : usedGb / totalGb * 100.0;

            RamValueText.Text = $"{usedGb:0.0} / {totalGb:0.0} GB";
            AnimateLinearBar(RamBar, RamBrush, percentage);
        }

        private void UpdateSsdCard()
        {
            try
            {
                string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                DriveInfo drive = new(systemRoot);

                if (!drive.IsReady)
                {
                    SsdValueText.Text = "-- / -- GB";
                    AnimateLinearBar(SsdBar, SsdBrush, 0);
                    return;
                }

                double totalGb = drive.TotalSize / 1024.0 / 1024.0 / 1024.0;
                double freeGb = drive.TotalFreeSpace / 1024.0 / 1024.0 / 1024.0;
                double usedGb = totalGb - freeGb;
                double percentage = totalGb <= 0 ? 0 : usedGb / totalGb * 100.0;

                SsdValueText.Text = $"{usedGb:0.0} / {totalGb:0.0} GB";
                AnimateLinearBar(SsdBar, SsdBrush, percentage);
            }
            catch
            {
                SsdValueText.Text = "-- / -- GB";
                AnimateLinearBar(SsdBar, SsdBrush, 0);
            }
        }

        private void InitializeNetworkSample()
        {
            activeNetworkInterface = FindActiveNetworkInterface();
            (long received, long sent) = GetCurrentNetworkBytes();

            previousBytesReceived = received;
            previousBytesSent = sent;
            previousNetworkSampleTime = DateTime.UtcNow;
        }

        private static NetworkInterface? FindActiveNetworkInterface()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface =>
                    networkInterface.OperationalStatus == OperationalStatus.Up &&
                    networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    networkInterface.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Where(networkInterface =>
                {
                    try
                    {
                        return networkInterface.GetIPProperties().GatewayAddresses.Count > 0;
                    }
                    catch
                    {
                        return false;
                    }
                })
                .OrderByDescending(networkInterface =>
                {
                    try
                    {
                        return networkInterface.Speed;
                    }
                    catch
                    {
                        return 0;
                    }
                })
                .FirstOrDefault();
        }

        private (long received, long sent) GetCurrentNetworkBytes()
        {
            try
            {
                if (activeNetworkInterface is null ||
                    activeNetworkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    activeNetworkInterface = FindActiveNetworkInterface();
                }

                if (activeNetworkInterface is null)
                {
                    return (0, 0);
                }

                IPv4InterfaceStatistics statistics =
                    activeNetworkInterface.GetIPv4Statistics();

                return (statistics.BytesReceived, statistics.BytesSent);
            }
            catch
            {
                activeNetworkInterface = FindActiveNetworkInterface();
                return (0, 0);
            }
        }

        private double GetNetworkCapacityMbPerSecond()
        {
            try
            {
                if (activeNetworkInterface is null || activeNetworkInterface.Speed <= 0)
                {
                    return 100;
                }

                double capacity = activeNetworkInterface.Speed / 8.0 / 1024.0 / 1024.0;
                return Math.Max(capacity, 1);
            }
            catch
            {
                return 100;
            }
        }

        private void UpdateInternetCard()
        {
            (long currentReceived, long currentSent) = GetCurrentNetworkBytes();
            DateTime now = DateTime.UtcNow;

            double elapsedSeconds = (now - previousNetworkSampleTime).TotalSeconds;

            if (elapsedSeconds <= 0)
            {
                return;
            }

            long receivedDifference = Math.Max(0, currentReceived - previousBytesReceived);
            long sentDifference = Math.Max(0, currentSent - previousBytesSent);

            double downloadMb = receivedDifference / elapsedSeconds / 1024.0 / 1024.0;
            double uploadMb = sentDifference / elapsedSeconds / 1024.0 / 1024.0;

            DownloadValueText.Text = $"{downloadMb:0.00} MB/s";
            UploadValueText.Text = $"{uploadMb:0.00} MB/s";

            double capacity = GetNetworkCapacityMbPerSecond();

            AnimateLinearBar(
                DownloadBar,
                DownloadBrush,
                downloadMb / capacity * 100.0);

            AnimateLinearBar(
                UploadBar,
                UploadBrush,
                uploadMb / capacity * 100.0);

            previousBytesReceived = currentReceived;
            previousBytesSent = currentSent;
            previousNetworkSampleTime = now;
        }

        private static void AnimateGauge(
            Line needle,
            SolidColorBrush needleBrush,
            double usage)
        {
            AnimateNeedle(needle, usage);
            AnimateColor(needleBrush, usage);
        }

        private static void AnimateNeedle(
            Line needle,
            double usage)
        {
            if (needle.RenderTransform is not RotateTransform rotation)
            {
                return;
            }

            usage = Math.Clamp(usage, 0, 100);

            double targetAngle = 180 + (usage / 100.0 * 270);
            double currentAngle = rotation.Angle;

            rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            rotation.Angle = currentAngle;

            DoubleAnimation animation = new()
            {
                From = currentAngle,
                To = targetAngle,
                Duration = TimeSpan.FromMilliseconds(450),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };

            rotation.BeginAnimation(
                RotateTransform.AngleProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
        }

        private static void AnimateLinearBar(
            ProgressBar progressBar,
            SolidColorBrush brush,
            double percentage)
        {
            percentage = Math.Clamp(percentage, 0, 100);

            double currentValue = progressBar.Value;

            progressBar.BeginAnimation(ProgressBar.ValueProperty, null);
            progressBar.Value = currentValue;

            DoubleAnimation animation = new()
            {
                From = currentValue,
                To = percentage,
                Duration = TimeSpan.FromMilliseconds(450),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };

            progressBar.BeginAnimation(
                ProgressBar.ValueProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);

            AnimateColor(brush, percentage);
        }

        private static void AnimateColor(
            SolidColorBrush brush,
            double percentage)
        {
            Color currentColor = brush.Color;
            Color targetColor = GetGaugeColor(percentage);

            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = currentColor;

            ColorAnimation animation = new()
            {
                From = currentColor,
                To = targetColor,
                Duration = TimeSpan.FromMilliseconds(450),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };

            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
        }

        private static Color GetGaugeColor(double percentage)
        {
            Color green = Color.FromRgb(0, 200, 83);
            Color red = Color.FromRgb(255, 31, 45);

            double amount = Math.Clamp(percentage / 100.0, 0, 1);

            byte redValue = (byte)(green.R + ((red.R - green.R) * amount));
            byte greenValue = (byte)(green.G + ((red.G - green.G) * amount));
            byte blueValue = (byte)(green.B + ((red.B - green.B) * amount));

            return Color.FromRgb(redValue, greenValue, blueValue);
        }

        private void ShowUnavailableHardwareValues()
        {
            CpuTemperatureText.Text = "-- °C";
            CpuClockText.Text = "-- GHz";
            CpuFanText.Text = "-- RPM";

            GpuTemperatureText.Text = "-- °C";
            GpuClockText.Text = "-- GHz";
            GpuFanText.Text = "-- RPM";

            AnimateLinearBar(CpuTemperatureBar, CpuTemperatureBrush, 0);
            AnimateLinearBar(CpuClockBar, CpuClockBrush, 0);
            AnimateLinearBar(CpuFanBar, CpuFanBrush, 0);

            AnimateLinearBar(GpuTemperatureBar, GpuTemperatureBrush, 0);
            AnimateLinearBar(GpuClockBar, GpuClockBrush, 0);
            AnimateLinearBar(GpuFanBar, GpuFanBrush, 0);
        }

        private void ShowHardwareError(
            string message,
            Exception exception)
        {
            if (hardwareErrorShown)
            {
                return;
            }

            hardwareErrorShown = true;

            MessageBox.Show(
                $"{message}\n\n{exception.Message}",
                "System Gauge",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private double GetCpuUsage()
        {
            if (!GetSystemTimes(
                    out FileTime idleTime,
                    out FileTime kernelTime,
                    out FileTime userTime))
            {
                return 0;
            }

            ulong idle = ConvertToUInt64(idleTime);
            ulong kernel = ConvertToUInt64(kernelTime);
            ulong user = ConvertToUInt64(userTime);

            ulong idleDifference = idle - previousIdleTime;
            ulong kernelDifference = kernel - previousKernelTime;
            ulong userDifference = user - previousUserTime;
            ulong totalDifference = kernelDifference + userDifference;

            previousIdleTime = idle;
            previousKernelTime = kernel;
            previousUserTime = user;

            if (totalDifference == 0)
            {
                return 0;
            }

            return Math.Clamp(
                (totalDifference - idleDifference) * 100.0 / totalDifference,
                0,
                100);
        }

        private void ReadCpuTimes()
        {
            if (!GetSystemTimes(
                    out FileTime idleTime,
                    out FileTime kernelTime,
                    out FileTime userTime))
            {
                return;
            }

            previousIdleTime = ConvertToUInt64(idleTime);
            previousKernelTime = ConvertToUInt64(kernelTime);
            previousUserTime = ConvertToUInt64(userTime);
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            timer.Stop();

            if (hardwareMonitorOpened)
            {
                hardwareComputer.Close();
            }
        }

        private static ulong ConvertToUInt64(FileTime fileTime)
        {
            return ((ulong)fileTime.HighDateTime << 32) | fileTime.LowDateTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(
            out FileTime idleTime,
            out FileTime kernelTime,
            out FileTime userTime);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(
            [In, Out] MemoryStatusEx memoryStatus);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private sealed class MemoryStatusEx
        {
            public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
            public uint MemoryLoad;
            public ulong TotalPhysicalMemory;
            public ulong AvailablePhysicalMemory;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }
    }
}
