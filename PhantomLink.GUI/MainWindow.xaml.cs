using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Interop;
using System.Windows.Threading;
using Newtonsoft.Json;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private RealtimeSceneExplorerWindow _sceneExplorerWindow;
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const int AutoDiscoveryHotkeyId = 0xB001;

        private HwndSource _hwndSource;
        private AppSettings _settings;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private sealed class AppSettings
        {
            public string AutoDiscoveryHotkey { get; set; } = "Ctrl+Shift+D";
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Setup log handling
            Logger.OnLogMessage += OnLogMessageReceived;
            
            // Initialize systems
            InitializeSystems();
            LoadSettings();
            if (GameControls != null)
                GameControls.SetAutoDiscoveryHotkeyText(_settings?.AutoDiscoveryHotkey ?? "Ctrl+Shift+D");

            WatchHub.Initialize(Dispatcher, WatchesGrid);
            WatchHub.SetAutoOptions(WatchesAutoRefreshCheck?.IsChecked == true, WatchesAutoPinCheck?.IsChecked == true);
            if (WatchesAutoRefreshCheck != null)
            {
                WatchesAutoRefreshCheck.Checked += (_, __) => WatchHub.SetAutoOptions(true, WatchesAutoPinCheck?.IsChecked == true);
                WatchesAutoRefreshCheck.Unchecked += (_, __) => WatchHub.SetAutoOptions(false, WatchesAutoPinCheck?.IsChecked == true);
            }
            if (WatchesAutoPinCheck != null)
            {
                WatchesAutoPinCheck.Checked += (_, __) => WatchHub.SetAutoOptions(WatchesAutoRefreshCheck?.IsChecked == true, true);
                WatchesAutoPinCheck.Unchecked += (_, __) => WatchHub.SetAutoOptions(WatchesAutoRefreshCheck?.IsChecked == true, false);
            }
        }

        private void RemoveWatch_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (WatchesGrid?.SelectedItem is WatchHub.WatchItem item)
                    WatchHub.Remove(item);
            }
            catch
            {
            }
        }

        private void ClearWatches_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                WatchHub.Clear();
            }
            catch
            {
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            if (_hwndSource != null)
            {
                _hwndSource.AddHook(WndProc);
                RegisterAutoDiscoveryHotkey(_settings?.AutoDiscoveryHotkey ?? "Ctrl+Shift+D");
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                var id = wParam.ToInt32();
                if (id == AutoDiscoveryHotkeyId)
                {
                    try
                    {
                        GameControls?.StartAutoDiscoveryFromHotkey();
                    }
                    catch
                    {
                    }
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        public void SetAutoDiscoveryHotkey(string hotkeyText)
        {
            hotkeyText = hotkeyText?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(hotkeyText))
                hotkeyText = "Ctrl+Shift+D";

            RegisterAutoDiscoveryHotkey(hotkeyText);
            _settings ??= new AppSettings();
            _settings.AutoDiscoveryHotkey = hotkeyText;
            SaveSettings();
            if (GameControls != null)
                GameControls.SetAutoDiscoveryHotkeyText(hotkeyText);
        }

        private void RegisterAutoDiscoveryHotkey(string hotkeyText)
        {
            try
            {
                if (_hwndSource == null)
                    return;
                UnregisterHotKey(_hwndSource.Handle, AutoDiscoveryHotkeyId);
                if (!TryParseHotkey(hotkeyText, out var mods, out var vk))
                    return;
                RegisterHotKey(_hwndSource.Handle, AutoDiscoveryHotkeyId, mods, vk);
            }
            catch
            {
            }
        }

        private static bool TryParseHotkey(string text, out uint modifiers, out uint virtualKey)
        {
            modifiers = 0;
            virtualKey = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var parts = text.Split(new[] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return false;

            for (var i = 0; i < parts.Length - 1; i++)
            {
                var p = parts[i].Trim();
                if (p.Equals("ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("control", StringComparison.OrdinalIgnoreCase))
                    modifiers |= MOD_CONTROL;
                else if (p.Equals("shift", StringComparison.OrdinalIgnoreCase))
                    modifiers |= MOD_SHIFT;
                else if (p.Equals("alt", StringComparison.OrdinalIgnoreCase))
                    modifiers |= MOD_ALT;
                else if (p.Equals("win", StringComparison.OrdinalIgnoreCase) || p.Equals("windows", StringComparison.OrdinalIgnoreCase))
                    modifiers |= MOD_WIN;
            }

            var keyText = parts[parts.Length - 1].Trim();
            if (keyText.Equals("esc", StringComparison.OrdinalIgnoreCase))
                keyText = "Escape";
            if (!Enum.TryParse<Key>(keyText, true, out var key))
                return false;
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftShift || key == Key.RightShift || key == Key.LeftAlt || key == Key.RightAlt || key == Key.LWin || key == Key.RWin)
                return false;
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            return virtualKey != 0;
        }

        private static string GetSettingsPath()
        {
            var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhantomLink");
            Directory.CreateDirectory(root);
            return System.IO.Path.Combine(root, "settings.json");
        }

        private void LoadSettings()
        {
            try
            {
                var path = GetSettingsPath();
                if (!File.Exists(path))
                {
                    _settings = new AppSettings();
                    return;
                }
                var json = File.ReadAllText(path);
                _settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
            catch
            {
                _settings = new AppSettings();
            }
        }

        private void SaveSettings()
        {
            try
            {
                if (_settings == null)
                    return;
                var path = GetSettingsPath();
                var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch
            {
            }
        }

        private void InitializeSystems()
        {
            try
            {
                // Start the IPC client for communicating with MelonLoader mod
                IPCMeloaderClient.Start();
                IPCMeloaderClient.ConnectionStatusChanged += OnConnectionStatusChanged;
                PatchManager.LoadPatches();
                _ = TryAutoLoadMetadataAsync();

                UpdateStatus();
            }
            catch (Exception ex)
            {
                Logger.LogError($"✗ InitializeSystems failed: {ex.Message}");
                throw;
            }
        }

        private void OnConnectionStatusChanged(bool isConnected)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (isConnected)
                {
                    // When connected, get game directory and initialize metadata loader
                    _ = TryLoadMetadataFromGameAsync();
                }
                else
                {
                    MetadataLoader.Clear();
                }
                UpdateStatus();
            });
        }

        private async Task TryAutoLoadMetadataAsync()
        {
            try
            {
                if (!string.IsNullOrEmpty(MetadataLoader.GameDirectory))
                    return;

                var toolBase = AppDomain.CurrentDomain.BaseDirectory;
                var guessed = MetadataLoader.TryDetectGameDirectoryFromToolLocation(toolBase);

                if (string.IsNullOrEmpty(guessed) || !Directory.Exists(guessed))
                    return;

                var ok = await MetadataLoader.TryInitializeAndLoadAsync(guessed);
                if (ok)
                {
                    Dispatcher.Invoke(UpdateStatus);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Metadata auto-detect failed: {ex.Message}");
            }
        }

        private async Task TryLoadMetadataFromGameAsync()
        {
            try
            {
                var response = await IPCMeloaderClient.SendCommandAsync("GET_GAME_DIRECTORY");

                if (response == null || !response.StartsWith("GAME_DIR|"))
                    return;

                var gameDirectory = response.Substring("GAME_DIR|".Length);
                if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
                {
                    Logger.LogWarning($"[IPC] Invalid game directory from mod: '{gameDirectory}'");
                    await TryAutoLoadMetadataAsync();
                    return;
                }

                if (string.Equals(MetadataLoader.GameDirectory, gameDirectory, StringComparison.OrdinalIgnoreCase))
                    return;

                var ok = await MetadataLoader.TryInitializeAndLoadAsync(gameDirectory);
                if (ok)
                {
                    Dispatcher.Invoke(UpdateStatus);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to load metadata from mod directory: {ex.Message}");
            }
        }

        private void OnLogMessageReceived(string message)
        {
            // This will be handled by the log view
            Dispatcher.Invoke(() =>
            {
                // Update status if it's an important message
                if (message.Contains("ERROR") || message.Contains("WARN") || message.Contains("INFO") && message.Contains("initialized"))
                {
                    StatusText.Text = message;
                }
            });
        }

        private void UpdateStatus()
        {
            var status = "Ready";
            
            // Check IPC client connection status
            if (IPCMeloaderClient.IsConnected)
                status += " | IPC: Connected";
            else
                status += " | IPC: Disconnected";
            
            // Check metadata loader status
            if (MetadataLoader.GameDirectory != null)
                status += $" | Game: {System.IO.Path.GetFileName(MetadataLoader.GameDirectory)}";
            
            if (MetadataLoader.IsIL2CPP)
                status += " | IL2CPP";
            else if (MetadataLoader.IsMono)
                status += " | Mono";
            
            // Use Dispatcher to update UI elements from background thread
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = status;
                if (MelonStatusText != null)
                    MelonStatusText.Text = IPCMeloaderClient.IsConnected ? "IPC: Connected" : "IPC: Disconnected";
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            // Clean up
            Logger.OnLogMessage -= OnLogMessageReceived;
            IPCMeloaderClient.ConnectionStatusChanged -= OnConnectionStatusChanged;
            try
            {
                if (_hwndSource != null)
                    UnregisterHotKey(_hwndSource.Handle, AutoDiscoveryHotkeyId);
            }
            catch
            {
            }
            IPCMeloaderClient.Stop();
            MetadataLoader.Clear();
            try
            {
                _sceneExplorerWindow?.Close();
            }
            catch
            {
            }
            base.OnClosed(e);
        }
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateStatus();
        }

        private void OpenRealtimeSceneExplorer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_sceneExplorerWindow != null)
                {
                    _sceneExplorerWindow.Activate();
                    return;
                }

                var win = new RealtimeSceneExplorerWindow
                {
                    Owner = this
                };
                win.Closed += (_, __) => _sceneExplorerWindow = null;
                _sceneExplorerWindow = win;
                win.Show();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to open Scene Explorer: {ex.Message}";
            }
        }

        private void PatchManagerView_Loaded(object sender, RoutedEventArgs e)
        {

        }
    }

    internal static class WatchHub
    {
        public sealed class WatchItem : INotifyPropertyChanged
        {
            private string _value;
            private string _status;

            public string Kind { get; init; }
            public string TypeName { get; init; }
            public string InstanceId { get; init; }
            public string MemberName { get; init; }

            public string Value
            {
                get => _value;
                set
                {
                    if (_value == value)
                        return;
                    _value = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
                }
            }

            public string Status
            {
                get => _status;
                set
                {
                    if (_status == value)
                        return;
                    _status = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private static readonly ObservableCollection<WatchItem> _items = new ObservableCollection<WatchItem>();
        private static DispatcherTimer _timer;
        private static Dispatcher _dispatcher;
        private static DataGrid _grid;
        private static bool _autoRefresh = true;
        private static bool _autoPin = true;
        private static bool _tickInProgress;

        public static IReadOnlyList<WatchItem> Items => _items;

        public static void Initialize(System.Windows.Threading.Dispatcher dispatcher, DataGrid grid)
        {
            if (_timer != null)
                return;

            _dispatcher = dispatcher;
            _grid = grid;
            if (_grid != null)
                _grid.ItemsSource = _items;

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _timer.Tick += async (_, __) => await TickAsync();
            _timer.Start();
        }

        public static void SetAutoOptions(bool autoRefresh, bool autoPin)
        {
            _autoRefresh = autoRefresh;
            _autoPin = autoPin;
        }

        public static void AddFieldWatch(string typeName, string instanceId, string fieldName)
        {
            Add("Field", typeName, instanceId, fieldName);
        }

        public static void AddPropertyWatch(string typeName, string instanceId, string propertyName)
        {
            Add("Property", typeName, instanceId, propertyName);
        }

        private static void Add(string kind, string typeName, string instanceId, string memberName)
        {
            if (_dispatcher == null)
                return;

            _dispatcher.BeginInvoke(new Action(async () =>
            {
                if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(memberName))
                    return;

                var existing = _items.FirstOrDefault(w =>
                    string.Equals(w.Kind, kind, StringComparison.Ordinal) &&
                    string.Equals(w.TypeName, typeName, StringComparison.Ordinal) &&
                    string.Equals(w.InstanceId, instanceId, StringComparison.Ordinal) &&
                    string.Equals(w.MemberName, memberName, StringComparison.Ordinal));

                if (existing != null)
                    return;

                var item = new WatchItem
                {
                    Kind = kind,
                    TypeName = typeName,
                    InstanceId = instanceId,
                    MemberName = memberName,
                    Value = "",
                    Status = "Added"
                };
                _items.Add(item);

                if (_autoPin && IPCMeloaderClient.IsConnected)
                {
                    try { await IPCMeloaderClient.SendCommandAsync($"PIN_OBJECT|{instanceId}", 8000); } catch { }
                }
            }));
        }

        public static void Remove(WatchItem item)
        {
            if (item == null)
                return;
            _items.Remove(item);
        }

        public static void Clear()
        {
            _items.Clear();
        }

        private static async Task TickAsync()
        {
            if (_tickInProgress)
                return;
            if (!_autoRefresh)
                return;
            if (_items.Count == 0)
                return;
            if (!IPCMeloaderClient.IsConnected)
            {
                for (var i = 0; i < _items.Count; i++)
                    _items[i].Status = "Not connected";
                return;
            }

            try
            {
                _tickInProgress = true;
                var count = Math.Min(_items.Count, 64);
                for (var i = 0; i < count; i++)
                {
                    var w = _items[i];
                    if (w == null)
                        continue;

                    var cmd = w.Kind == "Property"
                        ? $"GET_PROPERTY_INSTANCE|{w.TypeName}|{w.InstanceId}|{w.MemberName}"
                        : $"GET_FIELD_INSTANCE|{w.TypeName}|{w.InstanceId}|{w.MemberName}";

                    var resp = await IPCMeloaderClient.SendCommandAsync(cmd, 6000);
                    if (resp != null && resp.StartsWith("SUCCESS|", StringComparison.Ordinal))
                    {
                        w.Value = resp.Substring("SUCCESS|".Length);
                        w.Status = "OK";
                    }
                    else
                    {
                        w.Status = resp ?? "No response";
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _tickInProgress = false;
            }
        }
    }
}
