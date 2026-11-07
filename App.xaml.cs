using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace Island
{
    public partial class App : Application
    {
        private const string AppFolderName = "DynamicIsland";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "DynamicIsland";

        protected override void OnStartup(StartupEventArgs e)
        {
            string sourcePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? Assembly.GetExecutingAssembly().Location;
            string installDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", AppFolderName);
            string installedPath = Path.Combine(installDirectory, "DynamicIsland.exe");

            try
            {
                Directory.CreateDirectory(installDirectory);

                if (!PathsEqual(sourcePath, installedPath))
                {
                    File.Copy(sourcePath, installedPath, true);
                    SetStartupEntry(installedPath);
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = installedPath,
                        UseShellExecute = true
                    });
                    Shutdown();
                    return;
                }

                SetStartupEntry(installedPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Dynamic Island could not finish setting itself up.\n\n" + ex.Message,
                    "Dynamic Island setup",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            base.OnStartup(e);
        }

        private static void SetStartupEntry(string executablePath)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            key?.SetValue(RunValueName, "\"" + executablePath + "\"");
        }

        private static bool PathsEqual(string first, string second)
        {
            return string.Equals(
                Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}