using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace PhantomLink.Core
{
    public class PatchDefinition
    {
        public string AssemblyName { get; set; }
        public string TypeName { get; set; }
        public string MethodName { get; set; }
        public string PatchType { get; set; } // Prefix, Postfix, Transpiler
        public string PatchMethod { get; set; }
        public bool Enabled { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
    }

    public static class PatchManager
    {
        private static List<PatchDefinition> _patches = new List<PatchDefinition>();
        private static string _configPath = "patches.json";

        public static void LoadPatches()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    _patches = JsonConvert.DeserializeObject<List<PatchDefinition>>(json) ?? new List<PatchDefinition>();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to load patches: {ex.Message}");
                _patches = new List<PatchDefinition>();
            }
        }

        public static void SavePatches()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_patches, Formatting.Indented);
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to save patches: {ex.Message}");
            }
        }

        public static void AddPatch(PatchDefinition patch)
        {
            _patches.Add(patch);
            SavePatches();
        }

        public static void RemovePatch(PatchDefinition patch)
        {
            _patches.Remove(patch);
            SavePatches();
        }

        public static IEnumerable<PatchDefinition> GetPatches() => _patches;

        public static void EnablePatch(PatchDefinition patch)
        {
            patch.Enabled = true;
            SavePatches();
        }

        public static void DisablePatch(PatchDefinition patch)
        {
            patch.Enabled = false;
            SavePatches();
        }
    }

    public static class Logger
    {
        public static event Action<string> OnLogMessage;
        private static string _logFilePath;
        private static readonly object _fileLock = new object();

        static Logger()
        {
            // Initialize log file path
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _logFilePath = $"PhantomLink_Log_{timestamp}.txt";
            
            // Write initial log header
            lock (_fileLock)
            {
                File.WriteAllText(_logFilePath, $"=== PHANTOMLINK LOG - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n\n");
            }
        }

        public static void LogInfo(string message)
        {
            var logMessage = $"[INFO] {DateTime.Now:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId}] - {message}";
            OnLogMessage?.Invoke(logMessage);
            WriteToFile(logMessage);
        }

        public static void LogWarning(string message)
        {
            var logMessage = $"[WARN] {DateTime.Now:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId}] - {message}";
            OnLogMessage?.Invoke(logMessage);
            WriteToFile(logMessage);
        }

        public static void LogError(string message)
        {
            var logMessage = $"[ERROR] {DateTime.Now:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId}] - {message}";
            OnLogMessage?.Invoke(logMessage);
            WriteToFile(logMessage);
        }

        private static void WriteToFile(string message)
        {
            try
            {
                lock (_fileLock)
                {
                    File.AppendAllText(_logFilePath, message + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                // If file logging fails, we can still output to debug
                System.Diagnostics.Debug.WriteLine($"Failed to write to log file: {ex.Message}");
            }
        }

        public static string GetLogFilePath() => _logFilePath;
    }
}
