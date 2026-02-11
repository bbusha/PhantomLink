using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    public partial class GameControlsView : UserControl
    {
        private DispatcherTimer _monitoringTimer;
        private bool _isConnected = false;
        private bool _isMonitoring = false;
        private bool _godModeEnabled;
        private bool _infiniteHealthEnabled;
        private bool _infiniteAmmoEnabled;
        private bool _noclipEnabled;
        private bool _capabilitiesRefreshing = false;
        private bool _bindingsRefreshing = false;
        private DateTime _lastBindingsRefresh = DateTime.MinValue;
        private bool _statsUpdateInProgress = false;
        private readonly ObservableCollection<CapabilityRow> _capabilityRows = new ObservableCollection<CapabilityRow>();
        private readonly ObservableCollection<KeyObjectRow> _keyObjectRows = new ObservableCollection<KeyObjectRow>();
        private readonly ObservableCollection<ComponentRow> _componentRows = new ObservableCollection<ComponentRow>();
        private readonly ObservableCollection<NumericMemberRow> _numericMemberRows = new ObservableCollection<NumericMemberRow>();
        private readonly List<NumericMemberRow> _allNumericMemberRows = new List<NumericMemberRow>();
        private string _selectedKeyObjectKey;
        private string _selectedComponentId;
        private string _selectedComponentType;
        private NumericMemberRow _selectedNumericMember;
        private string _cachedGameDirectory;

        private readonly ObservableCollection<DiscoveryRow> _discoveryRows = new ObservableCollection<DiscoveryRow>();
        private readonly DiscoveryClient _discoveryClient = new DiscoveryClient();
        private readonly HeuristicDiscoveryEngine _discoveryEngine = new HeuristicDiscoveryEngine();
        private DiscoveryScan _currentDiscoveryScan;
        private DiscoveryScan _currentDynamicScan;
        private IReadOnlyList<DiscoveryCandidateScore> _currentDiscoveryRanking;
        private long _currentExperimentId;
        private long _currentObserveSessionId;
        private long _observeLastSeq;
        private readonly List<DiscoveryObservationSample> _observeSamples = new List<DiscoveryObservationSample>();
        private readonly List<string> _observeEvents = new List<string>();
        private Dictionary<string, DiscoveryBehaviorSignature> _currentDiscoverySignatures;
        private Dictionary<string, string> _currentDiscoveryCurrentToMax;
        private List<DiscoveryRelationEdge> _currentDiscoveryRelations;
        private Dictionary<string, DiscoveryBehaviorSignature> _currentDynamicSignatures;
        private Dictionary<string, string> _currentDynamicCurrentToMax;
        private DiscoveryKnowledgeBase _discoveryKb;
        private CancellationTokenSource _autoDiscoveryCts;
        private bool _autoDiscoveryRunning;

        private readonly ObservableCollection<DynamicCheatRow> _dynamicCheatRows = new ObservableCollection<DynamicCheatRow>();
        private readonly ObservableCollection<SavedCheatRow> _savedCheatRows = new ObservableCollection<SavedCheatRow>();
        private DispatcherTimer _dynamicCheatMonitorTimer;
        private long _dynamicLastScanMs;
        private long _dynamicLastRefreshMs;
        private long _dynamicLastBindMs;
        private long _dynamicLastPollMs;
        private int _dynamicScanPasses = 4;
        private bool _dynamicAggressiveScan = true;
        private List<long> _dynamicScanIds;
        private List<DiscoveryScan> _dynamicScans;

        private enum GuidedTestKind
        {
            Damage = 0,
            Ammo = 1,
            Sprint = 2,
            Buy = 3,
            Cooldown = 4
        }

        private sealed class SavedBindingEntry
        {
            public string Slot { get; set; }
            public string KeyObjectKey { get; set; }
            public string ComponentType { get; set; }
            public string DeclaringType { get; set; }
            public string MemberKind { get; set; }
            public string MemberName { get; set; }
        }

        private sealed class SavedBindingsConfig
        {
            public Dictionary<string, List<SavedBindingEntry>> Games { get; set; } = new Dictionary<string, List<SavedBindingEntry>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, Dictionary<string, bool>> CapabilityStatesByGame { get; set; } = new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class CapabilityRow
        {
            public string Command { get; set; }
            public string Available { get; set; }
            public string Reason { get; set; }
        }

        private sealed class KeyObjectRow
        {
            public string Key { get; set; }
            public string Id { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }
        }

        private sealed class ComponentRow
        {
            public string Id { get; set; }
            public string Type { get; set; }
        }

        private sealed class NumericMemberRow
        {
            public string Kind { get; set; }
            public string Name { get; set; }
            public string DeclaringType { get; set; }
            public string ValueType { get; set; }
            public string Value { get; set; }
            public string InstanceId { get; set; }
            public string Context { get; set; }
        }

        private sealed class DiscoveryRow
        {
            public string Score { get; set; }
            public string NameScore { get; set; }
            public string TypeScore { get; set; }
            public string ContextScore { get; set; }
            public string DynamicScore { get; set; }
            public string DatabaseScore { get; set; }
            public string KnowledgeScore { get; set; }
            public string RelationScore { get; set; }
            public string CausalScore { get; set; }
            public string Kind { get; set; }
            public string Member { get; set; }
            public string DeclaringType { get; set; }
            public string ComponentType { get; set; }
            public string Writable { get; set; }
            public string Path { get; set; }
            public string Confirmed { get; set; }
            public string Key { get; set; }
            public string Fingerprint { get; set; }
            public DiscoveryConcept Concept { get; set; }
        }

        private sealed class DynamicCheatRow
        {
            public string Category { get; set; }
            public string Concept { get; set; }
            public string Confidence { get; set; }
            public string Mode { get; set; }
            public string Member { get; set; }
            public string ComponentType { get; set; }
            public string Path { get; set; }
            public string Applied { get; set; }
            public string Alternatives { get; set; }
            public string Reason { get; set; }
            public bool BindEnabled { get; set; }
            public bool TestEnabled { get; set; }
            public string TestLabel { get; set; }
            public long ScanId { get; set; }
            public string Key { get; set; }
            public string Fingerprint { get; set; }
            public DiscoveryConcept ConceptValue { get; set; }
            public IReadOnlyDictionary<string, string> Args { get; set; }
        }

        private sealed class SavedCheatRow
        {
            public string Kind { get; set; }
            public string Concept { get; set; }
            public string Mode { get; set; }
            public string Member { get; set; }
            public string Added { get; set; }
            public string AppliedCount { get; set; }
            public string Command { get; set; }
            public string Fingerprint { get; set; }
            public DiscoveryKnowledgeCheat Cheat { get; set; }
            public DiscoveryKnowledgeSavedEdit Edit { get; set; }
        }

        public GameControlsView()
        {
            InitializeComponent();
            InitializeMonitoringTimer();
            CapabilitiesListView.ItemsSource = _capabilityRows;
            KeyObjectsListView.ItemsSource = _keyObjectRows;
            ComponentsListView.ItemsSource = _componentRows;
            NumericMembersListView.ItemsSource = _numericMemberRows;
            DiscoveryCandidatesListView.ItemsSource = _discoveryRows;
            DynamicCheatsListView.ItemsSource = _dynamicCheatRows;
            SavedCheatsListView.ItemsSource = _savedCheatRows;
            BindSlotComboBox.ItemsSource = new[] { "Health", "Ammo", "Money", "XP", "Stamina" };
            BindSlotComboBox.SelectedIndex = 0;
            if (ValueScanTolBox != null) ValueScanTolBox.Text = "0";
            if (ValueScanMaxBox != null) ValueScanMaxBox.Text = "100";
            if (ValueScanDepthBox != null) ValueScanDepthBox.Text = "2";
            DiscoveryConceptBox.ItemsSource = Enum.GetValues(typeof(DiscoveryConcept)).Cast<DiscoveryConcept>().ToArray();
            DiscoveryConceptBox.SelectedIndex = 0;
            if (DiscoveryExperimentLabelBox != null) DiscoveryExperimentLabelBox.Text = "damage_test";
            if (DiscoveryProgressBar != null) DiscoveryProgressBar.Value = 0;
            if (DiscoveryHotkeyBox != null && string.IsNullOrWhiteSpace(DiscoveryHotkeyBox.Text)) DiscoveryHotkeyBox.Text = "Ctrl+Shift+D";

            if (DynamicGenreBox != null)
            {
                DynamicGenreBox.ItemsSource = new[]
                {
                    "Unknown",
                    "Shooter",
                    "RPG",
                    "Survival",
                    "Strategy",
                    "Platformer",
                    "Action",
                    "Other"
                };
                DynamicGenreBox.SelectedIndex = 0;
            }
            if (DynamicVersionBox != null && string.IsNullOrWhiteSpace(DynamicVersionBox.Text))
                DynamicVersionBox.Text = "";
            if (DynamicScanPassesBox != null)
            {
                DynamicScanPassesBox.ItemsSource = new[] { "1", "2", "3", "4" };
                DynamicScanPassesBox.SelectedIndex = 3;
            }
            if (DynamicAggressiveScanBox != null)
                DynamicAggressiveScanBox.IsChecked = true;
            _dynamicCheatMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _dynamicCheatMonitorTimer.Tick += DynamicCheatMonitorTimer_Tick;

            if (UserRoleBox != null)
            {
                UserRoleBox.ItemsSource = new[] { "Standard", "Basic", "Analyst" };
                UserRoleBox.SelectedIndex = 0;
            }

            IPCMeloaderClient.EditPerformed += OnEditPerformed;
            Unloaded += (_, __) => { try { IPCMeloaderClient.EditPerformed -= OnEditPerformed; } catch { } };
            
            // Subscribe to connection status changes
            IPCMeloaderClient.ConnectionStatusChanged += OnConnectionStatusChanged;
            
            // Pipe service is already initialized by MainWindow, just update status
            UpdateConnectionStatus();
            UpdateConnectionStatus();

            InitializeSpawnerUi();
        }

        private void InitializeSpawnerUi()
        {
            try
            {
                SpawnModeComboBox.ItemsSource = new[]
                {
                    "Primitive",
                    "Clone",
                    "Resource",
                    "Addressable",
                    "Component",
                    "Factory"
                };
                SpawnModeComboBox.SelectedIndex = 0;

                EntityTypeComboBox.ItemsSource = new[]
                {
                    "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"
                };
                EntityTypeComboBox.SelectedIndex = 0;

                SpawnModeComboBox_SelectionChanged(null, null);
            }
            catch
            {
            }
        }

        private void InitializeMonitoringTimer()
        {
            _monitoringTimer = new DispatcherTimer();
            _monitoringTimer.Interval = TimeSpan.FromMilliseconds(1000); // Reduced frequency to reduce lag
            _monitoringTimer.Tick += MonitoringTimer_Tick;
        }

        private void OnConnectionStatusChanged(bool isConnected)
        {
            var wasConnected = _isConnected;
            _isConnected = isConnected;
            
            Dispatcher.Invoke(() =>
            {
                ConnectionStatus.Fill = _isConnected ? Brushes.Green : Brushes.Red;
                ConnectionStatusText.Text = _isConnected ? "Connected to Game" : "Not Connected to Game";
                ConnectButton.IsEnabled = !_isConnected;
                DisconnectButton.IsEnabled = _isConnected;
                if (RefreshCapabilitiesButton != null)
                    RefreshCapabilitiesButton.IsEnabled = _isConnected && !_capabilitiesRefreshing;
                if (ValueFinderRefreshButton != null)
                    ValueFinderRefreshButton.IsEnabled = _isConnected;
                
                // Enable/disable controls based on connection status
                SetControlsEnabled(_isConnected);
                
                if (_isConnected)
                {
                    GameStatusText.Text = "Connected to game (auto-reconnect enabled)";
                }
                else
                {
                    GameStatusText.Text = "Disconnected - attempting to reconnect...";
                    _cachedGameDirectory = null;
                }
            });

            if (_isConnected && !wasConnected)
                _ = HandleFirstConnectAsync();
        }

        private async Task HandleFirstConnectAsync()
        {
            await RefreshCapabilitiesAsync();
            await RefreshKeyObjectsAsync();
            await ApplySavedBindingsAsync();
            await RefreshBindingsAsync();
        }

        private async void RefreshCapabilitiesButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshCapabilitiesAsync();
        }

        private void UpdateConnectionStatus()
        {
            // Just update UI based on current connection state
            OnConnectionStatusChanged(_isConnected);
        }

        private void SetControlsEnabled(bool enabled)
        {
            var buttons = new Button[]
            {
                ToggleGodModeButton, InfiniteHealthButton, InfiniteAmmoButton,
                AddHealthButton, AddMoneyButton, AddXpButton,
                PauseGameButton, ResumeGameButton, SlowMotionButton,
                SkipLevelButton, RestartLevelButton, UnlockAllButton,
                FreeCameraButton, NoclipButton, ZoomOutButton,
                FirstPersonButton, ThirdPersonButton, ResetCameraButton,
                FullbrightButton, TeleportToCamButton,
                SpawnEntityButton, ApplyTimeScaleButton,
                SetDayTimeButton, SetNightTimeButton, FreezeTimeButton,
                ShowCollidersButton, ShowFpsButton, WireframeModeButton,
                DebugConsoleButton, ReloadSceneButton, CrashGameButton,
                ValueFinderRefreshButton,
                EditSelectedMemberButton,
                DynamicRefreshButton,
                DynamicApplySelectedButton,
                SavedCheatsLoadButton,
                SavedCheatsApplyButton,
                SavedCheatsRemoveButton
            };

            foreach (var b in buttons)
            {
                if (b != null)
                    b.IsEnabled = enabled;
            }
        }

        private void UserRoleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var role = UserRoleBox?.SelectedItem?.ToString() ?? "Standard";
                if (MainTabs == null)
                    return;

                for (var i = 0; i < MainTabs.Items.Count; i++)
                {
                    if (MainTabs.Items[i] is not TabItem tab)
                        continue;
                    var header = tab.Header?.ToString() ?? "";

                    if (role.Equals("Basic", StringComparison.OrdinalIgnoreCase))
                        tab.Visibility = header.Equals("Basic Controls", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
                    else if (role.Equals("Analyst", StringComparison.OrdinalIgnoreCase))
                        tab.Visibility = header.Equals("Discovery", StringComparison.OrdinalIgnoreCase) || header.Equals("Dynamic Cheats", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
                    else
                        tab.Visibility = Visibility.Visible;
                }

                if (role.Equals("Basic", StringComparison.OrdinalIgnoreCase))
                    MainTabs.SelectedIndex = 0;
                else if (role.Equals("Analyst", StringComparison.OrdinalIgnoreCase))
                    MainTabs.SelectedIndex = FindTabIndexByHeader(MainTabs, "Discovery");
            }
            catch
            {
            }
        }

        private static int FindTabIndexByHeader(TabControl tabs, string header)
        {
            if (tabs == null || string.IsNullOrWhiteSpace(header))
                return 0;
            for (var i = 0; i < tabs.Items.Count; i++)
            {
                if (tabs.Items[i] is not TabItem tab)
                    continue;
                if (string.Equals(tab.Header?.ToString(), header, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 0;
        }

        private async Task RefreshCapabilitiesAsync()
        {
            if (_capabilitiesRefreshing)
                return;
            _capabilitiesRefreshing = true;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (RefreshCapabilitiesButton != null)
                        RefreshCapabilitiesButton.IsEnabled = false;
                });

                if (!IPCMeloaderClient.IsConnected)
                    return;

                var response = await IPCMeloaderClient.SendCommandAsync("GET_CAPABILITIES", 2000);
                if (response == null || !response.StartsWith("CAPS|"))
                    return;

                var results = new Dictionary<string, (bool enabled, string reason)>(StringComparer.OrdinalIgnoreCase);
                var caps = response.Substring("CAPS|".Length).Split('|');
                foreach (var cap in caps)
                {
                    var parts = cap.Split('=', 2);
                    if (parts.Length != 2)
                        continue;

                    var key = parts[0];
                    var value = parts[1];

                    var enabled = value.StartsWith("1");
                    var reason = string.Empty;
                    if (!enabled && value.StartsWith("0:"))
                        reason = value.Substring("0:".Length);

                    ApplyCapability(key, enabled, reason);
                    results[key] = (enabled, reason);
                }

                UpdateCapabilitiesList(results);

                if ((DateTime.UtcNow - _lastBindingsRefresh) > TimeSpan.FromSeconds(5))
                    await RefreshBindingsAsync();
            }
            catch
            {
            }
            finally
            {
                _capabilitiesRefreshing = false;
                Dispatcher.Invoke(() =>
                {
                    if (RefreshCapabilitiesButton != null)
                        RefreshCapabilitiesButton.IsEnabled = _isConnected;
                });
            }
        }

        private void UpdateCapabilitiesList(Dictionary<string, (bool enabled, string reason)> results)
        {
            Dispatcher.Invoke(() =>
            {
                var existing = _capabilityRows.ToDictionary(r => r.Command, StringComparer.OrdinalIgnoreCase);

                foreach (var kvp in results.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (!existing.TryGetValue(kvp.Key, out var row))
                    {
                        row = new CapabilityRow { Command = kvp.Key };
                        _capabilityRows.Add(row);
                    }

                    row.Available = kvp.Value.enabled ? "Yes" : "No";
                    row.Reason = kvp.Value.enabled ? "" : kvp.Value.reason;
                }

                var toRemove = _capabilityRows.Where(r => !results.ContainsKey(r.Command)).ToList();
                foreach (var r in toRemove)
                    _capabilityRows.Remove(r);

                CapabilitiesListView.Items.Refresh();
            });
        }

        private async Task RefreshBindingsAsync()
        {
            if (_bindingsRefreshing)
                return;
            _bindingsRefreshing = true;

            try
            {
                if (!IPCMeloaderClient.IsConnected)
                    return;

                var response = await IPCMeloaderClient.SendCommandAsync("GET_BINDINGS", 2000);
                if (response == null || !response.StartsWith("BINDINGS|"))
                    return;

                var parts = response.Substring("BINDINGS|".Length).Split('|');
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in parts)
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length == 2)
                        dict[kv[0]] = kv[1];
                }

                Dispatcher.Invoke(() =>
                {
                    CapabilityPlayerText.Text = dict.TryGetValue("player", out var player) ? player : "Unknown";
                    CapabilityCameraText.Text = dict.TryGetValue("camera", out var camera) ? camera : "Unknown";
                    CapabilityHealthBindingText.Text = dict.TryGetValue("health", out var health) ? health : "Not bound";
                    CapabilityAmmoBindingText.Text = dict.TryGetValue("ammo", out var ammo) ? ammo : "Not bound";
                    CapabilityMoneyBindingText.Text = dict.TryGetValue("money", out var money) ? money : "Not bound";
                    CapabilityXpBindingText.Text = dict.TryGetValue("xp", out var xp) ? xp : "Not bound";
                    CapabilityStaminaBindingText.Text = dict.TryGetValue("stamina", out var stamina) ? stamina : "Not bound";
                });

                _lastBindingsRefresh = DateTime.UtcNow;
            }
            catch
            {
            }
            finally
            {
                _bindingsRefreshing = false;
            }
        }

        private async void ValueFinderRefresh_Click(object sender, RoutedEventArgs e)
        {
            await RefreshKeyObjectsAsync();
        }

        private async Task RefreshKeyObjectsAsync()
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ValueFinderStatusText.Text = "Not connected";
                        _keyObjectRows.Clear();
                        _componentRows.Clear();
                        _numericMemberRows.Clear();
                        _allNumericMemberRows.Clear();
                        SelectedMemberText.Text = "None";
                        BindSelectedMemberButton.IsEnabled = false;
                    });
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    ValueFinderStatusText.Text = "Loading key objects...";
                    if (ValueFinderRefreshButton != null)
                        ValueFinderRefreshButton.IsEnabled = false;
                });

                var response = await IPCMeloaderClient.SendCommandAsync("GET_KEY_OBJECTS", 4000);
                if (response == null)
                    response = "ERROR|No response";

                if (response.StartsWith("ERROR|", StringComparison.Ordinal))
                {
                    Dispatcher.Invoke(() => ValueFinderStatusText.Text = response);
                    return;
                }

                if (!response.StartsWith("KEY_OBJECTS|", StringComparison.Ordinal))
                {
                    Dispatcher.Invoke(() => ValueFinderStatusText.Text = response);
                    return;
                }

                var tokens = response.Substring("KEY_OBJECTS|".Length).Split('|', StringSplitOptions.RemoveEmptyEntries);
                var rows = new List<KeyObjectRow>();
                foreach (var token in tokens)
                {
                    var dict = ParseSemicolonKeyValues(token);
                    if (!dict.TryGetValue("k", out var key) || string.IsNullOrWhiteSpace(key))
                        continue;

                    rows.Add(new KeyObjectRow
                    {
                        Key = key,
                        Id = dict.TryGetValue("id", out var id) ? id : "",
                        Name = dict.TryGetValue("name", out var name) ? name : "",
                        Type = dict.TryGetValue("type", out var type) ? type : ""
                    });
                }

                rows.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));

                Dispatcher.Invoke(() =>
                {
                    _keyObjectRows.Clear();
                    foreach (var r in rows)
                        _keyObjectRows.Add(r);

                    _componentRows.Clear();
                    _numericMemberRows.Clear();
                    _allNumericMemberRows.Clear();
                    _selectedKeyObjectKey = null;
                    _selectedComponentId = null;
                    _selectedComponentType = null;
                    _selectedNumericMember = null;
                    SelectedMemberText.Text = "None";
                    BindSelectedMemberButton.IsEnabled = false;
                    ValueFinderStatusText.Text = $"Key objects: {rows.Count}";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => ValueFinderStatusText.Text = $"Error: {ex.Message}");
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    if (ValueFinderRefreshButton != null)
                        ValueFinderRefreshButton.IsEnabled = _isConnected;
                });
            }
        }

        private async void KeyObjectsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (KeyObjectsListView.SelectedItem is KeyObjectRow row && !string.IsNullOrWhiteSpace(row.Id))
            {
                _selectedKeyObjectKey = row.Key;
                await LoadComponentsAsync(row.Id);
            }
        }

        private async Task LoadComponentsAsync(string objectId)
        {
            try
            {
                _selectedComponentId = null;
                _selectedComponentType = null;
                _selectedNumericMember = null;

                Dispatcher.Invoke(() =>
                {
                    _componentRows.Clear();
                    _numericMemberRows.Clear();
                    _allNumericMemberRows.Clear();
                    SelectedMemberText.Text = "None";
                    BindSelectedMemberButton.IsEnabled = false;
                    ValueFinderStatusText.Text = "Loading components...";
                });

                var response = await IPCMeloaderClient.SendCommandAsync($"GET_COMPONENTS|{objectId}", 6000);
                if (response == null)
                    response = "ERROR|No response";

                if (response.StartsWith("ERROR|", StringComparison.Ordinal))
                {
                    Dispatcher.Invoke(() => ValueFinderStatusText.Text = response);
                    return;
                }

                if (!response.StartsWith("COMPONENTS|", StringComparison.Ordinal))
                {
                    Dispatcher.Invoke(() => ValueFinderStatusText.Text = response);
                    return;
                }

                var tokens = response.Substring("COMPONENTS|".Length).Split('|', StringSplitOptions.RemoveEmptyEntries);
                var rows = new List<ComponentRow>();

                foreach (var token in tokens)
                {
                    if (token.StartsWith("count=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (token.StartsWith("id=", StringComparison.OrdinalIgnoreCase) && !token.Contains(";"))
                        continue;
                    if (!token.StartsWith("id=", StringComparison.OrdinalIgnoreCase) || !token.Contains(";"))
                        continue;

                    var dict = ParseSemicolonKeyValues(token);
                    if (!dict.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
                        continue;
                    rows.Add(new ComponentRow
                    {
                        Id = id,
                        Type = dict.TryGetValue("type", out var t) ? t : ""
                    });
                }

                rows.Insert(0, new ComponentRow { Id = objectId, Type = "(GameObject)" });

                Dispatcher.Invoke(() =>
                {
                    _componentRows.Clear();
                    foreach (var r in rows)
                        _componentRows.Add(r);
                    ValueFinderStatusText.Text = $"Components: {rows.Count}";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => ValueFinderStatusText.Text = $"Error: {ex.Message}");
            }
        }

        private async void ComponentsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComponentsListView.SelectedItem is ComponentRow row && !string.IsNullOrWhiteSpace(row.Id))
            {
                _selectedComponentId = row.Id;
                _selectedComponentType = row.Type;
                await LoadNumericMembersAsync(row.Id);
            }
        }

        private async Task LoadNumericMembersAsync(string instanceId)
        {
            try
            {
                _selectedNumericMember = null;
                Dispatcher.Invoke(() =>
                {
                    _numericMemberRows.Clear();
                    _allNumericMemberRows.Clear();
                    SelectedMemberText.Text = "None";
                    BindSelectedMemberButton.IsEnabled = false;
                    ValueFinderStatusText.Text = "Loading numeric members...";
                });

                var response = await IPCMeloaderClient.SendCommandAsync($"GET_NUMERIC_MEMBERS|{instanceId}", 8000);
                if (response == null)
                    response = "ERROR|No response";

                if (response.StartsWith("ERROR|", StringComparison.Ordinal))
                {
                    Dispatcher.Invoke(() => ValueFinderStatusText.Text = response);
                    return;
                }

                if (!response.StartsWith("NUMERIC_MEMBERS|", StringComparison.Ordinal))
                {
                    Dispatcher.Invoke(() => ValueFinderStatusText.Text = response);
                    return;
                }

                var tokens = response.Substring("NUMERIC_MEMBERS|".Length).Split('|', StringSplitOptions.RemoveEmptyEntries);
                var members = new List<NumericMemberRow>();
                var typeName = "";
                foreach (var token in tokens)
                {
                    if (token.StartsWith("type=", StringComparison.OrdinalIgnoreCase))
                    {
                        typeName = token.Substring("type=".Length);
                        continue;
                    }
                    if (token.StartsWith("id=", StringComparison.OrdinalIgnoreCase) || token.StartsWith("count=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!token.StartsWith("m=", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var dict = ParseSemicolonKeyValues(token);
                    members.Add(new NumericMemberRow
                    {
                        Kind = dict.TryGetValue("kind", out var kind) ? kind : "",
                        Name = dict.TryGetValue("name", out var name) ? name : "",
                        DeclaringType = dict.TryGetValue("decl", out var decl) ? decl : "",
                        ValueType = dict.TryGetValue("type", out var vt) ? vt : "",
                        Value = dict.TryGetValue("value", out var val) ? val : ""
                    });
                }

                members.Sort((a, b) =>
                {
                    var byDecl = string.Compare(a.DeclaringType, b.DeclaringType, StringComparison.OrdinalIgnoreCase);
                    if (byDecl != 0) return byDecl;
                    var byKind = string.Compare(a.Kind, b.Kind, StringComparison.OrdinalIgnoreCase);
                    if (byKind != 0) return byKind;
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });

                Dispatcher.Invoke(() =>
                {
                    _allNumericMemberRows.Clear();
                    _allNumericMemberRows.AddRange(members);
                    ApplyNumericMemberFilter();
                    ValueFinderStatusText.Text = $"{typeName} numeric members: {members.Count}";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => ValueFinderStatusText.Text = $"Error: {ex.Message}");
            }
        }

        private void NumericMembersListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NumericMembersListView.SelectedItem is not NumericMemberRow row)
            {
                _selectedNumericMember = null;
                SelectedMemberText.Text = "None";
                BindSelectedMemberButton.IsEnabled = false;
                if (EditSelectedMemberButton != null)
                    EditSelectedMemberButton.IsEnabled = false;
                return;
            }

            _selectedNumericMember = row;
            if (!string.IsNullOrWhiteSpace(row.InstanceId))
            {
                _selectedComponentId = row.InstanceId;
                _selectedKeyObjectKey = "";
                _selectedComponentType = row.Context ?? "";
            }
            var decl = string.IsNullOrWhiteSpace(row.DeclaringType) ? "" : row.DeclaringType + ".";
            var value = string.IsNullOrWhiteSpace(row.Value) ? "" : $" = {row.Value}";
            SelectedMemberText.Text = $"{row.Kind} {decl}{row.Name}{value}";
            BindSelectedMemberButton.IsEnabled = _isConnected && !string.IsNullOrWhiteSpace(_selectedComponentId);
            if (EditSelectedMemberButton != null)
                EditSelectedMemberButton.IsEnabled = _isConnected && !string.IsNullOrWhiteSpace(_selectedComponentId);
        }

        private async void EditSelectedMember_Click(object sender, RoutedEventArgs e)
        {
            await EditSelectedNumericMemberAsync();
        }

        private async void NumericMembersListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            await EditSelectedNumericMemberAsync();
        }

        private async Task EditSelectedNumericMemberAsync()
        {
            try
            {
                if (_selectedNumericMember == null)
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    ValueFinderStatusText.Text = "Not connected";
                    return;
                }
                if (string.IsNullOrWhiteSpace(_selectedNumericMember.InstanceId) || string.IsNullOrWhiteSpace(_selectedNumericMember.Name) || string.IsNullOrWhiteSpace(_selectedNumericMember.DeclaringType))
                {
                    ValueFinderStatusText.Text = "Select a member";
                    return;
                }

                var dialog = new TextPromptDialog(
                    "Edit Value",
                    $"{_selectedNumericMember.DeclaringType}.{_selectedNumericMember.Name} (id={_selectedNumericMember.InstanceId})",
                    _selectedNumericMember.Value ?? string.Empty)
                {
                    Owner = Window.GetWindow(this)
                };
                if (dialog.ShowDialog() != true)
                    return;

                var newValue = dialog.ValueText ?? string.Empty;
                var kind = (_selectedNumericMember.Kind ?? "").Trim();
                string cmd;
                if (kind.Equals("FIELD", StringComparison.OrdinalIgnoreCase))
                    cmd = $"SET_FIELD_INSTANCE|{_selectedNumericMember.DeclaringType}|{_selectedNumericMember.InstanceId}|{_selectedNumericMember.Name}|{newValue}";
                else
                    cmd = $"SET_PROPERTY_INSTANCE|{_selectedNumericMember.DeclaringType}|{_selectedNumericMember.InstanceId}|{_selectedNumericMember.Name}|{newValue}";

                var resp = await IPCMeloaderClient.SendCommandAsync(cmd, 12000);
                ValueFinderStatusText.Text = resp ?? "ERROR|No response";

                if (resp != null && resp.StartsWith("SUCCESS|", StringComparison.Ordinal))
                    _selectedNumericMember.Value = newValue;
                ApplyNumericMemberFilter();
            }
            catch (Exception ex)
            {
                ValueFinderStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private void ValueFinderFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyNumericMemberFilter();
        }

        private void ApplyNumericMemberFilter()
        {
            var term = ValueFinderFilterBox.Text?.Trim() ?? "";

            _numericMemberRows.Clear();
            if (string.IsNullOrWhiteSpace(term))
            {
                foreach (var m in _allNumericMemberRows)
                    _numericMemberRows.Add(m);
                return;
            }

            foreach (var m in _allNumericMemberRows)
            {
                if ((!string.IsNullOrWhiteSpace(m.Name) && m.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(m.DeclaringType) && m.DeclaringType.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(m.ValueType) && m.ValueType.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(m.Context) && m.Context.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    _numericMemberRows.Add(m);
                }
            }
        }

        private async void AutoDetectBinding_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    ValueFinderStatusText.Text = "Not connected";
                    return;
                }

                var slot = BindSlotComboBox.SelectedItem?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(slot))
                {
                    ValueFinderStatusText.Text = "Select a slot";
                    return;
                }

                ValueFinderStatusText.Text = "Auto detecting...";
                var cmd = $"AUTO_DETECT_BINDING|{slot.Trim().ToLowerInvariant()}";
                var response = await IPCMeloaderClient.SendCommandAsync(cmd, 8000);
                ValueFinderStatusText.Text = response ?? "ERROR|No response";

                if (response != null && response.StartsWith("SUCCESS|", StringComparison.Ordinal))
                {
                    await RefreshCapabilitiesAsync();
                    await RefreshBindingsAsync();
                }
            }
            catch (Exception ex)
            {
                ValueFinderStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void ValueScan_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    ValueFinderStatusText.Text = "Not connected";
                    return;
                }

                var valueText = ValueScanValueBox?.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(valueText))
                {
                    ValueFinderStatusText.Text = "Enter a value to scan";
                    return;
                }

                var tolText = ValueScanTolBox?.Text?.Trim() ?? "0";
                var maxText = ValueScanMaxBox?.Text?.Trim() ?? "100";
                var depthText = ValueScanDepthBox?.Text?.Trim() ?? "2";

                if (!double.TryParse(valueText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    ValueFinderStatusText.Text = "Invalid scan value";
                    return;
                }

                if (!double.TryParse(tolText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                    tolText = "0";
                if (!int.TryParse(maxText, out var max))
                    maxText = "100";
                else
                    maxText = Math.Min(Math.Max(max, 1), 400).ToString();
                if (!int.TryParse(depthText, out var depth))
                    depthText = "2";
                else
                    depthText = Math.Min(Math.Max(depth, 0), 4).ToString();

                _selectedComponentId = null;
                _selectedComponentType = null;
                _selectedKeyObjectKey = null;
                _selectedNumericMember = null;
                BindSelectedMemberButton.IsEnabled = false;
                SelectedMemberText.Text = "None";

                ValueFinderStatusText.Text = "Scanning...";
                var cmd = $"VALUE_SCAN|{valueText}|{tolText}|{maxText}|{depthText}";
                var response = await IPCMeloaderClient.SendCommandAsync(cmd, 12000);
                if (response == null)
                {
                    ValueFinderStatusText.Text = "ERROR|No response";
                    return;
                }
                if (!response.StartsWith("VALUE_SCAN|", StringComparison.Ordinal))
                {
                    ValueFinderStatusText.Text = response;
                    return;
                }

                var tokens = response.Substring("VALUE_SCAN|".Length).Split('|', StringSplitOptions.RemoveEmptyEntries);
                var members = new List<NumericMemberRow>();
                foreach (var token in tokens)
                {
                    if (!token.Contains(';') || !token.StartsWith("h=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var dict = ParseSemicolonKeyValues(token);
                    if (!dict.TryGetValue("h", out var id) || string.IsNullOrWhiteSpace(id))
                        continue;
                    members.Add(new NumericMemberRow
                    {
                        InstanceId = id,
                        Kind = dict.TryGetValue("kind", out var kind) ? kind : "",
                        Name = dict.TryGetValue("name", out var name) ? name : "",
                        DeclaringType = dict.TryGetValue("decl", out var decl) ? decl : "",
                        ValueType = dict.TryGetValue("type", out var vt) ? vt : "",
                        Value = dict.TryGetValue("value", out var val) ? val : "",
                        Context = dict.TryGetValue("context", out var ctx) ? ctx : ""
                    });
                }

                Dispatcher.Invoke(() =>
                {
                    _allNumericMemberRows.Clear();
                    _allNumericMemberRows.AddRange(members);
                    ApplyNumericMemberFilter();
                    ValueFinderStatusText.Text = $"Value scan hits: {members.Count}";
                });
            }
            catch (Exception ex)
            {
                ValueFinderStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void BindSelectedMember_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    ValueFinderStatusText.Text = "Not connected";
                    return;
                }

                if (_selectedNumericMember == null || string.IsNullOrWhiteSpace(_selectedComponentId))
                {
                    ValueFinderStatusText.Text = "No member selected";
                    return;
                }

                var slot = BindSlotComboBox.SelectedItem?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(slot))
                {
                    ValueFinderStatusText.Text = "Select a slot";
                    return;
                }

                ValueFinderStatusText.Text = "Binding...";

                var cmd = $"SET_BINDING|{slot.Trim().ToLowerInvariant()}|{_selectedComponentId}|{_selectedNumericMember.DeclaringType ?? ""}|{_selectedNumericMember.Kind ?? ""}|{_selectedNumericMember.Name ?? ""}";
                var response = await IPCMeloaderClient.SendCommandAsync(cmd, 6000);
                if (response == null)
                    response = "ERROR|No response";

                ValueFinderStatusText.Text = response;

                if (response.StartsWith("SUCCESS|", StringComparison.Ordinal))
                {
                    await SaveCurrentBindingAsync(slot.Trim(), _selectedKeyObjectKey, _selectedComponentType, _selectedNumericMember);
                    await RefreshCapabilitiesAsync();
                    await RefreshBindingsAsync();
                }
            }
            catch (Exception ex)
            {
                ValueFinderStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private static string GetBindingsConfigPath()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhantomLink");
            Directory.CreateDirectory(root);
            return Path.Combine(root, "value_bindings.json");
        }

        private static SavedBindingsConfig LoadBindingsConfig()
        {
            try
            {
                var path = GetBindingsConfigPath();
                if (!File.Exists(path))
                    return new SavedBindingsConfig();

                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<SavedBindingsConfig>(json) ?? new SavedBindingsConfig();
            }
            catch
            {
                return new SavedBindingsConfig();
            }
        }

        private static void SaveBindingsConfig(SavedBindingsConfig config)
        {
            var path = GetBindingsConfigPath();
            var json = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(path, json);
        }

        private async Task<string> GetGameDirectoryAsync()
        {
            if (!string.IsNullOrWhiteSpace(_cachedGameDirectory))
                return _cachedGameDirectory;

            if (!IPCMeloaderClient.IsConnected)
                return null;

            var response = await IPCMeloaderClient.SendCommandAsync("GET_GAME_DIRECTORY", 2000);
            if (response != null && response.StartsWith("GAME_DIR|", StringComparison.Ordinal))
            {
                _cachedGameDirectory = response.Substring("GAME_DIR|".Length);
                return _cachedGameDirectory;
            }

            return null;
        }

        private async Task SaveCurrentBindingAsync(string slot, string keyObjectKey, string componentType, NumericMemberRow member)
        {
            if (member == null)
                return;

            var gameDir = await GetGameDirectoryAsync();
            if (string.IsNullOrWhiteSpace(gameDir))
                return;

            if (string.IsNullOrWhiteSpace(keyObjectKey))
                return;

            var entry = new SavedBindingEntry
            {
                Slot = slot,
                KeyObjectKey = keyObjectKey,
                ComponentType = componentType ?? "",
                DeclaringType = member.DeclaringType ?? "",
                MemberKind = member.Kind ?? "",
                MemberName = member.Name ?? ""
            };

            var config = LoadBindingsConfig();
            if (!config.Games.TryGetValue(gameDir, out var list) || list == null)
            {
                list = new List<SavedBindingEntry>();
                config.Games[gameDir] = list;
            }

            list.RemoveAll(b => string.Equals(b.Slot, entry.Slot, StringComparison.OrdinalIgnoreCase));
            list.Add(entry);
            SaveBindingsConfig(config);
        }

        private async Task SaveCapabilityStateAsync(string command, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(command))
                return;

            var gameDir = await GetGameDirectoryAsync();
            if (string.IsNullOrWhiteSpace(gameDir))
                return;

            var config = LoadBindingsConfig();
            if (!config.CapabilityStatesByGame.TryGetValue(gameDir, out var dict) || dict == null)
            {
                dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                config.CapabilityStatesByGame[gameDir] = dict;
            }

            dict[command.Trim()] = enabled;
            SaveBindingsConfig(config);
        }

        private async Task ApplySavedBindingsAsync()
        {
            if (!IPCMeloaderClient.IsConnected)
                return;

            var gameDir = await GetGameDirectoryAsync();
            if (string.IsNullOrWhiteSpace(gameDir))
                return;

            var config = LoadBindingsConfig();
            if (!config.Games.TryGetValue(gameDir, out var list) || list == null || list.Count == 0)
                return;

            var appliedAny = false;
            foreach (var b in list)
            {
                if (b == null)
                    continue;

                var cmd = $"SET_BINDING_KEY|{(b.Slot ?? "").Trim().ToLowerInvariant()}|{b.KeyObjectKey}|{b.ComponentType}|{b.DeclaringType}|{b.MemberKind}|{b.MemberName}";
                var response = await IPCMeloaderClient.SendCommandAsync(cmd, 6000);
                if (response != null && response.StartsWith("SUCCESS|", StringComparison.Ordinal))
                    appliedAny = true;
            }

            if (appliedAny)
                await RefreshBindingsAsync();
        }

        private async Task ApplySavedCapabilityStatesAsync()
        {
            if (!IPCMeloaderClient.IsConnected)
                return;

            var gameDir = await GetGameDirectoryAsync();
            if (string.IsNullOrWhiteSpace(gameDir))
                return;

            var config = LoadBindingsConfig();
            if (!config.CapabilityStatesByGame.TryGetValue(gameDir, out var dict) || dict == null || dict.Count == 0)
                return;

            foreach (var kvp in dict)
            {
                var command = kvp.Key?.Trim();
                if (string.IsNullOrWhiteSpace(command))
                    continue;

                var desired = kvp.Value ? "1" : "0";
                await IPCMeloaderClient.SendCommandAsync($"SET_CAPABILITY_STATE|{command}|{desired}", 6000);
            }
        }

        private static Dictionary<string, string> ParseSemicolonKeyValues(string token)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(token))
                return dict;

            var parts = token.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2)
                    continue;
                dict[kv[0]] = kv[1];
            }

            return dict;
        }

        private void ApplyCapability(string command, bool enabled, string reason)
        {
            Button button = command switch
            {
                "TOGGLE_GODMODE" => ToggleGodModeButton,
                "TOGGLE_INFINITE_HEALTH" => InfiniteHealthButton,
                "TOGGLE_INFINITE_AMMO" => InfiniteAmmoButton,
                "ADD_HEALTH" => AddHealthButton,
                "ADD_MONEY" => AddMoneyButton,
                "ADD_XP" => AddXpButton,
                "PAUSE_GAME" => PauseGameButton,
                "RESUME_GAME" => ResumeGameButton,
                "SET_TIMESCALE" => ApplyTimeScaleButton,
                "SKIP_LEVEL" => SkipLevelButton,
                "RESTART_LEVEL" => RestartLevelButton,
                "UNLOCK_ALL" => UnlockAllButton,
                "FREE_CAMERA" => FreeCameraButton,
                "TOGGLE_NOCLIP" => NoclipButton,
                "ZOOM_OUT" => ZoomOutButton,
                "FIRST_PERSON" => FirstPersonButton,
                "THIRD_PERSON" => ThirdPersonButton,
                "RESET_CAMERA" => ResetCameraButton,
                "SPAWN_ENTITY" => SpawnEntityButton,
                "SET_DAYTIME" => SetDayTimeButton,
                "SET_NIGHTTIME" => SetNightTimeButton,
                "FREEZE_TIME" => FreezeTimeButton,
                "TOGGLE_COLLIDERS" => ShowCollidersButton,
                "TOGGLE_FPS_DISPLAY" => ShowFpsButton,
                "TOGGLE_WIREFRAME" => WireframeModeButton,
                "OPEN_DEBUG_CONSOLE" => DebugConsoleButton,
                "RELOAD_SCENE" => ReloadSceneButton,
                "CRASH_GAME" => CrashGameButton,
                _ => null
            };

            if (button == null)
                return;

            button.IsEnabled = _isConnected && enabled;
            button.ToolTip = enabled ? null : string.IsNullOrWhiteSpace(reason) ? "Unavailable" : reason;
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ConnectButton.IsEnabled = false;
                GameStatusText.Text = "Connecting to game...";
                
                // Use proper async connection checking with timeout
                bool connected = await CheckConnectionWithTimeoutAsync(TimeSpan.FromSeconds(5));
                
                if (connected)
                {
                    GameStatusText.Text = "Connected to game successfully!";
                    Logger.LogInfo("Game controls connected to game process");
                }
                else
                {
                    GameStatusText.Text = "Failed to connect to game. Make sure MelonLoader is running.";
                    Logger.LogWarning("Failed to connect to game process from game controls");
                }
                
                UpdateConnectionStatus();
            }
            catch (Exception ex)
            {
                GameStatusText.Text = "Connection error: " + ex.Message;
                Logger.LogError("Game controls connection error: " + ex.Message);
            }
            finally
            {
                ConnectButton.IsEnabled = true;
            }
        }
        
        private async Task<bool> CheckConnectionWithTimeoutAsync(TimeSpan timeout)
        {
            var startTime = DateTime.Now;
            
            while (DateTime.Now - startTime < timeout)
            {
                // Check if pipe is connected AND the MelonLoader mod is connected to a game
                if (IPCMeloaderClient.IsConnected)
                {
                    // Send GAME_STATUS command to verify the mod is actually connected to a game
                    var response = await IPCMeloaderClient.SendCommandAsync("GAME_STATUS");
                    
                    if (IPCMeloaderClient.TryParseSuccess(response, out string result) && 
                        result.StartsWith("GAME_STATUS|"))
                    {
                        string status = result.Substring("GAME_STATUS|".Length);
                        if (bool.TryParse(status, out bool isGameConnected) && isGameConnected)
                        {
                            return true;
                        }
                    }
                }
                    
                // Use proper async delay to avoid UI freezing
                await Task.Delay(100);
            }
            
            return false;
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StopMonitoring();
                // Pipe disconnection is handled by the pipe service itself
                GameStatusText.Text = "Disconnected from game";
                Logger.LogInfo("Game controls disconnected from game process");
                UpdateConnectionStatus();
            }
            catch (Exception ex)
            {
                GameStatusText.Text = "Disconnection error: " + ex.Message;
                Logger.LogError("Game controls disconnection error: " + ex.Message);
            }
        }

        private async void MonitoringTimer_Tick(object sender, EventArgs e)
        {
            if (!_isConnected || !_isMonitoring) return;

            try
            {
                // Update real-time game stats via pipe
                await UpdateGameStatsAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError("Monitoring error: " + ex.Message);
            }
        }

        private async Task UpdateGameStatsAsync()
        {
            if (!_isConnected || !_isMonitoring) return;
            if (_statsUpdateInProgress) return;
            _statsUpdateInProgress = true;

            try
            {
                var keys = new List<string>();
                if (MonitorFpsCheckbox.IsChecked == true)
                    keys.Add("fps");
                keys.Add("memory");
                if (MonitorHealthCheckbox.IsChecked == true)
                    keys.Add("health");
                if (MonitorPositionCheckbox.IsChecked == true)
                    keys.Add("position");
                keys.Add("level");
                keys.Add("time");
                keys.Add("money");
                keys.Add("stamina");
                keys.Add("ammo");

                var cmd = keys.Count == 0 ? "GET_STATS_BATCH" : ("GET_STATS_BATCH|" + string.Join("|", keys));

                // Use a single batch command to reduce pipe calls and lag
                string batchResponse = await IPCMeloaderClient.SendCommandAsync(cmd);
                
                if (batchResponse != null && batchResponse.StartsWith("STATS_BATCH|"))
                {
                    // Parse batch response format: STATS_BATCH|fps=60|memory=512|health=100|position=x,y,z|level=1|time=12:34
                    var stats = batchResponse.Substring("STATS_BATCH|".Length).Split('|');
                    var statsDict = new Dictionary<string, string>();
                    
                    foreach (var stat in stats)
                    {
                        var parts = stat.Split('=', 2);
                        if (parts.Length == 2)
                            statsDict[parts[0]] = parts[1];
                    }

                    Dispatcher.Invoke(() =>
                    {
                        // Update FPS if monitoring enabled
                        if (MonitorFpsCheckbox.IsChecked == true && statsDict.TryGetValue("fps", out var fps))
                            FpsText.Text = fps;

                        // Update memory usage
                        if (statsDict.TryGetValue("memory", out var memory))
                            MemoryText.Text = memory + " MB";

                        // Update health if monitoring enabled
                        if (MonitorHealthCheckbox.IsChecked == true && statsDict.TryGetValue("health", out var health))
                            HealthText.Text = health;

                        // Update position if monitoring enabled
                        if (MonitorPositionCheckbox.IsChecked == true && statsDict.TryGetValue("position", out var position))
                            PositionText.Text = position;

                        // Update level
                        if (statsDict.TryGetValue("level", out var level))
                            LevelText.Text = level;

                        // Update game time
                        if (statsDict.TryGetValue("time", out var time))
                            GameTimeText.Text = time;

                        // Update money
                        if (statsDict.TryGetValue("money", out var money))
                            MoneyText.Text = money;

                        // Update stamina
                        if (statsDict.TryGetValue("stamina", out var stamina))
                            StaminaText.Text = stamina;

                        // Update ammo
                        if (statsDict.TryGetValue("ammo", out var ammo))
                            AmmoText.Text = ammo;
                    });
                }
                else
                {
                    // Fallback to individual commands if batch not supported
                    await UpdateGameStatsFallback();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Game stats update error: " + ex.Message);
                
                // If we get an error, stop monitoring to prevent continuous errors
                if (ex is TimeoutException || ex.Message.Contains("Pipe"))
                {
                    Dispatcher.Invoke(() =>
                    {
                        StopMonitoring();
                        GameStatusText.Text = "Monitoring stopped due to connection issues";
                    });
                }
            }
            finally
            {
                _statsUpdateInProgress = false;
            }
        }

        private async Task UpdateGameStatsFallback()
        {
            try
            {
                // Get FPS via pipe
                if (MonitorFpsCheckbox.IsChecked == true)
                {
                    string fpsResponse = await IPCMeloaderClient.SendCommandAsync("GET_FPS");
                    if (fpsResponse != null && fpsResponse.StartsWith("FPS|"))
                    {
                        Dispatcher.Invoke(() => FpsText.Text = fpsResponse.Substring("FPS|".Length));
                    }
                }

                // Get memory usage via pipe
                string memoryResponse = await IPCMeloaderClient.SendCommandAsync("GET_MEMORY");
                if (memoryResponse != null && memoryResponse.StartsWith("MEMORY|"))
                {
                    Dispatcher.Invoke(() => MemoryText.Text = memoryResponse.Substring("MEMORY|".Length) + " MB");
                }

                // Get player health via pipe
                if (MonitorHealthCheckbox.IsChecked == true)
                {
                    string healthResponse = await IPCMeloaderClient.SendCommandAsync("GET_HEALTH");
                    if (healthResponse != null && healthResponse.StartsWith("HEALTH|"))
                    {
                        Dispatcher.Invoke(() => HealthText.Text = healthResponse.Substring("HEALTH|".Length));
                    }
                }

                // Get player position via pipe
                if (MonitorPositionCheckbox.IsChecked == true)
                {
                    string positionResponse = await IPCMeloaderClient.SendCommandAsync("GET_POSITION");
                    if (positionResponse != null && positionResponse.StartsWith("POSITION|"))
                    {
                        Dispatcher.Invoke(() => PositionText.Text = positionResponse.Substring("POSITION|".Length));
                    }
                }

                // Get current level via pipe
                string levelResponse = await IPCMeloaderClient.SendCommandAsync("GET_LEVEL");
                if (levelResponse != null && levelResponse.StartsWith("LEVEL|"))
                {
                    Dispatcher.Invoke(() => LevelText.Text = levelResponse.Substring("LEVEL|".Length));
                }

                // Get game time via pipe
                string timeResponse = await IPCMeloaderClient.SendCommandAsync("GET_TIME");
                if (timeResponse != null && timeResponse.StartsWith("TIME|"))
                {
                    Dispatcher.Invoke(() => GameTimeText.Text = timeResponse.Substring("TIME|".Length));
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Game stats fallback update error: " + ex.Message);
            }
        }

        private void StartMonitoring_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                GameStatusText.Text = "Not connected to game";
                return;
            }

            _isMonitoring = true;
            _monitoringTimer.Start();
            GameStatusText.Text = "Monitoring game stats...";
            Logger.LogInfo("Started game monitoring");
        }

        private void StopMonitoring_Click(object sender, RoutedEventArgs e)
        {
            StopMonitoring();
            GameStatusText.Text = "Monitoring stopped";
        }

        private void StopMonitoring()
        {
            _isMonitoring = false;
            _monitoringTimer.Stop();
            Logger.LogInfo("Stopped game monitoring");
        }

        private void UpdateIntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_monitoringTimer != null)
            {
                _monitoringTimer.Interval = TimeSpan.FromMilliseconds(UpdateIntervalSlider.Value);
                UpdateIntervalText.Text = $"Update every {UpdateIntervalSlider.Value}ms";
            }
        }

        // Basic game control methods that send commands via pipe
        private async void ToggleGodMode_Click(object sender, RoutedEventArgs e)
        {
            _godModeEnabled = !_godModeEnabled;
            await SendGameCommand($"SET_CHEAT|GODMODE|{(_godModeEnabled ? "1" : "0")}", $"God Mode {(_godModeEnabled ? "Enabled" : "Disabled")}");
            ToggleGodModeButton.Content = _godModeEnabled ? "Disable God Mode" : "Enable God Mode";
        }

        private async void InfiniteHealth_Click(object sender, RoutedEventArgs e)
        {
            _infiniteHealthEnabled = !_infiniteHealthEnabled;
            await SendGameCommand($"SET_CHEAT|INFINITE_HEALTH|{(_infiniteHealthEnabled ? "1" : "0")}", $"Infinite Health {(_infiniteHealthEnabled ? "Enabled" : "Disabled")}");
            InfiniteHealthButton.Content = _infiniteHealthEnabled ? "Disable Inf Health" : "Enable Inf Health";
        }

        private async void InfiniteAmmo_Click(object sender, RoutedEventArgs e)
        {
            _infiniteAmmoEnabled = !_infiniteAmmoEnabled;
            await SendGameCommand($"SET_CHEAT|INFINITE_AMMO|{(_infiniteAmmoEnabled ? "1" : "0")}", $"Infinite Ammo {(_infiniteAmmoEnabled ? "Enabled" : "Disabled")}");
            InfiniteAmmoButton.Content = _infiniteAmmoEnabled ? "Disable Inf Ammo" : "Enable Inf Ammo";
        }

        private async void AddHealth_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("ADD_HEALTH 50", "Health added");
        }

        private async void AddMoney_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("ADD_MONEY 1000", "Money added");
        }

        private async void AddXP_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("ADD_XP 500", "XP added");
        }

        private async void PauseGame_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("PAUSE_GAME", "Game paused");
        }

        private async void ResumeGame_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("RESUME_GAME", "Game resumed");
        }

        private async void SlowMotion_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("SET_TIMESCALE 0.5", "Slow motion activated");
        }

        private async void SkipLevel_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("SKIP_LEVEL", "Level skipped");
        }

        private async void RestartLevel_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("RESTART_LEVEL", "Level restarted");
        }

        private async void UnlockAll_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("UNLOCK_ALL", "All content unlocked");
        }

        // Missing event handlers for XAML buttons
        private async void FreeCamera_Click(object sender, RoutedEventArgs e)
        {
            // Toggle FreeCam
            var result = await IPCMeloaderClient.SendCommandAsync("TOGGLE_FREECAM");
            if (result != null && result.StartsWith("SUCCESS|FREECAM|"))
            {
                var state = result.Substring("SUCCESS|FREECAM|".Length);
                bool enabled = state.Equals("True", StringComparison.OrdinalIgnoreCase);
                FreeCameraButton.Content = enabled ? "Disable FreeCam" : "Enable FreeCam";
                GameStatusText.Text = enabled ? "FreeCam Enabled (WASD+QE+Shift)" : "FreeCam Disabled";
            }
            else
            {
                GameStatusText.Text = result ?? "Error toggling FreeCam";
            }
        }

        private async void Noclip_Click(object sender, RoutedEventArgs e)
        {
            _noclipEnabled = !_noclipEnabled;
            await SendGameCommand($"SET_CHEAT|NOCLIP|{(_noclipEnabled ? "1" : "0")}", $"Noclip {(_noclipEnabled ? "Enabled" : "Disabled")}");
            NoclipButton.Content = _noclipEnabled ? "Disable Noclip" : "Enable Noclip";
        }

        private async void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("ZOOM_OUT", "Zoom out activated");
        }

        private async void FirstPerson_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("FIRST_PERSON", "First person view activated");
        }

        private async void ThirdPerson_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("THIRD_PERSON", "Third person view activated");
        }

        private async void ResetCamera_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("RESET_CAMERA", "Camera reset");
        }

        private async void Fullbright_Click(object sender, RoutedEventArgs e)
        {
            var result = await IPCMeloaderClient.SendCommandAsync("TOGGLE_FULLBRIGHT");
            if (result != null && result.StartsWith("SUCCESS|FULLBRIGHT|"))
            {
                var state = result.Substring("SUCCESS|FULLBRIGHT|".Length);
                bool enabled = state.Equals("True", StringComparison.OrdinalIgnoreCase);
                FullbrightButton.Content = enabled ? "Disable Fullbright" : "Enable Fullbright";
                GameStatusText.Text = enabled ? "Fullbright Enabled" : "Fullbright Disabled";
            }
            else
            {
                GameStatusText.Text = result ?? "Error toggling Fullbright";
            }
        }

        private async void TeleportToCam_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("TELEPORT_TO_CAMERA", "Teleported to Camera");
        }

        private async void SpawnEntity_Click(object sender, RoutedEventArgs e)
        {
            var mode = (SpawnModeComboBox.SelectedItem as string) ?? "Primitive";
            var primitive = (EntityTypeComboBox.SelectedItem as string) ?? "Cube";
            var source = SpawnSourceBox.Text?.Trim() ?? string.Empty;
            var name = SpawnNameBox.Text?.Trim() ?? string.Empty;

            var px = PositionXBox.Text?.Trim() ?? "";
            var py = PositionYBox.Text?.Trim() ?? "";
            var pz = PositionZBox.Text?.Trim() ?? "";

            var rx = RotationXBox.Text?.Trim() ?? "";
            var ry = RotationYBox.Text?.Trim() ?? "";
            var rz = RotationZBox.Text?.Trim() ?? "";

            var sx = ScaleXBox.Text?.Trim() ?? "";
            var sy = ScaleYBox.Text?.Trim() ?? "";
            var sz = ScaleZBox.Text?.Trim() ?? "";

            bool TryFloat(string t, out double v) => double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);

            var args = new List<string>();

            TryFloat(px, out var fpx);
            TryFloat(py, out var fpy);
            TryFloat(pz, out var fpz);
            var posProvided = (fpx != 0 || fpy != 0 || fpz != 0);
            if (posProvided)
                args.Add($"pos={fpx.ToString(System.Globalization.CultureInfo.InvariantCulture)},{fpy.ToString(System.Globalization.CultureInfo.InvariantCulture)},{fpz.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            TryFloat(rx, out var frx);
            TryFloat(ry, out var fry);
            TryFloat(rz, out var frz);
            var rotProvided = (frx != 0 || fry != 0 || frz != 0);
            if (rotProvided)
                args.Add($"rot={frx.ToString(System.Globalization.CultureInfo.InvariantCulture)},{fry.ToString(System.Globalization.CultureInfo.InvariantCulture)},{frz.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            TryFloat(sx, out var fsx);
            TryFloat(sy, out var fsy);
            TryFloat(sz, out var fsz);
            if (fsx == 0) fsx = 1;
            if (fsy == 0) fsy = 1;
            if (fsz == 0) fsz = 1;
            var scaleProvided = (fsx != 1 || fsy != 1 || fsz != 1);
            if (scaleProvided)
                args.Add($"scale={fsx.ToString(System.Globalization.CultureInfo.InvariantCulture)},{fsy.ToString(System.Globalization.CultureInfo.InvariantCulture)},{fsz.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            if (!string.IsNullOrWhiteSpace(name))
                args.Add($"name={name}");

            string command;
            if (mode.Equals("Primitive", StringComparison.OrdinalIgnoreCase))
            {
                command = $"SPAWN_ENTITY|PRIMITIVE|{primitive}";
            }
            else if (mode.Equals("Clone", StringComparison.OrdinalIgnoreCase))
            {
                command = $"SPAWN_ENTITY|CLONE|{source}";
            }
            else if (mode.Equals("Resource", StringComparison.OrdinalIgnoreCase))
            {
                command = $"SPAWN_ENTITY|RESOURCE|{source}";
            }
            else if (mode.Equals("Addressable", StringComparison.OrdinalIgnoreCase))
            {
                command = $"SPAWN_ENTITY|ADDRESSABLE|{source}";
            }
            else if (mode.Equals("Factory", StringComparison.OrdinalIgnoreCase))
            {
                var typeName = FactoryTypeBox.Text?.Trim() ?? string.Empty;
                var methodName = FactoryMethodBox.Text?.Trim() ?? string.Empty;
                var factoryArgs = FactoryArgsBox.Text?.Trim() ?? string.Empty;
                command = $"SPAWN_ENTITY|FACTORY|{typeName}::{methodName}";
                if (!string.IsNullOrWhiteSpace(factoryArgs))
                    args.Add($"args={factoryArgs}");
            }
            else
            {
                command = $"SPAWN_ENTITY|COMPONENT|{source}";
            }

            if (args.Count > 0)
                command += "|" + string.Join("|", args);

            await SendGameCommand(command, "Entity spawned");
        }

        private void SpawnModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var mode = (SpawnModeComboBox.SelectedItem as string) ?? "Primitive";
            var primitive = mode.Equals("Primitive", StringComparison.OrdinalIgnoreCase);
            var factory = mode.Equals("Factory", StringComparison.OrdinalIgnoreCase);
            EntityTypeComboBox.IsEnabled = primitive;
            SpawnSourceBox.IsEnabled = !primitive;
            FactoryTypeBox.IsEnabled = factory;
            FactoryMethodBox.IsEnabled = factory;
            FactoryArgsBox.IsEnabled = factory;
        }

        private async void ApplyTimeScale_Click(object sender, RoutedEventArgs e)
        {
            double scale = TimeScaleSlider.Value;
            await SendGameCommand($"SET_TIMESCALE {scale}", $"Time scale set to {scale}");
        }

        private async void SetDayTime_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("SET_DAYTIME", "Day time set");
        }

        private async void SetNightTime_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("SET_NIGHTTIME", "Night time set");
        }

        private async void FreezeTime_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("FREEZE_TIME", "Time frozen");
        }

        private async void ShowColliders_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("TOGGLE_COLLIDERS", "Colliders visibility toggled");
        }

        private async void ShowFPS_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("TOGGLE_FPS_DISPLAY", "FPS display toggled");
        }

        private async void WireframeMode_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("TOGGLE_WIREFRAME", "Wireframe mode toggled");
        }

        private async void DebugConsole_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("OPEN_DEBUG_CONSOLE", "Debug console opened");
        }

        private async void ReloadScene_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("RELOAD_SCENE", "Scene reloaded");
        }

        private async void CrashGame_Click(object sender, RoutedEventArgs e)
        {
            await SendGameCommand("CRASH_GAME", "Game crash initiated");
        }

        private async void DiscoveryScan_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    DiscoveryStatusText.Text = "Not connected";
                    return;
                }

                DiscoveryScanButton.IsEnabled = false;
                DiscoveryStatusText.Text = "Scanning...";
                _currentExperimentId = 0;
                DiscoveryEndExperimentButton.IsEnabled = false;
                _currentObserveSessionId = 0;
                _observeLastSeq = 0;
                _observeSamples.Clear();
                _observeEvents.Clear();
                _currentDiscoverySignatures = null;
                _currentDiscoveryCurrentToMax = null;
                _currentDiscoveryRelations = null;
                DiscoveryPullAnalyzeButton.IsEnabled = false;
                DiscoveryStopObserveButton.IsEnabled = false;
                DiscoveryMarkEventButton.IsEnabled = false;
                DiscoveryProgressBar.Value = 0;
                if (DiscoverySuggestionText != null)
                    DiscoverySuggestionText.Text = "";

                var scanId = await _discoveryClient.StartScanAsync(12000, "numeric,bool,count,enum", "mono", 2, 30000);
                while (true)
                {
                    var status = await _discoveryClient.GetScanStatusAsync(scanId, 12000);
                    DiscoveryProgressBar.Value = Math.Max(0, Math.Min(1, status.Progress));
                    if (status.Done || (!status.Running && status.Progress >= 1))
                        break;
                    DiscoveryStatusText.Text = $"Scanning... {status.Candidates} candidates ({(status.Progress * 100).ToString("F0", CultureInfo.InvariantCulture)}%)";
                    await Task.Delay(120);
                }

                _currentDiscoveryScan = await _discoveryClient.FetchScanAsync(scanId, 900, 30000);

                var gameDir = await GetGameDirectoryAsync() ?? "";
                _discoveryKb = DiscoveryKnowledgeBaseStore.Load(gameDir);

                _currentDiscoveryRanking = null;
                RefreshDiscoveryRanking();
                DiscoveryProgressBar.Value = 1;
                DiscoveryStatusText.Text = $"Scan complete: {_currentDiscoveryScan.Candidates.Count} candidates (scanId={_currentDiscoveryScan.ScanId})";
            }
            catch (Exception ex)
            {
                DiscoveryStatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                DiscoveryScanButton.IsEnabled = _isConnected;
            }
        }

        private void DiscoveryConcept_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                RefreshDiscoveryRanking();
            }
            catch
            {
            }
        }

        private async void DiscoveryConfirm_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var row = DiscoveryCandidatesListView.SelectedItem as DiscoveryRow;
                if (row == null || string.IsNullOrWhiteSpace(row.Fingerprint))
                {
                    DiscoveryStatusText.Text = "Select a candidate";
                    return;
                }

                var gameDir = await GetGameDirectoryAsync() ?? "";
                _discoveryKb ??= DiscoveryKnowledgeBaseStore.Load(gameDir);
                _discoveryKb.GameDirectory = gameDir;
                _discoveryKb.GameId = DiscoveryKnowledgeBaseStore.GetGameId(gameDir);

                var mapping = _discoveryKb.ConfirmedMappings.FirstOrDefault(m => m.Concept == row.Concept);
                if (mapping == null)
                {
                    mapping = new DiscoveryKnowledgeMapping { Concept = row.Concept };
                    _discoveryKb.ConfirmedMappings.Add(mapping);
                }

                mapping.Fingerprint = row.Fingerprint;
                var c = _currentDiscoveryScan != null && !string.IsNullOrWhiteSpace(row.Key) && _currentDiscoveryScan.ByKey.TryGetValue(row.Key, out var cand) ? cand : null;
                mapping.ComponentTypeName = c?.ComponentTypeName ?? "";
                mapping.DeclaringTypeName = c?.DeclaringTypeName ?? "";
                mapping.MemberName = c?.MemberName ?? "";
                mapping.MemberTypeName = c?.MemberTypeName ?? "";
                mapping.GameObjectPathHint = c?.GameObjectPath ?? "";
                mapping.Confidence = double.TryParse(row.Score, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
                mapping.ConfirmedUtc = DateTime.UtcNow;

                if (c != null)
                {
                    ApplyTokenLearning(_discoveryKb, row.Concept, c, 0.35);
                    TrimTokenWeights(_discoveryKb);
                }

                DiscoveryKnowledgeBaseStore.Save(_discoveryKb);
                RefreshDiscoveryRanking();
                DiscoveryStatusText.Text = $"Confirmed {row.Concept} => {row.Member}";
            }
            catch (Exception ex)
            {
                DiscoveryStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void DiscoveryBeginExperiment_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    DiscoveryStatusText.Text = "Not connected";
                    return;
                }
                if (_currentDiscoveryScan == null)
                {
                    DiscoveryStatusText.Text = "Run Scan first";
                    return;
                }

                var keys = DiscoveryCandidatesListView.SelectedItems.Cast<object>()
                    .OfType<DiscoveryRow>()
                    .Select(r => r.Key)
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (keys.Count == 0)
                {
                    var concept = GetSelectedDiscoveryConcept();
                    var ranking = _currentDiscoveryRanking ?? _discoveryEngine.RankCandidates(_currentDiscoveryScan, concept, _currentDiscoverySignatures, _currentDiscoveryCurrentToMax, _discoveryKb, 200);
                    keys = ranking.Select(r => r.Candidate.Key).Where(k => !string.IsNullOrWhiteSpace(k)).Take(160).ToList();
                }

                var label = DiscoveryExperimentLabelBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(label))
                    label = "experiment";

                var begin = await _discoveryClient.BeginExperimentAsync(_currentDiscoveryScan.ScanId, label, keys, 20000);
                _currentExperimentId = begin.ExperimentId;
                DiscoveryEndExperimentButton.IsEnabled = true;
                DiscoveryStatusText.Text = $"Experiment started: {label} (expId={_currentExperimentId}, keys={begin.BeforeValues.Count})";
            }
            catch (Exception ex)
            {
                DiscoveryStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void DiscoveryEndExperiment_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentExperimentId <= 0)
                {
                    DiscoveryStatusText.Text = "No active experiment";
                    return;
                }

                var concept = GetSelectedDiscoveryConcept();
                var end = await _discoveryClient.EndExperimentAsync(_currentExperimentId, 25000);
                _currentExperimentId = 0;
                DiscoveryEndExperimentButton.IsEnabled = false;

                var baseRanking = _currentDiscoveryRanking ?? _discoveryEngine.RankCandidates(_currentDiscoveryScan, concept, _currentDiscoverySignatures, _currentDiscoveryCurrentToMax, _discoveryKb, 250);
                _currentDiscoveryRanking = _discoveryEngine.ApplyExperimentEvidence(_currentDiscoveryScan, concept, baseRanking, end, 250);
                RefreshDiscoveryRanking();

                await AppendExperimentToKnowledgeBaseAsync(end);
                DiscoveryStatusText.Text = $"Experiment ended: {end.Label} (changed={end.Changed}, unchanged={end.Unchanged})";
            }
            catch (Exception ex)
            {
                DiscoveryStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void DiscoveryFreezeCheat_Click(object sender, RoutedEventArgs e)
        {
            await BindDiscoveryCheatAsync("freeze", null);
        }

        private async void DiscoveryNoDecreaseCheat_Click(object sender, RoutedEventArgs e)
        {
            await BindDiscoveryCheatAsync("nodecrease", null);
        }

        private async void DiscoveryAutoFillCheat_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentDiscoveryScan == null)
                {
                    DiscoveryStatusText.Text = "Run Scan first";
                    return;
                }

                var row = DiscoveryCandidatesListView.SelectedItem as DiscoveryRow;
                if (row == null)
                {
                    DiscoveryStatusText.Text = "Select a candidate";
                    return;
                }

                var maxConcept = row.Concept switch
                {
                    DiscoveryConcept.HealthCurrent => DiscoveryConcept.HealthMax,
                    DiscoveryConcept.AmmoCurrent => DiscoveryConcept.AmmoMax,
                    DiscoveryConcept.StaminaCurrent => DiscoveryConcept.StaminaMax,
                    _ => (DiscoveryConcept?)null
                };

                DiscoveryCandidate max = null;
                if (maxConcept.HasValue)
                {
                    var maxRank = _discoveryEngine.RankCandidates(_currentDiscoveryScan, maxConcept.Value, _currentDiscoverySignatures, _currentDiscoveryCurrentToMax, _discoveryKb, 40);
                    max = maxRank.Select(r => r.Candidate).FirstOrDefault(c => c != null && c.Key != row.Key);
                }

                if (max == null)
                {
                    DiscoveryStatusText.Text = "No max candidate found for autofill";
                    return;
                }

                var args = _discoveryEngine.BuildCheatArgs(row.Concept, _currentDiscoveryScan.ByKey[row.Key], max);
                await BindDiscoveryCheatAsync("autofill", args);
            }
            catch (Exception ex)
            {
                DiscoveryStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void DiscoveryCheatList_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    DiscoveryStatusText.Text = "Not connected";
                    return;
                }
                var list = await _discoveryClient.ListCheatsAsync(12000);
                DiscoveryStatusText.Text = $"Active cheats: {list.Cheats.Count}";
            }
            catch (Exception ex)
            {
                DiscoveryStatusText.Text = $"Error: {ex.Message}";
            }
        }

        public void StartAutoDiscoveryFromHotkey()
        {
            Dispatcher.Invoke(() =>
            {
                if (_autoDiscoveryRunning)
                    return;
                DiscoveryAuto_Click(this, new RoutedEventArgs());
            });
        }

        public void SetAutoDiscoveryHotkeyText(string hotkeyText)
        {
            try
            {
                if (DiscoveryHotkeyBox != null)
                    DiscoveryHotkeyBox.Text = hotkeyText ?? "";
            }
            catch
            {
            }
        }

        private async void DiscoveryAuto_Click(object sender, RoutedEventArgs e)
        {
            if (_autoDiscoveryRunning)
                return;

            _autoDiscoveryCts?.Cancel();
            _autoDiscoveryCts = new CancellationTokenSource();
            _autoDiscoveryRunning = true;

            DiscoveryAutoButton.IsEnabled = false;
            DiscoveryAutoCancelButton.IsEnabled = true;
            DiscoveryProgressBar.Value = 0;

            try
            {
                await RunAutoDiscoveryAsync(_autoDiscoveryCts.Token);
            }
            catch (OperationCanceledException)
            {
                DiscoveryStatusText.Text = "Auto discovery canceled";
            }
            catch (Exception ex)
            {
                DiscoveryStatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                _autoDiscoveryRunning = false;
                DiscoveryAutoButton.IsEnabled = _isConnected;
                DiscoveryAutoCancelButton.IsEnabled = false;
            }
        }

        private void DiscoveryAutoCancel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _autoDiscoveryCts?.Cancel();
            }
            catch
            {
            }
        }

        private void DiscoveryHotkeyApply_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var hk = DiscoveryHotkeyBox.Text?.Trim() ?? "";
                var w = Window.GetWindow(this) as MainWindow;
                if (w != null)
                {
                    w.SetAutoDiscoveryHotkey(hk);
                    DiscoveryStatusText.Text = $"Hotkey set: {hk}";
                }
            }
            catch (Exception ex)
            {
                DiscoveryStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async Task RunAutoDiscoveryAsync(CancellationToken ct)
        {
            if (!IPCMeloaderClient.IsConnected)
                throw new InvalidOperationException("Not connected");

            DiscoveryStatusText.Text = "Auto discovery: scan";
            DiscoveryProgressBar.Value = 0;

            _currentExperimentId = 0;
            DiscoveryEndExperimentButton.IsEnabled = false;
            _currentObserveSessionId = 0;
            _observeLastSeq = 0;
            _observeSamples.Clear();
            _observeEvents.Clear();
            _currentDiscoverySignatures = null;
            _currentDiscoveryCurrentToMax = null;
            _currentDiscoveryRelations = null;
            DiscoveryPullAnalyzeButton.IsEnabled = false;
            DiscoveryStopObserveButton.IsEnabled = false;
            DiscoveryMarkEventButton.IsEnabled = false;
            if (DiscoverySuggestionText != null)
                DiscoverySuggestionText.Text = "";

            var scanId = await _discoveryClient.StartScanAsync(12000, "numeric,bool,count,enum", "mono", 2, 30000);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var st = await _discoveryClient.GetScanStatusAsync(scanId, 12000);
                DiscoveryProgressBar.Value = Math.Max(0, Math.Min(1, st.Progress));
                DiscoveryStatusText.Text = $"Auto discovery: scan ({(st.Progress * 100).ToString("F0", CultureInfo.InvariantCulture)}%)";
                if (st.Done || (!st.Running && st.Progress >= 1))
                    break;
                await Task.Delay(120, ct);
            }

            _currentDiscoveryScan = await _discoveryClient.FetchScanAsync(scanId, 900, 30000);

            var gameDir = await GetGameDirectoryAsync() ?? "";
            _discoveryKb = DiscoveryKnowledgeBaseStore.Load(gameDir);

            ct.ThrowIfCancellationRequested();

            DiscoveryStatusText.Text = "Auto discovery: observe";
            DiscoveryProgressBar.Value = 0;

            var concepts = new[]
            {
                DiscoveryConcept.HealthCurrent,
                DiscoveryConcept.HealthMax,
                DiscoveryConcept.AmmoCurrent,
                DiscoveryConcept.AmmoMax,
                DiscoveryConcept.StaminaCurrent,
                DiscoveryConcept.StaminaMax,
                DiscoveryConcept.Currency,
                DiscoveryConcept.Cooldown,
                DiscoveryConcept.InventoryCount
            };

            var keySet = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < concepts.Length; i++)
            {
                var r = _discoveryEngine.RankCandidates(_currentDiscoveryScan, concepts[i], null, null, _discoveryKb, 70);
                for (var j = 0; j < r.Count; j++)
                {
                    var k = r[j]?.Candidate?.Key;
                    if (!string.IsNullOrWhiteSpace(k))
                        keySet.Add(k);
                    if (keySet.Count >= 1200)
                        break;
                }
                if (keySet.Count >= 1200)
                    break;
            }

            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["mode"] = "summary",
                ["perTickKeys"] = "500"
            };

            var sessionId = await _discoveryClient.StartObservationAsync(_currentDiscoveryScan.ScanId, 100, 350, options, keySet, 20000);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var st = await _discoveryClient.GetObservationStatusAsync(sessionId, 12000);
                DiscoveryProgressBar.Value = Math.Max(0, Math.Min(1, st.Progress));
                DiscoveryStatusText.Text = $"Auto discovery: observe ({(st.Progress * 100).ToString("F0", CultureInfo.InvariantCulture)}%)";
                if (st.Done || (!st.Running && st.Progress >= 1))
                    break;
                await Task.Delay(150, ct);
            }

            var summary = await _discoveryClient.PullObservationSummaryAsync(sessionId, 20000);
            try { await _discoveryClient.StopObservationAsync(sessionId, 12000); } catch { }

            ct.ThrowIfCancellationRequested();

            var sig = new Dictionary<string, DiscoveryBehaviorSignature>(StringComparer.Ordinal);
            for (var i = 0; i < summary.Entries.Count; i++)
            {
                var e = summary.Entries[i];
                if (e == null || string.IsNullOrWhiteSpace(e.Key))
                    continue;
                if (!_currentDiscoveryScan.ByKey.TryGetValue(e.Key, out var c) || c == null || string.IsNullOrWhiteSpace(c.Fingerprint))
                    continue;
                sig[c.Fingerprint] = new DiscoveryBehaviorSignature
                {
                    Fingerprint = c.Fingerprint,
                    Kind = c.Kind,
                    Min = e.Min,
                    Max = e.Max,
                    Mean = e.Mean,
                    Variance = e.Variance,
                    UpdateRate = e.UpdateRate,
                    MostlyInteger = e.IntegerRate >= 0.80,
                    UpdatedUtc = DateTime.UtcNow
                };
            }

            _currentDiscoverySignatures = sig;
            _currentDiscoveryCurrentToMax = _discoveryEngine.InferMaxRelationshipsFromSignatures(_currentDiscoveryScan, _currentDiscoverySignatures, 70);
            _currentDiscoveryRelations = null;

            await PersistBehaviorAnalysisToKnowledgeBaseAsync();

            DiscoveryStatusText.Text = "Auto discovery: rank + save";
            DiscoveryProgressBar.Value = 0.85;

            await AutoConfirmTopMappingsAsync();

            if (DiscoveryAutoApplyCheatsBox.IsChecked == true)
                await AutoBindCheatsAsync();

            _currentDiscoveryRanking = null;
            RefreshDiscoveryRanking();
            DiscoveryProgressBar.Value = 1;
            DiscoveryStatusText.Text = "Auto discovery complete";
        }

        private async Task AutoConfirmTopMappingsAsync()
        {
            var gameDir = await GetGameDirectoryAsync() ?? "";
            _discoveryKb ??= DiscoveryKnowledgeBaseStore.Load(gameDir);
            _discoveryKb.GameDirectory = gameDir;
            _discoveryKb.GameId = DiscoveryKnowledgeBaseStore.GetGameId(gameDir);

            var concepts = new[]
            {
                DiscoveryConcept.HealthCurrent,
                DiscoveryConcept.HealthMax,
                DiscoveryConcept.AmmoCurrent,
                DiscoveryConcept.AmmoMax,
                DiscoveryConcept.StaminaCurrent,
                DiscoveryConcept.StaminaMax,
                DiscoveryConcept.Currency,
                DiscoveryConcept.Cooldown,
                DiscoveryConcept.InventoryCount
            };

            for (var i = 0; i < concepts.Length; i++)
            {
                var concept = concepts[i];
                var rank = _discoveryEngine.RankCandidates(_currentDiscoveryScan, concept, _currentDiscoverySignatures, _currentDiscoveryCurrentToMax, _discoveryKb, 60);
                var best = rank.Count > 0 ? rank[0] : null;
                var c = best?.Candidate;
                if (c == null || string.IsNullOrWhiteSpace(c.Fingerprint))
                    continue;
                if (best.Score < 0.72)
                    continue;

                var mapping = _discoveryKb.ConfirmedMappings.FirstOrDefault(m => m.Concept == concept);
                if (mapping == null)
                {
                    mapping = new DiscoveryKnowledgeMapping { Concept = concept };
                    _discoveryKb.ConfirmedMappings.Add(mapping);
                }

                mapping.Fingerprint = c.Fingerprint;
                mapping.ComponentTypeName = c.ComponentTypeName ?? "";
                mapping.DeclaringTypeName = c.DeclaringTypeName ?? "";
                mapping.MemberName = c.MemberName ?? "";
                mapping.MemberTypeName = c.MemberTypeName ?? "";
                mapping.GameObjectPathHint = c.GameObjectPath ?? "";
                mapping.Confidence = best.Score;
                mapping.ConfirmedUtc = DateTime.UtcNow;
            }

            DiscoveryKnowledgeBaseStore.Save(_discoveryKb);
        }

        private async Task AutoBindCheatsAsync()
        {
            if (_currentDiscoveryScan == null || _discoveryKb?.ConfirmedMappings == null)
                return;

            var scanId = _currentDiscoveryScan.ScanId;
            var map = _discoveryKb.ConfirmedMappings
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Fingerprint))
                .ToDictionary(m => m.Concept, m => m);

            async Task Bind(DiscoveryConcept concept, string mode, IReadOnlyDictionary<string, string> args)
            {
                if (!map.TryGetValue(concept, out var m))
                    return;
                if (!_currentDiscoveryScan.ByFingerprint.TryGetValue(m.Fingerprint, out var cand))
                    return;
                await _discoveryClient.BindCheatAsync(scanId, cand.Key, mode, args, 20000);
            }

            await Bind(DiscoveryConcept.Currency, "nodecrease", null);
            await Bind(DiscoveryConcept.Cooldown, "clampmax", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["value"] = "0" });

            if (map.TryGetValue(DiscoveryConcept.HealthCurrent, out var hc))
            {
                if (_currentDiscoveryScan.ByFingerprint.TryGetValue(hc.Fingerprint, out var cand))
                    await _discoveryClient.BindCheatAsync(scanId, cand.Key, "nodecrease", null, 20000);
            }

            if (map.TryGetValue(DiscoveryConcept.AmmoCurrent, out var ac))
            {
                if (_currentDiscoveryScan.ByFingerprint.TryGetValue(ac.Fingerprint, out var cand))
                {
                    var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (_currentDiscoveryCurrentToMax != null && _currentDiscoveryCurrentToMax.TryGetValue(cand.Key, out var maxKey))
                        args["maxKey"] = maxKey;
                    args["threshold"] = "0.95";
                    await _discoveryClient.BindCheatAsync(scanId, cand.Key, "autofill", args, 20000);
                }
            }

            if (map.TryGetValue(DiscoveryConcept.StaminaCurrent, out var sc))
            {
                if (_currentDiscoveryScan.ByFingerprint.TryGetValue(sc.Fingerprint, out var cand))
                {
                    var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (_currentDiscoveryCurrentToMax != null && _currentDiscoveryCurrentToMax.TryGetValue(cand.Key, out var maxKey))
                        args["maxKey"] = maxKey;
                    args["threshold"] = "0.95";
                    await _discoveryClient.BindCheatAsync(scanId, cand.Key, "autofill", args, 20000);
                }
            }
        }

        private async void DiscoveryStartObserve_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    DiscoveryStatusText.Text = "Not connected";
                    return;
                }
                if (_currentDiscoveryScan == null)
                {
                    DiscoveryStatusText.Text = "Run Scan first";
                    return;
                }

                var keys = DiscoveryCandidatesListView.SelectedItems.Cast<object>()
                    .OfType<DiscoveryRow>()
                    .Select(r => r.Key)
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (keys.Count == 0)
                {
                    var concept = GetSelectedDiscoveryConcept();
                    var ranking = _currentDiscoveryRanking ?? _discoveryEngine.RankCandidates(_currentDiscoveryScan, concept, _currentDiscoverySignatures, _currentDiscoveryCurrentToMax, _discoveryKb, 250);
                    keys = ranking.Select(r => r.Candidate.Key).Where(k => !string.IsNullOrWhiteSpace(k)).Take(300).ToList();
                }

                _observeSamples.Clear();
                _observeEvents.Clear();
                _observeLastSeq = 0;
                _currentObserveSessionId = await _discoveryClient.StartObservationAsync(_currentDiscoveryScan.ScanId, 100, 2500, keys, 20000);

                DiscoveryPullAnalyzeButton.IsEnabled = true;
                DiscoveryStopObserveButton.IsEnabled = true;
                DiscoveryMarkEventButton.IsEnabled = true;
                DiscoveryStatusText.Text = $"Observation started: sessionId={_currentObserveSessionId}, keys={keys.Count}";
            }
            catch (Exception ex)
            {
                DiscoveryStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void DiscoveryPullAnalyze_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentObserveSessionId <= 0)
                {
                    DiscoveryStatusText.Text = "No active observation";
                    return;
                }
                var pull = await _discoveryClient.PullObservationAsync(_currentObserveSessionId, _observeLastSeq, 800, 20000);
                if (pull.Events.Count > 0)
                {
                    _observeEvents.AddRange(pull.Events);
                    if (_observeEvents.Count > 2000)
                        _observeEvents.RemoveRange(0, _observeEvents.Count - 2000);
                }
                if (pull.Samples.Count > 0)
                {
                    _observeLastSeq = Math.Max(_observeLastSeq, pull.LastSeq);
                    _observeSamples.AddRange(pull.Samples.OrderBy(s => s.Seq));
                    if (_observeSamples.Count > 5000)
                        _observeSamples.RemoveRange(0, _observeSamples.Count - 5000);
                }

                _currentDiscoverySignatures = _discoveryEngine.ComputeBehaviorSignatures(_currentDiscoveryScan, _observeSamples);
                _currentDiscoveryCurrentToMax = _discoveryEngine.InferMaxRelationships(_currentDiscoveryScan, _observeSamples, _currentDiscoverySignatures);
                _currentDiscoveryRelations = _discoveryEngine.BuildRelationGraph(_currentDiscoveryScan, _observeSamples, _currentDiscoverySignatures, _currentDiscoveryCurrentToMax);

                await PersistBehaviorAnalysisToKnowledgeBaseAsync();
                var concept = GetSelectedDiscoveryConcept();
                var baseRanking = _discoveryEngine.RankCandidates(_currentDiscoveryScan, concept, _currentDiscoverySignatures, _currentDiscoveryCurrentToMax, _discoveryKb, 250);
                _currentDiscoveryRanking = _discoveryEngine.ApplyEventCausalEvidence(_currentDiscoveryScan, concept, baseRanking, _observeSamples, _observeEvents, 250);
                var lastEventName = TryGetLastObservedEventName(_observeEvents, out var en) ? en : null;
                await AppendEventCausalPriorsToKnowledgeBaseAsync(_currentDiscoveryScan, concept, lastEventName, baseRanking, _currentDiscoveryRanking);
                RefreshDiscoveryRanking();

                DiscoveryStatusText.Text = $"Pulled {pull.Samples.Count} samples, events={pull.Events.Count}, signatures={_currentDiscoverySignatures.Count}, relations={_currentDiscoveryRelations.Count}";
            }
            catch (Exception ex)
            {
                DiscoveryStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void DiscoveryStopObserve_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentObserveSessionId <= 0)
                {
                    DiscoveryStatusText.Text = "No active observation";
                    return;
                }
                var ok = await _discoveryClient.StopObservationAsync(_currentObserveSessionId, 20000);
                DiscoveryStatusText.Text = ok ? $"Observation stopped (sessionId={_currentObserveSessionId})" : "Observation stop failed";
            }
            catch (Exception ex)
            {
                DiscoveryStatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                _currentObserveSessionId = 0;
                DiscoveryPullAnalyzeButton.IsEnabled = false;
                DiscoveryStopObserveButton.IsEnabled = false;
                DiscoveryMarkEventButton.IsEnabled = false;
            }
        }

        private async void DiscoveryMarkEvent_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentObserveSessionId <= 0)
                {
                    DiscoveryStatusText.Text = "No active observation";
                    return;
                }
                var name = DiscoveryEventBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    name = DiscoveryExperimentLabelBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    name = "event";
                var ok = await _discoveryClient.MarkEventAsync(_currentObserveSessionId, name, 12000);
                DiscoveryStatusText.Text = ok ? $"Event marked: {name}" : "Event mark failed";
            }
            catch (Exception ex)
            {
                DiscoveryStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private void DiscoverySuggestExperiment_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentDiscoveryScan == null)
                {
                    DiscoveryStatusText.Text = "Run Scan first";
                    return;
                }

                var concept = GetSelectedDiscoveryConcept();
                var ranking = _currentDiscoveryRanking;
                if (ranking == null || ranking.Count == 0 || ranking[0].Concept != concept)
                    ranking = _discoveryEngine.RankCandidates(_currentDiscoveryScan, concept, _currentDiscoverySignatures, _currentDiscoveryCurrentToMax, _discoveryKb, 250);

                var suggestion = _discoveryEngine.SuggestNextExperiment(concept, ranking);
                if (suggestion == null)
                {
                    DiscoverySuggestionText.Text = "No suggestion";
                    return;
                }

                DiscoverySuggestionText.Text = suggestion.Prompt ?? "";
                if (!string.IsNullOrWhiteSpace(suggestion.Label))
                    DiscoveryExperimentLabelBox.Text = suggestion.Label;
                if (!string.IsNullOrWhiteSpace(suggestion.EventName))
                    DiscoveryEventBox.Text = suggestion.EventName;
            }
            catch (Exception ex)
            {
                DiscoveryStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void DiscoveryRunDamageTest_Click(object sender, RoutedEventArgs e)
        {
            await RunGuidedTestAsync(GuidedTestKind.Damage);
        }

        private async void DiscoveryRunAmmoTest_Click(object sender, RoutedEventArgs e)
        {
            await RunGuidedTestAsync(GuidedTestKind.Ammo);
        }

        private async void DiscoveryRunSprintTest_Click(object sender, RoutedEventArgs e)
        {
            await RunGuidedTestAsync(GuidedTestKind.Sprint);
        }

        private async void DiscoveryRunBuyTest_Click(object sender, RoutedEventArgs e)
        {
            await RunGuidedTestAsync(GuidedTestKind.Buy);
        }

        private async void DiscoveryRunCooldownTest_Click(object sender, RoutedEventArgs e)
        {
            await RunGuidedTestAsync(GuidedTestKind.Cooldown);
        }

        private async Task RunGuidedTestAsync(GuidedTestKind kind)
        {
            if (!IPCMeloaderClient.IsConnected)
            {
                DiscoveryStatusText.Text = "Not connected";
                return;
            }
            if (_currentDiscoveryScan == null)
            {
                DiscoveryStatusText.Text = "Run Scan first";
                return;
            }

            var concept = kind switch
            {
                GuidedTestKind.Damage => DiscoveryConcept.HealthCurrent,
                GuidedTestKind.Ammo => DiscoveryConcept.AmmoCurrent,
                GuidedTestKind.Sprint => DiscoveryConcept.StaminaCurrent,
                GuidedTestKind.Buy => DiscoveryConcept.Currency,
                GuidedTestKind.Cooldown => DiscoveryConcept.Cooldown,
                _ => DiscoveryConcept.HealthCurrent
            };

            var label = kind switch
            {
                GuidedTestKind.Damage => "damage_test",
                GuidedTestKind.Ammo => "ammo_test",
                GuidedTestKind.Sprint => "sprint_test",
                GuidedTestKind.Buy => "buy_test",
                GuidedTestKind.Cooldown => "cooldown_test",
                _ => "event"
            };

            var seconds = kind switch
            {
                GuidedTestKind.Damage => 8,
                GuidedTestKind.Ammo => 8,
                GuidedTestKind.Sprint => 8,
                GuidedTestKind.Buy => 10,
                GuidedTestKind.Cooldown => 12,
                _ => 8
            };

            if (DiscoveryConceptBox != null)
                DiscoveryConceptBox.SelectedItem = concept;
            if (DiscoveryExperimentLabelBox != null)
                DiscoveryExperimentLabelBox.Text = label;
            if (DiscoveryEventBox != null)
                DiscoveryEventBox.Text = label;
            if (DiscoverySuggestionText != null)
                DiscoverySuggestionText.Text = "";

            if (_currentObserveSessionId > 0)
            {
                try { await _discoveryClient.StopObservationAsync(_currentObserveSessionId, 20000); } catch { }
                _currentObserveSessionId = 0;
            }

            var ranking = _discoveryEngine.RankCandidates(_currentDiscoveryScan, concept, _currentDiscoverySignatures, _currentDiscoveryCurrentToMax, _discoveryKb, 320);
            var keys = ranking.Select(r => r?.Candidate?.Key).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).Take(300).ToList();
            if (keys.Count == 0)
            {
                DiscoveryStatusText.Text = "No candidates to observe";
                return;
            }

            _observeSamples.Clear();
            _observeEvents.Clear();
            _observeLastSeq = 0;

            DiscoveryStartObserveButton.IsEnabled = false;
            DiscoveryPullAnalyzeButton.IsEnabled = false;
            DiscoveryStopObserveButton.IsEnabled = true;
            DiscoveryMarkEventButton.IsEnabled = false;

            var startedSessionId = 0L;
            try
            {
                startedSessionId = await _discoveryClient.StartObservationAsync(_currentDiscoveryScan.ScanId, 100, 1800, keys, 20000);
                _currentObserveSessionId = startedSessionId;
                DiscoveryStatusText.Text = $"{label}: preparing...";
                await Task.Delay(800);

                await _discoveryClient.MarkEventAsync(_currentObserveSessionId, label, 12000);
                for (var i = seconds; i >= 1; i--)
                {
                    DiscoveryStatusText.Text = $"{label}: do it now ({i}s)";
                    await Task.Delay(1000);
                }

                var pull = await _discoveryClient.PullObservationAsync(_currentObserveSessionId, 0, 800, 20000);
                if (pull.Events.Count > 0)
                    _observeEvents.AddRange(pull.Events);
                if (pull.Samples.Count > 0)
                    _observeSamples.AddRange(pull.Samples.OrderBy(s => s.Seq));

                _currentDiscoverySignatures = _discoveryEngine.ComputeBehaviorSignatures(_currentDiscoveryScan, _observeSamples);
                _currentDiscoveryCurrentToMax = _discoveryEngine.InferMaxRelationships(_currentDiscoveryScan, _observeSamples, _currentDiscoverySignatures);
                _currentDiscoveryRelations = _discoveryEngine.BuildRelationGraph(_currentDiscoveryScan, _observeSamples, _currentDiscoverySignatures, _currentDiscoveryCurrentToMax);

                await PersistBehaviorAnalysisToKnowledgeBaseAsync();

                var baseRanking = _discoveryEngine.RankCandidates(_currentDiscoveryScan, concept, _currentDiscoverySignatures, _currentDiscoveryCurrentToMax, _discoveryKb, 250);
                _currentDiscoveryRanking = _discoveryEngine.ApplyEventCausalEvidence(_currentDiscoveryScan, concept, baseRanking, _observeSamples, _observeEvents, 250);
                await AppendEventCausalPriorsToKnowledgeBaseAsync(_currentDiscoveryScan, concept, label, baseRanking, _currentDiscoveryRanking);
                RefreshDiscoveryRanking();

                DiscoveryStatusText.Text = $"{label}: done (samples={_observeSamples.Count}, events={_observeEvents.Count})";
            }
            catch (Exception ex)
            {
                DiscoveryStatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                if (startedSessionId > 0)
                {
                    try { await _discoveryClient.StopObservationAsync(startedSessionId, 20000); } catch { }
                }
                _currentObserveSessionId = 0;
                DiscoveryStartObserveButton.IsEnabled = _isConnected;
                DiscoveryPullAnalyzeButton.IsEnabled = false;
                DiscoveryStopObserveButton.IsEnabled = false;
                DiscoveryMarkEventButton.IsEnabled = false;
            }
        }

        private static bool TryGetLastObservedEventName(IReadOnlyList<string> events, out string name)
        {
            name = null;
            if (events == null || events.Count == 0)
                return false;

            for (var i = events.Count - 1; i >= 0; i--)
            {
                var e = events[i];
                if (string.IsNullOrWhiteSpace(e))
                    continue;
                var segs = e.Split(';', StringSplitOptions.RemoveEmptyEntries);
                for (var s = 0; s < segs.Length; s++)
                {
                    var seg = segs[s];
                    var eq = seg.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    var k = seg.Substring(0, eq);
                    var v = seg.Substring(eq + 1);
                    if (k.Equals("name", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v))
                    {
                        name = v;
                        return true;
                    }
                }
            }
            return false;
        }

        private async Task AppendEventCausalPriorsToKnowledgeBaseAsync(
            DiscoveryScan scan,
            DiscoveryConcept concept,
            string eventName,
            IReadOnlyList<DiscoveryCandidateScore> baseRanking,
            IReadOnlyList<DiscoveryCandidateScore> adjustedRanking)
        {
            try
            {
                if (scan == null)
                    return;
                if (string.IsNullOrWhiteSpace(eventName))
                    return;
                if (baseRanking == null || baseRanking.Count == 0 || adjustedRanking == null || adjustedRanking.Count == 0)
                    return;

                var baseByKey = new Dictionary<string, double>(StringComparer.Ordinal);
                for (var i = 0; i < baseRanking.Count; i++)
                {
                    var r = baseRanking[i];
                    var key = r?.Candidate?.Key;
                    if (string.IsNullOrWhiteSpace(key))
                        continue;
                    baseByKey[key] = r.Score;
                }

                var diffs = new List<(string Fingerprint, double Bonus)>();
                for (var i = 0; i < adjustedRanking.Count; i++)
                {
                    var r = adjustedRanking[i];
                    var cand = r?.Candidate;
                    if (cand == null || string.IsNullOrWhiteSpace(cand.Key) || string.IsNullOrWhiteSpace(cand.Fingerprint))
                        continue;
                    if (!baseByKey.TryGetValue(cand.Key, out var baseScore))
                        continue;
                    var bonus = r.Score - baseScore;
                    if (bonus <= 0.03)
                        continue;
                    diffs.Add((cand.Fingerprint, bonus));
                }

                if (diffs.Count == 0)
                    return;

                diffs = diffs
                    .OrderByDescending(d => d.Bonus)
                    .Take(25)
                    .ToList();

                var gameDir = await GetGameDirectoryAsync() ?? "";
                _discoveryKb ??= DiscoveryKnowledgeBaseStore.Load(gameDir);
                _discoveryKb.GameDirectory = gameDir;
                _discoveryKb.GameId = DiscoveryKnowledgeBaseStore.GetGameId(gameDir);

                var now = DateTime.UtcNow;
                _discoveryKb.CausalPriors ??= new List<DiscoveryKnowledgeCausalPrior>();
                _discoveryKb.CausalPriors.RemoveAll(p => p == null || (now - p.UpdatedUtc).TotalDays > 180);

                for (var i = 0; i < diffs.Count; i++)
                {
                    var (fp, bonus) = diffs[i];
                    var strength = Math.Max(0, Math.Min(1.0, bonus / 0.30));

                    var existing = _discoveryKb.CausalPriors.FirstOrDefault(p =>
                        p != null &&
                        p.Concept == concept &&
                        string.Equals(p.Fingerprint, fp, StringComparison.Ordinal) &&
                        string.Equals(p.EventName ?? "", eventName, StringComparison.OrdinalIgnoreCase));

                    if (existing == null)
                    {
                        _discoveryKb.CausalPriors.Add(new DiscoveryKnowledgeCausalPrior
                        {
                            Concept = concept,
                            EventName = eventName,
                            Fingerprint = fp,
                            Strength = strength,
                            UpdatedUtc = now
                        });
                    }
                    else
                    {
                        existing.Strength = Math.Max(existing.Strength, strength);
                        existing.UpdatedUtc = now;
                    }
                }

                if (_discoveryKb.CausalPriors.Count > 1200)
                    _discoveryKb.CausalPriors = _discoveryKb.CausalPriors.OrderByDescending(p => p.UpdatedUtc).Take(1200).ToList();

                DiscoveryKnowledgeBaseStore.Save(_discoveryKb);
            }
            catch
            {
            }
        }

        private async void DynamicSaveProfile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var gameDir = await GetGameDirectoryAsync() ?? "";
                _discoveryKb ??= DiscoveryKnowledgeBaseStore.Load(gameDir);
                _discoveryKb.GameDirectory = gameDir;
                _discoveryKb.GameId = DiscoveryKnowledgeBaseStore.GetGameId(gameDir);

                _discoveryKb.GameGenre = DynamicGenreBox?.SelectedItem?.ToString() ?? _discoveryKb.GameGenre;
                _discoveryKb.GameVersionLabel = DynamicVersionBox?.Text?.Trim();
                DiscoveryKnowledgeBaseStore.Save(_discoveryKb);
                DynamicStatusText.Text = "Saved game profile";
            }
            catch (Exception ex)
            {
                DynamicStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void DynamicRefresh_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDynamicCheatsAsync();
        }

        private void DynamicScanPassesBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var selected = DynamicScanPassesBox?.SelectedItem?.ToString() ?? "";
                if (int.TryParse(selected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                    _dynamicScanPasses = Math.Max(1, Math.Min(4, p));
                else
                    _dynamicScanPasses = 4;
                _currentDynamicScan = null;
                _dynamicScanIds = null;
                _dynamicScans = null;
                _currentDynamicSignatures = null;
                _currentDynamicCurrentToMax = null;
            }
            catch
            {
            }
        }

        private void DynamicAggressiveScan_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                _dynamicAggressiveScan = DynamicAggressiveScanBox?.IsChecked == true;
                _currentDynamicScan = null;
                _dynamicScanIds = null;
                _dynamicScans = null;
                _currentDynamicSignatures = null;
                _currentDynamicCurrentToMax = null;
            }
            catch
            {
            }
        }

        private async Task RefreshDynamicCheatsAsync()
        {
            var swAll = Stopwatch.StartNew();
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    DynamicStatusText.Text = "Not connected";
                    return;
                }

                DynamicStatusText.Text = "Refreshing...";
                DynamicWarningsText.Text = "";

                await EnsureDynamicScanAsync();

                var gameDir = await GetGameDirectoryAsync() ?? "";
                _discoveryKb ??= DiscoveryKnowledgeBaseStore.Load(gameDir);
                _discoveryKb.GameDirectory = gameDir;
                _discoveryKb.GameId = DiscoveryKnowledgeBaseStore.GetGameId(gameDir);

                if (DynamicGenreBox != null && string.IsNullOrWhiteSpace(_discoveryKb.GameGenre) == false)
                    DynamicGenreBox.SelectedItem = _discoveryKb.GameGenre;
                if (DynamicVersionBox != null && string.IsNullOrWhiteSpace(_discoveryKb.GameVersionLabel) == false)
                    DynamicVersionBox.Text = _discoveryKb.GameVersionLabel;

                var scan = _currentDynamicScan;
                if (scan == null)
                {
                    DynamicStatusText.Text = "No scan";
                    return;
                }

                var sigs = _currentDynamicSignatures;
                if (sigs == null && _discoveryKb?.BehaviorSignatures != null && _discoveryKb.BehaviorSignatures.Count > 0)
                    sigs = _discoveryKb.BehaviorSignatures.Where(s => s != null && !string.IsNullOrWhiteSpace(s.Fingerprint)).ToDictionary(s => s.Fingerprint, s => s, StringComparer.Ordinal);

                var currentToMax = _currentDynamicCurrentToMax;
                if (currentToMax == null && sigs != null && sigs.Count > 0)
                    currentToMax = _discoveryEngine.InferMaxRelationshipsFromSignatures(scan, sigs);

                var active = await _discoveryClient.ListCheatsAsync(12000);
                var activeFp = new HashSet<string>(active.Cheats.Where(c => c != null && !string.IsNullOrWhiteSpace(c.Fingerprint)).Select(c => c.Fingerprint), StringComparer.Ordinal);

                var swRecs = Stopwatch.StartNew();
                _dynamicCheatRows.Clear();
                var concepts = new[]
                {
                    DiscoveryConcept.HealthCurrent,
                    DiscoveryConcept.HealthMax,
                    DiscoveryConcept.ShieldCurrent,
                    DiscoveryConcept.ShieldMax,
                    DiscoveryConcept.AmmoCurrent,
                    DiscoveryConcept.AmmoMax,
                    DiscoveryConcept.StaminaCurrent,
                    DiscoveryConcept.StaminaMax,
                    DiscoveryConcept.Currency,
                    DiscoveryConcept.Score,
                    DiscoveryConcept.Cooldown,
                    DiscoveryConcept.AbilityCharges,
                    DiscoveryConcept.Lives,
                    DiscoveryConcept.InventoryCount
                };

                void PickForConcept(DiscoveryConcept concept, IReadOnlyList<DiscoveryCandidateScore> ranking, out DiscoveryCandidate chosen, out DiscoveryCandidateScore chosenScore, out bool usedKbConfirmed)
                {
                    chosen = null;
                    chosenScore = null;
                    usedKbConfirmed = false;

                    if (ranking == null || ranking.Count == 0)
                        return;

                    if (_discoveryEngine.TryResolveConfirmedMapping(scan, concept, _discoveryKb, out var confirmed) && confirmed != null)
                    {
                        for (var i = 0; i < ranking.Count; i++)
                        {
                            var r = ranking[i];
                            if (r?.Candidate?.Key == null)
                                continue;
                            if (string.Equals(r.Candidate.Key, confirmed.Key, StringComparison.Ordinal))
                            {
                                chosen = r.Candidate;
                                chosenScore = r;
                                usedKbConfirmed = true;
                                return;
                            }
                        }
                    }

                    chosenScore = ranking[0];
                    chosen = chosenScore?.Candidate;
                }

                var probeBudget = 2;
                var probesUsed = 0;

                for (var ci = 0; ci < concepts.Length; ci++)
                {
                    var concept = concepts[ci];
                    var ranking = _discoveryEngine.RankCheatCandidates(scan, concept, sigs, currentToMax, _discoveryKb, 60);

                    PickForConcept(concept, ranking, out var chosen, out var chosenScore, out var usedKbConfirmed);

                    if (chosen != null &&
                        probesUsed < probeBudget &&
                        !usedKbConfirmed &&
                        concept != DiscoveryConcept.InventoryCount &&
                        chosen.CanWrite &&
                        !string.IsNullOrWhiteSpace(chosen.Key) &&
                        !string.IsNullOrWhiteSpace(chosen.Fingerprint) &&
                        !HasRecentWriteProbe(_discoveryKb, concept, chosen.Fingerprint, 7))
                    {
                        var scanIdForProbe = chosen.SourceScanId > 0 ? chosen.SourceScanId : scan.ScanId;
                        try
                        {
                            var probe = await _discoveryClient.WriteProbeAsync(scanIdForProbe, chosen.Key, null, 250, true, 4500);
                            UpsertWriteProbe(_discoveryKb, concept, probe);
                            TrimWriteProbes(_discoveryKb);
                            DiscoveryKnowledgeBaseStore.Save(_discoveryKb);
                            probesUsed++;

                            ranking = _discoveryEngine.RankCheatCandidates(scan, concept, sigs, currentToMax, _discoveryKb, 60);
                            PickForConcept(concept, ranking, out chosen, out chosenScore, out usedKbConfirmed);
                        }
                        catch
                        {
                        }
                    }

                    var mode = _discoveryEngine.BuildCheatMode(concept);
                    IReadOnlyDictionary<string, string> args = null;

                    if (chosen != null)
                    {
                        DiscoveryCandidate maxCandidate = null;
                        if (currentToMax != null && chosenScore != null &&
                            (concept == DiscoveryConcept.AmmoCurrent || concept == DiscoveryConcept.StaminaCurrent || concept == DiscoveryConcept.HealthCurrent || concept == DiscoveryConcept.ShieldCurrent) &&
                            currentToMax.TryGetValue(chosen.Key, out var maxKey) && !string.IsNullOrWhiteSpace(maxKey) &&
                            scan.ByKey.TryGetValue(maxKey, out var tmpMax) && tmpMax != null)
                        {
                            if (tmpMax.SourceScanId == 0 || chosen.SourceScanId == 0 || tmpMax.SourceScanId == chosen.SourceScanId)
                                maxCandidate = tmpMax;
                        }

                        args = _discoveryEngine.BuildCheatArgs(concept, chosen, maxCandidate);
                    }

                    var conf = chosenScore?.Score ?? 0;
                    var confidenceText = conf.ToString("F3", CultureInfo.InvariantCulture);

                    var reason = "";
                    if (chosenScore != null)
                    {
                        reason =
                            $"S={chosenScore.Score.ToString("F3", CultureInfo.InvariantCulture)} " +
                            $"N={chosenScore.NameScore.ToString("F2", CultureInfo.InvariantCulture)} " +
                            $"T={chosenScore.TypeScore.ToString("F2", CultureInfo.InvariantCulture)} " +
                            $"Ctx={chosenScore.ContextScore.ToString("F2", CultureInfo.InvariantCulture)} " +
                            $"Dyn={chosenScore.DynamicScore.ToString("F2", CultureInfo.InvariantCulture)} " +
                            $"Db={chosenScore.DatabaseScore.ToString("F2", CultureInfo.InvariantCulture)} " +
                            $"Kb={chosenScore.KnowledgeScore.ToString("F2", CultureInfo.InvariantCulture)} " +
                            $"Rel={chosenScore.RelationScore.ToString("F2", CultureInfo.InvariantCulture)} " +
                            $"Causal={chosenScore.CausalScore.ToString("F2", CultureInfo.InvariantCulture)}";
                        if (usedKbConfirmed)
                            reason = "KB confirmed | " + reason;
                    }

                    if (chosen != null && TryGetWriteProbe(_discoveryKb, concept, chosen.Fingerprint, out var wp))
                    {
                        var wpLabel = wp.Sticky ? "sticky" : (wp.RubberBand ? "rubber" : (wp.ClampDetected ? "clamp" : "ok"));
                        reason = $"{reason} | WP={wp.Score.ToString("F2", CultureInfo.InvariantCulture)} {wpLabel}";
                    }

                    var alts = "";
                    if (ranking.Count > 1)
                    {
                        var parts = new List<string>(3);
                        for (var i = 0; i < ranking.Count && parts.Count < 3; i++)
                        {
                            var r = ranking[i];
                            var cand = r?.Candidate;
                            if (cand == null || chosen != null && string.Equals(cand.Key, chosen.Key, StringComparison.Ordinal))
                                continue;
                            parts.Add($"{r.Score.ToString("F2", CultureInfo.InvariantCulture)} {cand.DeclaringTypeName}.{cand.MemberName}");
                        }
                        alts = string.Join(" | ", parts);
                    }

                    var applied = "";
                    if (chosen != null && !string.IsNullOrWhiteSpace(chosen.Fingerprint) && activeFp.Contains(chosen.Fingerprint))
                        applied = "Yes";

                    var needTest = chosen == null || conf < 0.66;
                    var testEnabled = needTest && TryMapConceptToGuidedTest(concept, out _);

                    _dynamicCheatRows.Add(new DynamicCheatRow
                    {
                        Category = GetDynamicCategory(concept),
                        Concept = GetDynamicConceptLabel(concept),
                        Confidence = confidenceText,
                        Mode = mode,
                        Member = chosen?.MemberName ?? "",
                        ComponentType = chosen?.ComponentTypeName ?? "",
                        Path = chosen?.GameObjectPath ?? "",
                        Applied = applied,
                        Alternatives = alts,
                        Reason = reason,
                        BindEnabled = chosen != null && !string.IsNullOrWhiteSpace(chosen.Key),
                        TestEnabled = testEnabled,
                        TestLabel = testEnabled ? "Run Test" : "",
                        ScanId = chosen?.SourceScanId ?? 0,
                        Key = chosen?.Key,
                        Fingerprint = chosen?.Fingerprint,
                        ConceptValue = concept,
                        Args = args
                    });
                }
                swRecs.Stop();
                _dynamicLastRefreshMs = swRecs.ElapsedMilliseconds;

                UpdateDynamicPerfText();
                DynamicStatusText.Text = $"Ready: concepts={_dynamicCheatRows.Count}, active={active.Cheats.Count}";
                if ((_currentDynamicSignatures == null || _currentDynamicSignatures.Count == 0) && (sigs?.Count ?? 0) == 0)
                    DynamicWarningsText.Text = "Tip: run Observe/Pull+Analyze for stronger dynamic signals";
            }
            catch (Exception ex)
            {
                DynamicStatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                swAll.Stop();
            }
        }

        private async Task EnsureDynamicScanAsync()
        {
            if (_currentDynamicScan != null && _dynamicScans != null && _dynamicScans.Count > 0)
                return;

            var swScan = Stopwatch.StartNew();
            try
            {
                var passes = Math.Max(1, Math.Min(4, _dynamicScanPasses));
                var scans = new List<DiscoveryScan>(passes);
                var scanIds = new List<long>(passes);

                for (var pass = 0; pass < passes; pass++)
                {
                    var scope = "mono";
                    if (_dynamicAggressiveScan)
                        scope = pass == 0 ? "mono" : "all";

                    var maxCandidates = _dynamicAggressiveScan ? (scope == "all" ? 42000 : 22000) : 14000;
                    var budgetMs = _dynamicAggressiveScan ? 12 : 4;
                    var timeoutMs = _dynamicAggressiveScan ? 70000 : 40000;

                    DynamicStatusText.Text = $"Scanning {pass + 1}/{passes}...";
                    var scanId = await _discoveryClient.StartScanAsync(maxCandidates, "numeric,bool,count,enum", scope, budgetMs, timeoutMs);
                    while (true)
                    {
                        var st = await _discoveryClient.GetScanStatusAsync(scanId, 12000);
                        var pct = (int)Math.Round(Math.Max(0, Math.Min(1, st.Progress)) * 100);
                        var stage = string.IsNullOrWhiteSpace(st.Stage) ? "scan" : st.Stage;
                        DynamicStatusText.Text = $"Scanning {pass + 1}/{passes}: {stage} {pct}% ({st.Candidates})";
                        if (st.Done || (!st.Running && st.Progress >= 1))
                            break;
                        await Task.Delay(140);
                    }

                    var scan = await _discoveryClient.FetchScanAsync(scanId, 900, timeoutMs);
                    scans.Add(scan);
                    scanIds.Add(scanId);
                }

                var primary = scanIds.Count > 0 ? scanIds[scanIds.Count - 1] : 0;
                _currentDynamicScan = DiscoveryScanEnsembler.Merge(scans, primary);
                _dynamicScanIds = scanIds;
                _dynamicScans = scans;
                _currentDynamicSignatures = null;
                _currentDynamicCurrentToMax = null;
            }
            finally
            {
                swScan.Stop();
                _dynamicLastScanMs = swScan.ElapsedMilliseconds;
            }
        }

        private static string GetDynamicConceptLabel(DiscoveryConcept concept)
        {
            return concept switch
            {
                DiscoveryConcept.Currency => "CurrencyCurrent",
                DiscoveryConcept.Cooldown => "CooldownRemaining",
                _ => concept.ToString()
            };
        }

        private static string GetDynamicCategory(DiscoveryConcept concept)
        {
            return concept switch
            {
                DiscoveryConcept.HealthCurrent or DiscoveryConcept.HealthMax or DiscoveryConcept.StaminaCurrent or DiscoveryConcept.StaminaMax or DiscoveryConcept.ShieldCurrent or DiscoveryConcept.ShieldMax or DiscoveryConcept.Lives => "Player",
                DiscoveryConcept.AmmoCurrent or DiscoveryConcept.AmmoMax => "Weapon",
                DiscoveryConcept.Cooldown or DiscoveryConcept.AbilityCharges => "Abilities",
                DiscoveryConcept.Currency or DiscoveryConcept.Score => "Economy",
                DiscoveryConcept.InventoryCount => "Inventory",
                _ => "General"
            };
        }

        private static bool TryMapConceptToGuidedTest(DiscoveryConcept concept, out GuidedTestKind kind)
        {
            kind = GuidedTestKind.Damage;
            switch (concept)
            {
                case DiscoveryConcept.HealthCurrent:
                case DiscoveryConcept.ShieldCurrent:
                case DiscoveryConcept.Lives:
                    kind = GuidedTestKind.Damage;
                    return true;
                case DiscoveryConcept.AmmoCurrent:
                case DiscoveryConcept.AbilityCharges:
                    kind = GuidedTestKind.Ammo;
                    return true;
                case DiscoveryConcept.StaminaCurrent:
                    kind = GuidedTestKind.Sprint;
                    return true;
                case DiscoveryConcept.Currency:
                case DiscoveryConcept.Score:
                    kind = GuidedTestKind.Buy;
                    return true;
                case DiscoveryConcept.Cooldown:
                    kind = GuidedTestKind.Cooldown;
                    return true;
                default:
                    return false;
            }
        }

        private async void DynamicBindRow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    DynamicStatusText.Text = "Not connected";
                    return;
                }

                var btn = sender as Button;
                if (btn?.Tag is not DynamicCheatRow row)
                    return;

                if (row == null || string.IsNullOrWhiteSpace(row.Key))
                    return;

                var scanId = row.ScanId > 0 ? row.ScanId : (_currentDynamicScan?.ScanId ?? 0);
                if (scanId <= 0)
                {
                    DynamicStatusText.Text = "No scan";
                    return;
                }

                await _discoveryClient.BindCheatAsync(scanId, row.Key, row.Mode, row.Args, 20000);
                await AppendDynamicCheatToKnowledgeBaseAsync(row);
                DynamicStatusText.Text = $"Bound: {row.Concept} => {row.Member}";
                await RefreshDynamicCheatsAsync();
            }
            catch (Exception ex)
            {
                DynamicStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void DynamicRunTestRow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    DynamicStatusText.Text = "Not connected";
                    return;
                }

                var btn = sender as Button;
                if (btn?.Tag is not DynamicCheatRow row)
                    return;

                if (!TryMapConceptToGuidedTest(row.ConceptValue, out var kind))
                    return;

                await RunDynamicGuidedTestAsync(kind);
                await RefreshDynamicCheatsAsync();
            }
            catch (Exception ex)
            {
                DynamicStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async Task RunDynamicGuidedTestAsync(GuidedTestKind kind)
        {
            await EnsureDynamicScanAsync();

            var scan = _dynamicScans != null && _dynamicScans.Count > 0 ? _dynamicScans[_dynamicScans.Count - 1] : null;
            if (scan == null || scan.ScanId <= 0)
            {
                DynamicStatusText.Text = "No scan";
                return;
            }

            var concept = kind switch
            {
                GuidedTestKind.Damage => DiscoveryConcept.HealthCurrent,
                GuidedTestKind.Ammo => DiscoveryConcept.AmmoCurrent,
                GuidedTestKind.Sprint => DiscoveryConcept.StaminaCurrent,
                GuidedTestKind.Buy => DiscoveryConcept.Currency,
                GuidedTestKind.Cooldown => DiscoveryConcept.Cooldown,
                _ => DiscoveryConcept.HealthCurrent
            };

            var label = kind switch
            {
                GuidedTestKind.Damage => "damage_test",
                GuidedTestKind.Ammo => "ammo_test",
                GuidedTestKind.Sprint => "sprint_test",
                GuidedTestKind.Buy => "buy_test",
                GuidedTestKind.Cooldown => "cooldown_test",
                _ => "event"
            };

            var seconds = kind switch
            {
                GuidedTestKind.Damage => 8,
                GuidedTestKind.Ammo => 8,
                GuidedTestKind.Sprint => 8,
                GuidedTestKind.Buy => 10,
                GuidedTestKind.Cooldown => 12,
                _ => 8
            };

            var gameDir = await GetGameDirectoryAsync() ?? "";
            _discoveryKb ??= DiscoveryKnowledgeBaseStore.Load(gameDir);
            _discoveryKb.GameDirectory = gameDir;
            _discoveryKb.GameId = DiscoveryKnowledgeBaseStore.GetGameId(gameDir);

            var sigs = _currentDynamicSignatures;
            if (sigs == null && _discoveryKb?.BehaviorSignatures != null && _discoveryKb.BehaviorSignatures.Count > 0)
                sigs = _discoveryKb.BehaviorSignatures.Where(s => s != null && !string.IsNullOrWhiteSpace(s.Fingerprint)).ToDictionary(s => s.Fingerprint, s => s, StringComparer.Ordinal);

            var currentToMax = _currentDynamicCurrentToMax;
            if (currentToMax == null && sigs != null && sigs.Count > 0)
                currentToMax = _discoveryEngine.InferMaxRelationshipsFromSignatures(scan, sigs);

            var ranking = _discoveryEngine.RankCheatCandidates(scan, concept, sigs, currentToMax, _discoveryKb, 320);
            var keys = ranking.Select(r => r?.Candidate?.Key).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).Take(300).ToList();
            if (keys.Count == 0)
            {
                DynamicStatusText.Text = "No candidates to observe";
                return;
            }

            var samples = new List<DiscoveryObservationSample>();
            var events = new List<string>();

            var sessionId = 0L;
            try
            {
                DynamicStatusText.Text = $"{label}: preparing...";
                sessionId = await _discoveryClient.StartObservationAsync(scan.ScanId, 100, 1800, keys, 20000);
                await Task.Delay(800);

                await _discoveryClient.MarkEventAsync(sessionId, label, 12000);
                for (var i = seconds; i >= 1; i--)
                {
                    DynamicStatusText.Text = $"{label}: do it now ({i}s)";
                    await Task.Delay(1000);
                }

                var pull = await _discoveryClient.PullObservationAsync(sessionId, 0, 900, 20000);
                if (pull.Events.Count > 0)
                    events.AddRange(pull.Events);
                if (pull.Samples.Count > 0)
                    samples.AddRange(pull.Samples.OrderBy(s => s.Seq));

                var newSigs = _discoveryEngine.ComputeBehaviorSignatures(scan, samples);
                var newCurrentToMax = _discoveryEngine.InferMaxRelationships(scan, samples, newSigs);
                var relations = _discoveryEngine.BuildRelationGraph(scan, samples, newSigs, newCurrentToMax);

                await PersistBehaviorAnalysisToKnowledgeBaseAsync(gameDir, newSigs, relations);

                var baseRanking = _discoveryEngine.RankCandidates(scan, concept, newSigs, newCurrentToMax, _discoveryKb, 250);
                var adjusted = _discoveryEngine.ApplyEventCausalEvidence(scan, concept, baseRanking, samples, events, 250);
                await AppendEventCausalPriorsToKnowledgeBaseAsync(scan, concept, label, baseRanking, adjusted);

                _currentDynamicSignatures = newSigs;
                _currentDynamicCurrentToMax = newCurrentToMax;

                DynamicStatusText.Text = $"{label}: done (samples={samples.Count}, events={events.Count})";
            }
            catch (Exception ex)
            {
                DynamicStatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                if (sessionId > 0)
                {
                    try { await _discoveryClient.StopObservationAsync(sessionId, 20000); } catch { }
                }
            }
        }

        private Task PersistBehaviorAnalysisToKnowledgeBaseAsync(string gameDir, Dictionary<string, DiscoveryBehaviorSignature> signatures, List<DiscoveryRelationEdge> relations)
        {
            try
            {
                if (signatures == null || signatures.Count == 0)
                    return Task.CompletedTask;

                _discoveryKb ??= DiscoveryKnowledgeBaseStore.Load(gameDir);
                _discoveryKb.GameDirectory = gameDir;
                _discoveryKb.GameId = DiscoveryKnowledgeBaseStore.GetGameId(gameDir);

                var existing = _discoveryKb.BehaviorSignatures?.ToDictionary(s => s.Fingerprint ?? "", s => s, StringComparer.Ordinal) ?? new Dictionary<string, DiscoveryBehaviorSignature>(StringComparer.Ordinal);
                foreach (var kv in signatures)
                    existing[kv.Key] = kv.Value;
                _discoveryKb.BehaviorSignatures = existing.Values.Where(s => s != null && !string.IsNullOrWhiteSpace(s.Fingerprint)).ToList();

                if (relations != null)
                {
                    _discoveryKb.Relations = relations
                        .Where(r => r != null && !string.IsNullOrWhiteSpace(r.A_Fingerprint) && !string.IsNullOrWhiteSpace(r.B_Fingerprint))
                        .ToList();
                }

                DiscoveryKnowledgeBaseStore.Save(_discoveryKb);
            }
            catch
            {
            }
            return Task.CompletedTask;
        }

        private async void DynamicApplySelected_Click(object sender, RoutedEventArgs e)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    DynamicStatusText.Text = "Not connected";
                    return;
                }
                if (_currentDynamicScan == null)
                {
                    DynamicStatusText.Text = "Run a scan first";
                    return;
                }

                var selected = DynamicCheatsListView.SelectedItems.Cast<object>().OfType<DynamicCheatRow>().ToList();
                if (selected.Count == 0)
                {
                    DynamicStatusText.Text = "Select a recommendation";
                    return;
                }

                var applied = 0;
                for (var i = 0; i < selected.Count; i++)
                {
                    var row = selected[i];
                    if (row == null || string.IsNullOrWhiteSpace(row.Key))
                        continue;
                    var scanId = row.ScanId > 0 ? row.ScanId : _currentDynamicScan.ScanId;
                    if (scanId <= 0)
                        continue;
                    await _discoveryClient.BindCheatAsync(scanId, row.Key, row.Mode, row.Args, 20000);
                    await AppendDynamicCheatToKnowledgeBaseAsync(row);
                    applied++;
                }

                sw.Stop();
                _dynamicLastBindMs = sw.ElapsedMilliseconds;
                UpdateDynamicPerfText();
                DynamicStatusText.Text = $"Applied {applied} cheats";
                await RefreshDynamicCheatsAsync();
            }
            catch (Exception ex)
            {
                DynamicStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void DynamicUpvote_Click(object sender, RoutedEventArgs e)
        {
            await ApplyDynamicFeedbackAsync("up");
        }

        private async void DynamicDownvote_Click(object sender, RoutedEventArgs e)
        {
            await ApplyDynamicFeedbackAsync("down");
        }

        private async void SavedCheatsLoad_Click(object sender, RoutedEventArgs e)
        {
            await RefreshSavedCheatsAsync();
        }

        private async void SavedCheatsApply_Click(object sender, RoutedEventArgs e)
        {
            await ApplySelectedSavedCheatsAsync();
        }

        private async void SavedCheatsRemove_Click(object sender, RoutedEventArgs e)
        {
            await RemoveSelectedSavedCheatsAsync();
        }

        private async Task RefreshSavedCheatsAsync()
        {
            try
            {
                var gameDir = await GetGameDirectoryAsync() ?? "";
                _discoveryKb ??= DiscoveryKnowledgeBaseStore.Load(gameDir);
                _discoveryKb.GameDirectory = gameDir;
                _discoveryKb.GameId = DiscoveryKnowledgeBaseStore.GetGameId(gameDir);

                _savedCheatRows.Clear();

                if (_discoveryKb.Cheats != null)
                {
                    for (var i = 0; i < _discoveryKb.Cheats.Count; i++)
                    {
                        var c = _discoveryKb.Cheats[i];
                        if (c == null || string.IsNullOrWhiteSpace(c.Fingerprint) || string.IsNullOrWhiteSpace(c.Mode))
                            continue;

                        var member = c.Fingerprint;
                        var scan = _currentDynamicScan ?? _currentDiscoveryScan;
                        if (scan != null && scan.ByFingerprint.TryGetValue(c.Fingerprint, out var cand) && cand != null)
                            member = $"{cand.DeclaringTypeName}.{cand.MemberName}";

                        _savedCheatRows.Add(new SavedCheatRow
                        {
                            Kind = "Cheat",
                            Concept = c.Concept ?? "",
                            Mode = c.Mode ?? "",
                            Member = member,
                            Added = c.AddedUtc.ToString("u", CultureInfo.InvariantCulture).Replace('Z', ' ').Trim(),
                            AppliedCount = "",
                            Command = "",
                            Fingerprint = c.Fingerprint,
                            Cheat = c
                        });
                    }
                }

                if (_discoveryKb.SavedEdits != null)
                {
                    for (var i = 0; i < _discoveryKb.SavedEdits.Count; i++)
                    {
                        var e = _discoveryKb.SavedEdits[i];
                        if (e == null || string.IsNullOrWhiteSpace(e.Command))
                            continue;

                        _savedCheatRows.Add(new SavedCheatRow
                        {
                            Kind = "Edit",
                            Concept = "",
                            Mode = "",
                            Member = e.DisplayName ?? "",
                            Added = e.AddedUtc.ToString("u", CultureInfo.InvariantCulture).Replace('Z', ' ').Trim(),
                            AppliedCount = e.AppliedCount.ToString(CultureInfo.InvariantCulture),
                            Command = e.Command,
                            Fingerprint = "",
                            Edit = e
                        });
                    }
                }

                DynamicStatusText.Text = $"Saved loaded: cheats={_discoveryKb.Cheats?.Count ?? 0}, edits={_discoveryKb.SavedEdits?.Count ?? 0}";
            }
            catch (Exception ex)
            {
                DynamicStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async Task ApplySelectedSavedCheatsAsync()
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    DynamicStatusText.Text = "Not connected";
                    return;
                }

                var selected = SavedCheatsListView.SelectedItems.Cast<object>().OfType<SavedCheatRow>().ToList();
                if (selected.Count == 0)
                {
                    DynamicStatusText.Text = "Select saved items";
                    return;
                }

                if (_currentDynamicScan == null)
                    await RefreshDynamicCheatsAsync();
                var scan = _currentDynamicScan;

                var gameDir = await GetGameDirectoryAsync() ?? "";
                _discoveryKb ??= DiscoveryKnowledgeBaseStore.Load(gameDir);
                _discoveryKb.GameDirectory = gameDir;
                _discoveryKb.GameId = DiscoveryKnowledgeBaseStore.GetGameId(gameDir);

                var applied = 0;
                var now = DateTime.UtcNow;

                for (var i = 0; i < selected.Count; i++)
                {
                    var row = selected[i];
                    if (row == null)
                        continue;

                    if (row.Cheat != null)
                    {
                        if (scan == null || string.IsNullOrWhiteSpace(row.Cheat.Fingerprint) || string.IsNullOrWhiteSpace(row.Cheat.Mode))
                            continue;
                        if (!scan.ByFingerprint.TryGetValue(row.Cheat.Fingerprint, out var cand) || cand == null)
                            continue;

                        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (row.Cheat.Threshold.HasValue)
                            args["threshold"] = row.Cheat.Threshold.Value.ToString("R", CultureInfo.InvariantCulture);
                        if (row.Cheat.Value.HasValue)
                            args["value"] = row.Cheat.Value.Value.ToString("R", CultureInfo.InvariantCulture);
                        if (!string.IsNullOrWhiteSpace(row.Cheat.MaxFingerprint) && scan.ByFingerprint.TryGetValue(row.Cheat.MaxFingerprint, out var maxCand) && maxCand != null)
                        {
                            if (maxCand.SourceScanId == 0 || cand.SourceScanId == 0 || maxCand.SourceScanId == cand.SourceScanId)
                                args["maxKey"] = maxCand.Key;
                        }

                        var bindScanId = cand.SourceScanId > 0 ? cand.SourceScanId : scan.ScanId;
                        if (bindScanId <= 0)
                            continue;
                        await _discoveryClient.BindCheatAsync(bindScanId, cand.Key, row.Cheat.Mode, args.Count == 0 ? null : args, 20000);
                        applied++;
                        continue;
                    }

                    if (row.Edit != null && !string.IsNullOrWhiteSpace(row.Edit.Command))
                    {
                        var resp = await IPCMeloaderClient.SendCommandAsync(row.Edit.Command, 12000);
                        if (resp != null && resp.StartsWith("SUCCESS|", StringComparison.Ordinal))
                        {
                            row.Edit.AppliedCount++;
                            row.Edit.LastAppliedUtc = now;
                            applied++;
                        }
                        continue;
                    }
                }

                TrimSavedEdits(_discoveryKb);
                SnapshotCheatDefinitions(_discoveryKb);
                DiscoveryKnowledgeBaseStore.Save(_discoveryKb);
                DynamicStatusText.Text = $"Applied saved: {applied}";
                await RefreshSavedCheatsAsync();
            }
            catch (Exception ex)
            {
                DynamicStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async Task RemoveSelectedSavedCheatsAsync()
        {
            try
            {
                var selected = SavedCheatsListView.SelectedItems.Cast<object>().OfType<SavedCheatRow>().ToList();
                if (selected.Count == 0)
                {
                    DynamicStatusText.Text = "Select saved items";
                    return;
                }

                var gameDir = await GetGameDirectoryAsync() ?? "";
                _discoveryKb ??= DiscoveryKnowledgeBaseStore.Load(gameDir);
                _discoveryKb.GameDirectory = gameDir;
                _discoveryKb.GameId = DiscoveryKnowledgeBaseStore.GetGameId(gameDir);

                var removed = 0;
                for (var i = 0; i < selected.Count; i++)
                {
                    var row = selected[i];
                    if (row?.Cheat != null && _discoveryKb.Cheats != null)
                    {
                        removed += _discoveryKb.Cheats.RemoveAll(c =>
                            c != null &&
                            string.Equals(c.Fingerprint, row.Cheat.Fingerprint, StringComparison.Ordinal) &&
                            string.Equals(c.Mode, row.Cheat.Mode, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(c.Concept ?? "", row.Cheat.Concept ?? "", StringComparison.OrdinalIgnoreCase));
                    }
                    else if (row?.Edit != null && _discoveryKb.SavedEdits != null)
                    {
                        removed += _discoveryKb.SavedEdits.RemoveAll(e => e != null && string.Equals(e.Command, row.Edit.Command, StringComparison.Ordinal));
                    }
                }

                TrimSavedEdits(_discoveryKb);
                SnapshotCheatDefinitions(_discoveryKb);
                DiscoveryKnowledgeBaseStore.Save(_discoveryKb);
                DynamicStatusText.Text = $"Removed: {removed}";
                await RefreshSavedCheatsAsync();
            }
            catch (Exception ex)
            {
                DynamicStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async Task ApplyDynamicFeedbackAsync(string kind)
        {
            try
            {
                if (_currentDiscoveryScan == null)
                {
                    DynamicStatusText.Text = "Run a scan first";
                    return;
                }

                var selected = DynamicCheatsListView.SelectedItems.Cast<object>().OfType<DynamicCheatRow>().ToList();
                if (selected.Count == 0)
                {
                    DynamicStatusText.Text = "Select a recommendation";
                    return;
                }

                var gameDir = await GetGameDirectoryAsync() ?? "";
                _discoveryKb ??= DiscoveryKnowledgeBaseStore.Load(gameDir);
                _discoveryKb.GameDirectory = gameDir;
                _discoveryKb.GameId = DiscoveryKnowledgeBaseStore.GetGameId(gameDir);

                _discoveryKb.Feedback ??= new List<DiscoveryKnowledgeUserFeedback>();
                _discoveryKb.TokenWeights ??= new List<DiscoveryKnowledgeTokenWeight>();

                var now = DateTime.UtcNow;
                var updated = 0;
                for (var i = 0; i < selected.Count; i++)
                {
                    var row = selected[i];
                    if (row == null || string.IsNullOrWhiteSpace(row.Fingerprint))
                        continue;

                    _discoveryKb.Feedback.Add(new DiscoveryKnowledgeUserFeedback
                    {
                        Kind = kind,
                        Concept = row.ConceptValue,
                        Fingerprint = row.Fingerprint,
                        Strength = 1.0,
                        Note = row.Mode,
                        UpdatedUtc = now
                    });

                    if (_currentDiscoveryScan.ByKey.TryGetValue(row.Key, out var cand) && cand != null)
                        ApplyTokenLearning(_discoveryKb, row.ConceptValue, cand, kind.Equals("down", StringComparison.OrdinalIgnoreCase) ? -0.12 : 0.12);

                    updated++;
                }

                TrimFeedback(_discoveryKb);
                TrimTokenWeights(_discoveryKb);
                DiscoveryKnowledgeBaseStore.Save(_discoveryKb);
                DynamicStatusText.Text = $"Feedback saved ({kind}) for {updated}";
            }
            catch (Exception ex)
            {
                DynamicStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private void DynamicMonitorCheats_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (DynamicMonitorCheatsBox?.IsChecked == true)
                    _dynamicCheatMonitorTimer?.Start();
                else
                    _dynamicCheatMonitorTimer?.Stop();
            }
            catch
            {
            }
        }

        private async void DynamicCheatMonitorTimer_Tick(object sender, EventArgs e)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                    return;

                var active = await _discoveryClient.ListCheatsAsync(12000);
                var activeFp = new HashSet<string>(active.Cheats.Where(c => c != null && !string.IsNullOrWhiteSpace(c.Fingerprint)).Select(c => c.Fingerprint), StringComparer.Ordinal);
                for (var i = 0; i < _dynamicCheatRows.Count; i++)
                {
                    var row = _dynamicCheatRows[i];
                    if (row == null || string.IsNullOrWhiteSpace(row.Fingerprint))
                        continue;
                    row.Applied = activeFp.Contains(row.Fingerprint) ? "Yes" : "";
                }
                DynamicCheatsListView.Items.Refresh();
                DynamicStatusText.Text = $"Monitoring: active={active.Cheats.Count}";
            }
            catch
            {
            }
            finally
            {
                sw.Stop();
                _dynamicLastPollMs = sw.ElapsedMilliseconds;
                UpdateDynamicPerfText();
            }
        }

        private async Task AppendDynamicCheatToKnowledgeBaseAsync(DynamicCheatRow row)
        {
            try
            {
                if (row == null || string.IsNullOrWhiteSpace(row.Fingerprint))
                    return;

                var gameDir = await GetGameDirectoryAsync() ?? "";
                _discoveryKb ??= DiscoveryKnowledgeBaseStore.Load(gameDir);
                _discoveryKb.GameDirectory = gameDir;
                _discoveryKb.GameId = DiscoveryKnowledgeBaseStore.GetGameId(gameDir);

                var cheat = new DiscoveryKnowledgeCheat
                {
                    Concept = row.ConceptValue.ToString(),
                    Fingerprint = row.Fingerprint,
                    Mode = row.Mode
                };

                if (row.Args != null)
                {
                    if (row.Args.TryGetValue("threshold", out var th) && double.TryParse(th, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                        cheat.Threshold = t;
                    if (row.Args.TryGetValue("value", out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var vv))
                        cheat.Value = vv;
                }

                if (row.Args != null && row.Args.TryGetValue("maxKey", out var maxKey) && !string.IsNullOrWhiteSpace(maxKey))
                {
                    var scan = _currentDynamicScan ?? _currentDiscoveryScan;
                    if (scan != null && scan.ByKey.TryGetValue(maxKey, out var maxCand) && maxCand != null && !string.IsNullOrWhiteSpace(maxCand.Fingerprint))
                        cheat.MaxFingerprint = maxCand.Fingerprint;
                }

                _discoveryKb.Cheats.Add(cheat);
                SnapshotCheatDefinitions(_discoveryKb);
                DiscoveryKnowledgeBaseStore.Save(_discoveryKb);
            }
            catch
            {
            }
        }

        private static void SnapshotCheatDefinitions(DiscoveryKnowledgeBase kb)
        {
            if (kb == null)
                return;

            kb.CheatDefinitionsRevision = Math.Max(0, kb.CheatDefinitionsRevision) + 1;
            kb.CheatDefinitionHistory ??= new List<DiscoveryKnowledgeCheatSetSnapshot>();

            var signature = ComputeCheatDefinitionsSignature(kb.Cheats);
            kb.CheatDefinitionHistory.Add(new DiscoveryKnowledgeCheatSetSnapshot
            {
                Revision = kb.CheatDefinitionsRevision,
                Signature = signature,
                UpdatedUtc = DateTime.UtcNow,
                Cheats = kb.Cheats.Select(c => c).ToList()
            });

            if (kb.CheatDefinitionHistory.Count > 40)
                kb.CheatDefinitionHistory = kb.CheatDefinitionHistory.OrderByDescending(s => s.Revision).Take(40).OrderBy(s => s.Revision).ToList();
        }

        private static string ComputeCheatDefinitionsSignature(IReadOnlyList<DiscoveryKnowledgeCheat> cheats)
        {
            try
            {
                if (cheats == null || cheats.Count == 0)
                    return "";

                var sb = new StringBuilder();
                foreach (var c in cheats.OrderBy(c => c?.Concept ?? "", StringComparer.OrdinalIgnoreCase)
                                        .ThenBy(c => c?.Fingerprint ?? "", StringComparer.Ordinal)
                                        .ThenBy(c => c?.Mode ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    if (c == null)
                        continue;
                    sb.Append(c.Concept ?? "");
                    sb.Append('|');
                    sb.Append(c.Fingerprint ?? "");
                    sb.Append('|');
                    sb.Append(c.Mode ?? "");
                    sb.Append('|');
                    sb.Append(c.MaxFingerprint ?? "");
                    sb.Append('|');
                    sb.Append(c.Threshold?.ToString("R", CultureInfo.InvariantCulture) ?? "");
                    sb.Append('|');
                    sb.Append(c.Value?.ToString("R", CultureInfo.InvariantCulture) ?? "");
                    sb.Append('\n');
                }

                using var sha = SHA256.Create();
                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                var hash = sha.ComputeHash(bytes);
                return Convert.ToHexString(hash);
            }
            catch
            {
                return "";
            }
        }

        private static void ApplyTokenLearning(DiscoveryKnowledgeBase kb, DiscoveryConcept concept, DiscoveryCandidate cand, double delta)
        {
            if (kb == null || cand == null)
                return;

            kb.TokenWeights ??= new List<DiscoveryKnowledgeTokenWeight>();
            var hay = (cand.MemberName ?? "") + " " + (cand.DeclaringTypeName ?? "") + " " + (cand.ComponentTypeName ?? "") + " " + (cand.GameObjectName ?? "") + " " + (cand.GameObjectPath ?? "");
            var tokens = TokenizeForLearning(hay);
            if (tokens.Count == 0)
                return;

            var now = DateTime.UtcNow;
            var used = 0;
            foreach (var t in tokens)
            {
                if (string.IsNullOrWhiteSpace(t))
                    continue;
                if (t.Length < 3)
                    continue;
                var existing = kb.TokenWeights.FirstOrDefault(w => w != null && w.Concept == concept && string.Equals(w.Token, t, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    kb.TokenWeights.Add(new DiscoveryKnowledgeTokenWeight
                    {
                        Concept = concept,
                        Token = t.ToLowerInvariant(),
                        Weight = Math.Max(-1, Math.Min(1, delta)),
                        UpdatedUtc = now
                    });
                }
                else
                {
                    existing.Weight = Math.Max(-1, Math.Min(1, existing.Weight + delta));
                    existing.UpdatedUtc = now;
                }
                used++;
                if (used >= 24)
                    break;
            }
        }

        private static List<string> TokenizeForLearning(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return result;

            var sb = new StringBuilder();
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (char.IsLetterOrDigit(ch) || ch == '_')
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    continue;
                }

                if (sb.Length > 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
            }
            if (sb.Length > 0)
                result.Add(sb.ToString());

            return result;
        }

        private static void TrimTokenWeights(DiscoveryKnowledgeBase kb)
        {
            if (kb?.TokenWeights == null)
                return;
            var now = DateTime.UtcNow;
            kb.TokenWeights.RemoveAll(w => w == null || string.IsNullOrWhiteSpace(w.Token) || (now - w.UpdatedUtc).TotalDays > 365);
            if (kb.TokenWeights.Count > 3000)
                kb.TokenWeights = kb.TokenWeights.OrderByDescending(w => w.UpdatedUtc).Take(3000).ToList();
        }

        private static void TrimFeedback(DiscoveryKnowledgeBase kb)
        {
            if (kb?.Feedback == null)
                return;
            var now = DateTime.UtcNow;
            kb.Feedback.RemoveAll(f => f == null || string.IsNullOrWhiteSpace(f.Fingerprint) || (now - f.UpdatedUtc).TotalDays > 365);
            if (kb.Feedback.Count > 3000)
                kb.Feedback = kb.Feedback.OrderByDescending(f => f.UpdatedUtc).Take(3000).ToList();
        }

        private static void TrimWriteProbes(DiscoveryKnowledgeBase kb)
        {
            if (kb?.WriteProbes == null)
                return;
            var now = DateTime.UtcNow;
            kb.WriteProbes.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Fingerprint) || (now - p.UpdatedUtc).TotalDays > 365);
            if (kb.WriteProbes.Count > 3000)
                kb.WriteProbes = kb.WriteProbes.OrderByDescending(p => p.UpdatedUtc).Take(3000).ToList();
        }

        private static bool HasRecentWriteProbe(DiscoveryKnowledgeBase kb, DiscoveryConcept concept, string fingerprint, int maxAgeDays)
        {
            if (kb?.WriteProbes == null || kb.WriteProbes.Count == 0 || string.IsNullOrWhiteSpace(fingerprint))
                return false;
            var now = DateTime.UtcNow;
            for (var i = 0; i < kb.WriteProbes.Count; i++)
            {
                var p = kb.WriteProbes[i];
                if (p == null)
                    continue;
                if (p.Concept != concept)
                    continue;
                if (!string.Equals(p.Fingerprint, fingerprint, StringComparison.Ordinal))
                    continue;
                if ((now - p.UpdatedUtc).TotalDays <= maxAgeDays)
                    return true;
            }
            return false;
        }

        private static bool TryGetWriteProbe(DiscoveryKnowledgeBase kb, DiscoveryConcept concept, string fingerprint, out DiscoveryKnowledgeWriteProbe probe)
        {
            probe = null;
            if (kb?.WriteProbes == null || kb.WriteProbes.Count == 0 || string.IsNullOrWhiteSpace(fingerprint))
                return false;
            DiscoveryKnowledgeWriteProbe best = null;
            for (var i = 0; i < kb.WriteProbes.Count; i++)
            {
                var p = kb.WriteProbes[i];
                if (p == null || p.Concept != concept || string.IsNullOrWhiteSpace(p.Fingerprint))
                    continue;
                if (!string.Equals(p.Fingerprint, fingerprint, StringComparison.Ordinal))
                    continue;
                if (best == null || p.UpdatedUtc > best.UpdatedUtc)
                    best = p;
            }
            if (best == null)
                return false;
            probe = best;
            return true;
        }

        private static void UpsertWriteProbe(DiscoveryKnowledgeBase kb, DiscoveryConcept concept, DiscoveryWriteProbeResult result)
        {
            try
            {
                if (kb == null || result == null || string.IsNullOrWhiteSpace(result.Fingerprint))
                    return;
                kb.WriteProbes ??= new List<DiscoveryKnowledgeWriteProbe>();

                var existing = kb.WriteProbes.FirstOrDefault(p =>
                    p != null &&
                    p.Concept == concept &&
                    string.Equals(p.Fingerprint, result.Fingerprint, StringComparison.Ordinal));

                if (existing == null)
                {
                    existing = new DiscoveryKnowledgeWriteProbe
                    {
                        Concept = concept,
                        Fingerprint = result.Fingerprint
                    };
                    kb.WriteProbes.Add(existing);
                }

                existing.DelayMs = result.DelayMs;
                existing.Wrote = result.Wrote;
                existing.Sticky = result.Sticky;
                existing.RubberBand = result.RubberBand;
                existing.ClampDetected = result.ClampDetected;
                existing.Score = Math.Max(0, Math.Min(1.0, result.Score));
                existing.UpdatedUtc = DateTime.UtcNow;
            }
            catch
            {
            }
        }

        private static void TrimSavedEdits(DiscoveryKnowledgeBase kb)
        {
            if (kb?.SavedEdits == null)
                return;
            var now = DateTime.UtcNow;
            kb.SavedEdits.RemoveAll(e => e == null || string.IsNullOrWhiteSpace(e.Command) || (now - e.AddedUtc).TotalDays > 365);
            if (kb.SavedEdits.Count > 1000)
                kb.SavedEdits = kb.SavedEdits.OrderByDescending(e => e.AddedUtc).Take(1000).ToList();
        }

        private async void OnEditPerformed(IPCMeloaderClient.IPCEditEvent e)
        {
            try
            {
                if (e == null || !e.Success || string.IsNullOrWhiteSpace(e.Command))
                    return;
                if (e.Command.StartsWith("DISCOVERY_CHEAT_BIND|", StringComparison.Ordinal) ||
                    e.Command.StartsWith("DISCOVERY_CHEAT_UNBIND|", StringComparison.Ordinal))
                    return;

                var gameDir = await GetGameDirectoryAsync() ?? "";
                if (string.IsNullOrWhiteSpace(gameDir))
                    return;

                _discoveryKb ??= DiscoveryKnowledgeBaseStore.Load(gameDir);
                _discoveryKb.GameDirectory = gameDir;
                _discoveryKb.GameId = DiscoveryKnowledgeBaseStore.GetGameId(gameDir);
                _discoveryKb.SavedEdits ??= new List<DiscoveryKnowledgeSavedEdit>();

                var displayName = TryBuildEditDisplayName(e.Command, out var dn) ? dn : e.Command;
                var existing = _discoveryKb.SavedEdits.FirstOrDefault(s => s != null && string.Equals(s.Command, e.Command, StringComparison.Ordinal));
                if (existing == null)
                {
                    _discoveryKb.SavedEdits.Add(new DiscoveryKnowledgeSavedEdit
                    {
                        Command = e.Command,
                        DisplayName = displayName,
                        AddedUtc = DateTime.UtcNow
                    });
                }

                TryAutoCreateFreezeCheatFromEdit(e.Command);

                TrimSavedEdits(_discoveryKb);
                SnapshotCheatDefinitions(_discoveryKb);
                DiscoveryKnowledgeBaseStore.Save(_discoveryKb);
            }
            catch
            {
            }
        }

        private bool TryBuildEditDisplayName(string command, out string displayName)
        {
            displayName = null;
            try
            {
                if (string.IsNullOrWhiteSpace(command))
                    return false;
                var parts = command.Split('|', StringSplitOptions.None);
                if (parts.Length < 4)
                    return false;

                if (parts[0] == "SET_FIELD" || parts[0] == "SET_PROPERTY")
                {
                    if (parts.Length < 4)
                        return false;
                    displayName = $"{parts[1]}.{parts[2]} = {parts[3]}";
                    return true;
                }

                if (parts[0] == "SET_FIELD_INSTANCE" || parts[0] == "SET_PROPERTY_INSTANCE")
                {
                    if (parts.Length < 5)
                        return false;
                    displayName = $"{parts[1]}.{parts[3]} (id={parts[2]}) = {parts[4]}";
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private void TryAutoCreateFreezeCheatFromEdit(string command)
        {
            try
            {
                if (_currentDiscoveryScan == null || _discoveryKb == null)
                    return;

                if (string.IsNullOrWhiteSpace(command))
                    return;

                var parts = command.Split('|', StringSplitOptions.None);
                if (parts.Length < 5)
                    return;

                if (parts[0] != "SET_FIELD_INSTANCE" && parts[0] != "SET_PROPERTY_INSTANCE" && parts[0] != "SET_FIELD" && parts[0] != "SET_PROPERTY")
                    return;

                var declaringType = parts[1] ?? "";
                var member = parts[0] == "SET_FIELD" || parts[0] == "SET_PROPERTY" ? (parts.Length > 2 ? parts[2] : "") : (parts.Length > 3 ? parts[3] : "");
                var valueText = parts[0] == "SET_FIELD" || parts[0] == "SET_PROPERTY" ? (parts.Length > 3 ? parts[3] : "") : (parts.Length > 4 ? parts[4] : "");
                if (string.IsNullOrWhiteSpace(declaringType) || string.IsNullOrWhiteSpace(member))
                    return;

                var candidates = _currentDiscoveryScan.Candidates
                    .Where(c => c != null &&
                                c.CanWrite &&
                                !string.IsNullOrWhiteSpace(c.Fingerprint) &&
                                string.Equals(c.DeclaringTypeName ?? "", declaringType, StringComparison.Ordinal) &&
                                string.Equals(c.MemberName ?? "", member, StringComparison.Ordinal))
                    .ToList();

                if (candidates.Count != 1)
                    return;

                if (!TryParseFreezeValue(valueText, out var freezeValue))
                    return;

                _discoveryKb.Cheats ??= new List<DiscoveryKnowledgeCheat>();

                var fp = candidates[0].Fingerprint;
                var existing = _discoveryKb.Cheats.FirstOrDefault(c =>
                    c != null &&
                    string.Equals(c.Fingerprint, fp, StringComparison.Ordinal) &&
                    string.Equals(c.Mode, "freeze", StringComparison.OrdinalIgnoreCase) &&
                    c.Value.HasValue &&
                    Math.Abs(c.Value.Value - freezeValue) < 1e-9);

                if (existing != null)
                    return;

                _discoveryKb.Cheats.Add(new DiscoveryKnowledgeCheat
                {
                    Concept = "Custom",
                    Fingerprint = fp,
                    Mode = "freeze",
                    Value = freezeValue,
                    AddedUtc = DateTime.UtcNow
                });
            }
            catch
            {
            }
        }

        private static bool TryParseFreezeValue(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return true;
            if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1")
            {
                value = 1;
                return true;
            }
            if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0")
            {
                value = 0;
                return true;
            }
            return false;
        }

        private void UpdateDynamicPerfText()
        {
            if (DynamicPerfText == null)
                return;

            var parts = new List<string>();
            if (_dynamicLastScanMs > 0)
                parts.Add($"scan={_dynamicLastScanMs}ms");
            if (_dynamicLastRefreshMs > 0)
                parts.Add($"refresh={_dynamicLastRefreshMs}ms");
            if (_dynamicLastBindMs > 0)
                parts.Add($"apply={_dynamicLastBindMs}ms");
            if (_dynamicLastPollMs > 0)
                parts.Add($"poll={_dynamicLastPollMs}ms");
            DynamicPerfText.Text = parts.Count > 0 ? string.Join("  ", parts) : "";
        }

        private async Task PersistBehaviorAnalysisToKnowledgeBaseAsync()
        {
            try
            {
                if (_currentDiscoveryScan == null || _currentDiscoverySignatures == null)
                    return;

                var gameDir = await GetGameDirectoryAsync() ?? "";
                _discoveryKb ??= DiscoveryKnowledgeBaseStore.Load(gameDir);
                _discoveryKb.GameDirectory = gameDir;
                _discoveryKb.GameId = DiscoveryKnowledgeBaseStore.GetGameId(gameDir);

                var existing = _discoveryKb.BehaviorSignatures?.ToDictionary(s => s.Fingerprint ?? "", s => s, StringComparer.Ordinal) ?? new Dictionary<string, DiscoveryBehaviorSignature>(StringComparer.Ordinal);
                foreach (var kv in _currentDiscoverySignatures)
                    existing[kv.Key] = kv.Value;
                _discoveryKb.BehaviorSignatures = existing.Values.Where(s => s != null && !string.IsNullOrWhiteSpace(s.Fingerprint)).ToList();

                if (_currentDiscoveryRelations != null)
                {
                    _discoveryKb.Relations = _currentDiscoveryRelations
                        .Where(r => r != null && !string.IsNullOrWhiteSpace(r.A_Fingerprint) && !string.IsNullOrWhiteSpace(r.B_Fingerprint))
                        .ToList();
                }

                DiscoveryKnowledgeBaseStore.Save(_discoveryKb);
            }
            catch
            {
            }
        }

        private void RefreshDiscoveryRanking()
        {
            if (_currentDiscoveryScan == null)
                return;

            var concept = GetSelectedDiscoveryConcept();
            IReadOnlyList<DiscoveryCandidateScore> ranking = _currentDiscoveryRanking;
            if (ranking == null || ranking.Count == 0 || ranking[0].Concept != concept)
                ranking = _discoveryEngine.RankCandidates(_currentDiscoveryScan, concept, _currentDiscoverySignatures, _currentDiscoveryCurrentToMax, _discoveryKb, 250);
            _currentDiscoveryRanking = ranking;

            var confirmed = new HashSet<string>(StringComparer.Ordinal);
            if (_discoveryKb?.ConfirmedMappings != null)
            {
                for (var i = 0; i < _discoveryKb.ConfirmedMappings.Count; i++)
                {
                    var fp = _discoveryKb.ConfirmedMappings[i]?.Fingerprint;
                    if (!string.IsNullOrWhiteSpace(fp))
                        confirmed.Add(fp);
                }
            }

            _discoveryRows.Clear();
            for (var i = 0; i < ranking.Count; i++)
            {
                var r = ranking[i];
                var c = r.Candidate;
                if (c == null)
                    continue;
                _discoveryRows.Add(new DiscoveryRow
                {
                    Score = r.Score.ToString("F3", CultureInfo.InvariantCulture),
                    NameScore = r.NameScore.ToString("F2", CultureInfo.InvariantCulture),
                    TypeScore = r.TypeScore.ToString("F2", CultureInfo.InvariantCulture),
                    ContextScore = r.ContextScore.ToString("F2", CultureInfo.InvariantCulture),
                    DynamicScore = r.DynamicScore.ToString("F2", CultureInfo.InvariantCulture),
                    DatabaseScore = r.DatabaseScore.ToString("F2", CultureInfo.InvariantCulture),
                    KnowledgeScore = r.KnowledgeScore.ToString("F2", CultureInfo.InvariantCulture),
                    RelationScore = r.RelationScore.ToString("F2", CultureInfo.InvariantCulture),
                    CausalScore = r.CausalScore.ToString("F2", CultureInfo.InvariantCulture),
                    Kind = c.Kind.ToString(),
                    Member = c.MemberName ?? "",
                    DeclaringType = c.DeclaringTypeName ?? "",
                    ComponentType = c.ComponentTypeName ?? "",
                    Writable = c.CanWrite ? "Yes" : "No",
                    Path = c.GameObjectPath ?? "",
                    Confirmed = !string.IsNullOrWhiteSpace(c.Fingerprint) && confirmed.Contains(c.Fingerprint) ? "Yes" : "",
                    Key = c.Key,
                    Fingerprint = c.Fingerprint,
                    Concept = concept
                });
            }
        }

        private DiscoveryConcept GetSelectedDiscoveryConcept()
        {
            if (DiscoveryConceptBox.SelectedItem is DiscoveryConcept c)
                return c;
            var s = DiscoveryConceptBox.SelectedItem?.ToString();
            if (!string.IsNullOrWhiteSpace(s) && Enum.TryParse<DiscoveryConcept>(s, out var parsed))
                return parsed;
            return DiscoveryConcept.HealthCurrent;
        }

        private async Task BindDiscoveryCheatAsync(string mode, IReadOnlyDictionary<string, string> args)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    DiscoveryStatusText.Text = "Not connected";
                    return;
                }
                if (_currentDiscoveryScan == null)
                {
                    DiscoveryStatusText.Text = "Run Scan first";
                    return;
                }

                var row = DiscoveryCandidatesListView.SelectedItem as DiscoveryRow;
                if (row == null || string.IsNullOrWhiteSpace(row.Key))
                {
                    DiscoveryStatusText.Text = "Select a candidate";
                    return;
                }

                var cheatId = await _discoveryClient.BindCheatAsync(_currentDiscoveryScan.ScanId, row.Key, mode, args, 20000);
                await AppendCheatToKnowledgeBaseAsync(row, mode, args);
                DiscoveryStatusText.Text = $"Cheat bound: id={cheatId} mode={mode}";
            }
            catch (Exception ex)
            {
                DiscoveryStatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async Task AppendCheatToKnowledgeBaseAsync(DiscoveryRow row, string mode, IReadOnlyDictionary<string, string> args)
        {
            try
            {
                if (row == null || string.IsNullOrWhiteSpace(row.Fingerprint))
                    return;

                var gameDir = await GetGameDirectoryAsync() ?? "";
                _discoveryKb ??= DiscoveryKnowledgeBaseStore.Load(gameDir);
                _discoveryKb.GameDirectory = gameDir;
                _discoveryKb.GameId = DiscoveryKnowledgeBaseStore.GetGameId(gameDir);

                var cheat = new DiscoveryKnowledgeCheat
                {
                    Concept = row.Concept.ToString(),
                    Fingerprint = row.Fingerprint,
                    Mode = mode
                };

                if (args != null)
                {
                    if (args.TryGetValue("threshold", out var th) && double.TryParse(th, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                        cheat.Threshold = t;
                    if (args.TryGetValue("value", out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var vv))
                        cheat.Value = vv;
                    if (args.TryGetValue("maxKey", out var maxKey) && !string.IsNullOrWhiteSpace(maxKey))
                    {
                        if (_currentDiscoveryScan != null && _currentDiscoveryScan.ByKey.TryGetValue(maxKey, out var maxCand) && maxCand != null && !string.IsNullOrWhiteSpace(maxCand.Fingerprint))
                            cheat.MaxFingerprint = maxCand.Fingerprint;
                    }
                }

                _discoveryKb.Cheats.Add(cheat);
                SnapshotCheatDefinitions(_discoveryKb);
                if (_currentDiscoveryScan != null && _currentDiscoveryScan.ByKey.TryGetValue(row.Key ?? "", out var cand) && cand != null)
                    ApplyTokenLearning(_discoveryKb, row.Concept, cand, 0.08);
                TrimTokenWeights(_discoveryKb);
                DiscoveryKnowledgeBaseStore.Save(_discoveryKb);
            }
            catch
            {
            }
        }

        private async Task AppendExperimentToKnowledgeBaseAsync(DiscoveryExperimentEndResult end)
        {
            try
            {
                if (end == null || _currentDiscoveryScan == null)
                    return;

                var gameDir = await GetGameDirectoryAsync() ?? "";
                _discoveryKb ??= DiscoveryKnowledgeBaseStore.Load(gameDir);
                _discoveryKb.GameDirectory = gameDir;
                _discoveryKb.GameId = DiscoveryKnowledgeBaseStore.GetGameId(gameDir);

                var log = new DiscoveryKnowledgeExperimentLog
                {
                    Label = end.Label,
                    StartedUtc = DateTime.UtcNow,
                    EndedUtc = DateTime.UtcNow
                };

                for (var i = 0; i < end.Deltas.Count; i++)
                {
                    var d = end.Deltas[i];
                    if (d == null || string.IsNullOrWhiteSpace(d.Key))
                        continue;
                    if (!_currentDiscoveryScan.ByKey.TryGetValue(d.Key, out var cand))
                        continue;

                    log.Deltas.Add(new DiscoveryKnowledgeExperimentDelta
                    {
                        Fingerprint = cand.Fingerprint,
                        KeyHint = cand.Key,
                        Before = d.Before,
                        After = d.After,
                        Delta = d.Delta
                    });
                }

                _discoveryKb.Experiments.Add(log);
                DiscoveryKnowledgeBaseStore.Save(_discoveryKb);
            }
            catch
            {
            }
        }

        private async Task SendGameCommand(string command, string successMessage)
        {
            if (!_isConnected)
            {
                GameStatusText.Text = "Not connected to game";
                return;
            }

            try
            {
                GameStatusText.Text = $"Sending: {command}";
                string response = await IPCMeloaderClient.SendCommandAsync(command);
                
                if (response != null && !response.StartsWith("ERROR"))
                {
                    GameStatusText.Text = successMessage;
                    Logger.LogInfo($"Game command successful: {command}");
                }
                else
                {
                    GameStatusText.Text = $"Command failed: {response}";
                    Logger.LogWarning($"Game command failed: {command} - {response}");
                }
            }
            catch (Exception ex)
            {
                GameStatusText.Text = $"Command error: {ex.Message}";
                Logger.LogError($"Game command error: {command} - {ex.Message}");
            }
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            // Start checking connection status periodically
            var connectionTimer = new DispatcherTimer();
            connectionTimer.Interval = TimeSpan.FromSeconds(2);
            connectionTimer.Tick += (s, args) => UpdateConnectionStatus();
            connectionTimer.Start();
        }
    }
}
