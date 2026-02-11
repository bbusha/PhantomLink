using System;
using System.Windows;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    public partial class App : Application
    {
        public App()
        {
            try
            {
                this.Startup += OnApplicationStartup;
                this.Exit += OnApplicationExit;
                this.DispatcherUnhandledException += OnUnhandledException;
            }
            catch (Exception ex)
            {
                Logger.LogError($"=== FATAL EXCEPTION IN APP CONSTRUCTOR ===\n{ex}");
                MessageBox.Show($"Fatal error in application constructor: {ex.Message}\n\nCheck the log file for details", 
                    "Runtime Tool Error", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }
        
        private void OnApplicationStartup(object sender, StartupEventArgs e)
        {
            try
            {
                // Set up global exception handlers
                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
                TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

                RegisterAssemblyResolver();

                TryStartLogTerminal(e.Args);
            }
            catch (Exception ex)
            {
                Logger.LogError($"=== FATAL EXCEPTION DURING STARTUP ===\n{ex}");
                MessageBox.Show($"Fatal error during startup: {ex.Message}\n\nCheck the log file for details", 
                    "Runtime Tool Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        private static void RegisterAssemblyResolver()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var probeDirs = new[]
                {
                    baseDir,
                    Path.Combine(baseDir, "Dependencies", "Il2CppInterop", "net6.0"),
                    Path.Combine(baseDir, "Dependencies", "MelonLoader", "MelonLoader", "net6")
                }
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .ToArray();

                AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
                {
                    try
                    {
                        var requested = assemblyName.Name;
                        if (string.IsNullOrWhiteSpace(requested))
                            return null;

                        foreach (var dir in probeDirs)
                        {
                            var candidate = Path.Combine(dir, requested + ".dll");
                            if (File.Exists(candidate))
                            {
                                return context.LoadFromAssemblyPath(candidate);
                            }
                        }

                        return null;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"[RESOLVE] Failed resolving '{assemblyName}': {ex.Message}");
                        return null;
                    }
                };

                PreloadKnownInterop(probeDirs);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Failed to register assembly resolver: {ex.Message}");
            }
        }

        private static void PreloadKnownInterop(string[] probeDirs)
        {
            TryLoadFromProbes("Il2CppInterop.Common", probeDirs);
            TryLoadFromProbes("Il2CppInterop.Runtime", probeDirs);
        }

        private static void TryLoadFromProbes(string simpleName, string[] probeDirs)
        {
            try
            {
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase)))
                    return;

                foreach (var dir in probeDirs)
                {
                    var candidate = Path.Combine(dir, simpleName + ".dll");
                    if (!File.Exists(candidate))
                        continue;

                    AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[PRELOAD] Failed to load {simpleName}: {ex.Message}");
            }
        }
        
        private void OnApplicationExit(object sender, ExitEventArgs e)
        {
            try
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during shutdown logging: {ex}");
            }
        }
        
        private void OnUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                Logger.LogError($"=== UNHANDLED EXCEPTION ===\n{e.Exception}");
                MessageBox.Show($"Unhandled exception: {e.Exception.Message}\n\nCheck the log file for details", 
                    "Runtime Tool Error", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling unhandled exception: {ex}");
            }
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                {
                    Logger.LogError($"CRITICAL: Unhandled application exception: {ex}");
                    
                    // Don't terminate the application for background thread exceptions
                    if (e.IsTerminating)
                    {
                    }
                }
            }
            catch (Exception logEx)
            {
                Debug.WriteLine($"Error in unhandled exception handler: {logEx}");
            }
        }

        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                Logger.LogError($"CRITICAL: Unobserved task exception: {e.Exception}");
                e.SetObserved(); // Mark the exception as handled to prevent application termination
            }
            catch (Exception logEx)
            {
                Debug.WriteLine($"Error in task exception handler: {logEx}");
            }
        }

        private static void TryStartLogTerminal(string[] args)
        {
            try
            {
                if (args != null && args.Any(a => string.Equals(a, "--no-terminal", StringComparison.OrdinalIgnoreCase)))
                    return;

                var logPath = Path.GetFullPath(Logger.GetLogFilePath());
                if (!File.Exists(logPath))
                    return;

                StartWindowsTerminalTail(logPath);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Failed to start log terminal: {ex.Message}");
            }
        }

        private static void StartWindowsTerminalTail(string logPath)
        {
            var escapedPath = logPath.Replace("'", "''");
            var psCommand = $"Get-Content -LiteralPath '{escapedPath}' -Wait";

            try
            {
                var wt = new ProcessStartInfo
                {
                    FileName = "wt.exe",
                    Arguments = $"-w 0 new-tab --title \"MyRuntimeTool Logs\" powershell -NoExit -Command \"{psCommand}\"",
                    UseShellExecute = true
                };

                Process.Start(wt);
                return;
            }
            catch
            {
            }

            var fallback = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoExit -Command \"{psCommand}\"",
                UseShellExecute = true
            };

            Process.Start(fallback);
        }


    }
}
