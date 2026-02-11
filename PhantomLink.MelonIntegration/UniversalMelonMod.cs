using System;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
#if NET35
#else
using System.Collections.Concurrent;
#endif

using PhantomLink.MelonIntegration.Core;
using PhantomLink.MelonIntegration.IPC;
using PhantomLink.MelonIntegration.Features;

// MelonLoader assembly attributes
[assembly: MelonLoader.MelonInfo(typeof(PhantomLink.MelonIntegration.UniversalMelonMod), "PhantomLink Universal Mod", "1.1.0", "bbush")]
[assembly: MelonLoader.MelonGame(null, null)] // Universal game compatibility

namespace PhantomLink.MelonIntegration
{
    /// <summary>
    /// UNIVERSAL MELONMOD - Extreme universal compatibility across all MelonLoader versions
    /// and both IL2CPP/Mono game types. Acts as pipe client for runtime tool communication.
    /// </summary>
    public class UniversalMelonMod : MelonLoader.MelonMod
    {
        private static Thread _pipeThread;
        private static bool _isConnected = false;
        private static string _pipeName = "PhantomLinkUniversalPipe";
        private static readonly TimeSpan _reconnectInterval = TimeSpan.FromSeconds(3);
        private static string _gameDirectory = null;
        
        // Universal compatibility detection
        private static bool _isIL2CPP = false;
        private static bool _isMono = false;
        private static string _melonLoaderVersion = "Unknown";

        private static int _mainThreadId = 0;
#if NET35
        private static readonly Queue<MainThreadRequest> _mainThreadQueue = new Queue<MainThreadRequest>();
        private static readonly object _mainThreadQueueLock = new object();
#else
        private static readonly ConcurrentQueue<MainThreadRequest> _mainThreadQueue = new ConcurrentQueue<MainThreadRequest>();
#endif
        private static bool _fpsOverlayEnabled = false;
        private static double _fps = 0;
        private static readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
        private static long _lastFpsTicks = 0;
        private static float _timeScaleBeforePause = 1f;
        private static bool _timeFrozen = false;
        private static bool _colliderOverlayEnabled = false;
        private static long _colliderOverlayLastScanTicks = 0;
        private static Array _colliderOverlayCache = null;
        private static bool _wireframeEnabled = false;
        private static bool _noclipEnabled = false;
        private static readonly List<object> _noclipChangedComponents = new List<object>();
        private static readonly Dictionary<ObjectMemberKey, object> _noclipOriginalValues = new Dictionary<ObjectMemberKey, object>();
        private static int _noclipLayer = -1;
        private static readonly bool[] _noclipLayerIgnoreOriginal = new bool[32];
        private static bool _noclipLayerIgnoreCaptured = false;

        private static bool _freeCameraEnabled = false;
        private static object _freeCamGameObject = null;
        private static object _mainCameraBeforeFreeCam = null;
        private static bool _mainCameraWasEnabled = true;
        private static bool _freeCamUsingMainCamera = false;
        private static object _freeCamOriginalParent = null;
        private static object _freeCamOriginalLocalPosition = null;
        private static object _freeCamOriginalLocalEulerAngles = null;
        private static readonly List<object> _freeCamDisabledComponents = new List<object>();
        private static readonly Dictionary<ObjectMemberKey, object> _freeCamOriginalValues = new Dictionary<ObjectMemberKey, object>();

        private static long _lastRunInBackgroundTicks = 0;
        private static bool _runInBackgroundForced = false;

        private static volatile bool _sceneSyncActive = false;
        private static int _sceneSyncSessionId = 0;
        private static long _sceneSyncLastScanTicks = 0;
        private static readonly Dictionary<int, SceneSyncEntry> _sceneSyncObjects = new Dictionary<int, SceneSyncEntry>();
        private static readonly Queue<string> _sceneSyncEvents = new Queue<string>(2048);
        private static readonly object _sceneSyncEventsLock = new object();
        private static volatile bool _sceneSyncCleanupRequested = false;
        private static bool _sceneSyncScanInProgress = false;
        private static bool _sceneSyncScanForceCreate = false;
        private static readonly Stack<object> _sceneSyncScanStack = new Stack<object>(4096);
        private static readonly HashSet<int> _sceneSyncScanVisited = new HashSet<int>();
        private static readonly HashSet<int> _sceneSyncScanCurrent = new HashSet<int>();
        private static int _sceneSyncScanMaxObjects = 8000;
        private static int _sceneSyncScanProcessed = 0;
        private static long _sceneSyncNextScanStartTicks = 0;
        private static readonly Queue<int> _sceneSyncPendingDestroy = new Queue<int>(8192);
        private static string _sceneSyncLastSceneSignature = "";
        private static long _sceneSyncLastSceneSignatureCheckTicks = 0;
        private static bool _sceneSyncAwaitingDestroyDrain = false;

        private static bool _zoomOutEnabled = false;
        private static float _originalFov = 60f;
        private static bool _cameraOriginalCaptured = false;

        private static bool _firstPersonEnabled = false;
        private static bool _thirdPersonEnabled = false;
        private static object _cameraOriginalParent = null;
        private static object _cameraOriginalPosition = null;
        private static object _cameraOriginalRotation = null;

        private static bool _infiniteHealthEnabled = false;
        private static bool _infiniteAmmoEnabled = false;
        private static bool _infiniteStaminaEnabled = false;
        private static bool _godModeEnabled = false;
        private static BoundNumericMember _healthMember;
        private static BoundNumericMember _ammoMember;
        private static BoundNumericMember _moneyMember;
        private static BoundNumericMember _xpMember;
        private static BoundNumericMember _staminaMember;
        private static WeakReference _cachedPlayerObject;
        private static long _cachedPlayerObjectTicks;
        private static long _lastHealthRebindTicks = 0;
        private static long _lastHealthSlowScanTicks = 0;
        private static long _lastAmmoValueScanTicks = 0;
        private static long _lastMoneySearchTicks = 0;
        private static long _lastXpSearchTicks = 0;
        private static long _lastStaminaSearchTicks = 0;

        private sealed class UiValueObservation
        {
            public string Slot;
            public double PrimaryValue;
            public double SecondaryValue;
            public bool HasSecondary;
            public int Confidence;
        }

        private enum DiscoveryCandidateKind
        {
            Numeric = 0,
            Boolean = 1,
            CollectionCount = 2,
            Enum = 3
        }

        private sealed class DiscoveryCandidateDescriptor
        {
            public string Key;
            public string Fingerprint;
            public DiscoveryCandidateKind Kind;
            public WeakReference Component;
            public MemberInfo Member;
            public string GameObjectName;
            public string GameObjectPath;
            public string ComponentTypeName;
            public string DeclaringTypeName;
            public string MemberName;
            public string MemberTypeName;
            public bool IsStatic;
            public bool CanWrite;
        }

        private sealed class DiscoveryScanCache
        {
            public long ScanId;
            public long CreatedTicks;
            public List<DiscoveryCandidateDescriptor> Entries;
            public Dictionary<string, DiscoveryCandidateDescriptor> ByKey;
        }

        private struct DiscoverySample
        {
            public long Seq;
            public long Ticks;
            public Dictionary<string, string> Values;
        }

        private struct DiscoveryObservationStats
        {
            public long Count;
            public double Mean;
            public double M2;
            public double Min;
            public double Max;
            public long IntCount;
            public long Updates;
            public double Last;
            public bool HasLast;

            public void Push(double v)
            {
                if (Count == 0)
                {
                    Count = 1;
                    Mean = v;
                    M2 = 0;
                    Min = v;
                    Max = v;
                    Last = v;
                    HasLast = true;
                    return;
                }

                Count++;
                if (v < Min) Min = v;
                if (v > Max) Max = v;

                var delta = v - Mean;
                Mean += delta / Count;
                var delta2 = v - Mean;
                M2 += delta * delta2;

                if (HasLast)
                {
                    if (Math.Abs(v - Last) > 1e-9)
                        Updates++;
                }
                Last = v;
                HasLast = true;
            }

            public double Variance => Count > 1 ? (M2 / (Count - 1)) : 0;
        }

        private sealed class DiscoveryObservationSession
        {
            public long SessionId;
            public long ScanId;
            public int IntervalMs;
            public HashSet<string> Keys;
            public List<string> KeyList;
            public int Cursor;
            public int PerTickKeys;
            public bool SummaryOnly;
            public Dictionary<string, DiscoveryObservationStats> Stats;
            public long SamplesTaken;
            public bool Completed;
            public long ExpiresAtTicks;
            public long NextSampleTicks;
            public long NextSeq;
            public long LastPulledSeq;
            public int MaxSamples;
            public Queue<DiscoverySample> Samples;
            public Queue<string> Events;
        }

        private static readonly object _discoverySync = new object();
        private static long _discoveryNextScanId = 1;
        private static readonly Dictionary<long, DiscoveryScanCache> _discoveryScans = new Dictionary<long, DiscoveryScanCache>();
        private static readonly Dictionary<long, DiscoveryScanJob> _discoveryScanJobs = new Dictionary<long, DiscoveryScanJob>();
        private static long _discoveryNextSessionId = 1;
        private static readonly Dictionary<long, DiscoveryObservationSession> _discoverySessions = new Dictionary<long, DiscoveryObservationSession>();
        private static readonly Dictionary<Type, CachedDiscoveryMember[]> _discoveryMemberCache = new Dictionary<Type, CachedDiscoveryMember[]>();
        private static readonly object _discoveryMemberCacheSync = new object();

        private sealed class DiscoveryScanJob
        {
            public long ScanId;
            public long StartedTicks;
            public int MaxCandidates;
            public HashSet<DiscoveryCandidateKind> Kinds;
            public int BudgetMs;
            public string Scope;
            public Array Components;
            public int Index;
            public int Total;
            public List<DiscoveryCandidateDescriptor> Entries;
            public Dictionary<string, DiscoveryCandidateDescriptor> ByKey;
            public bool Completed;
            public string Stage;
        }

        private sealed class CachedDiscoveryMember
        {
            public MemberInfo Member;
            public DiscoveryCandidateKind Kind;
            public bool CanWrite;
            public bool IsStatic;
            public string DeclaringTypeName;
            public string MemberName;
            public string MemberTypeName;
        }

        private static string _discoveryOverlayText;
        private static double _discoveryOverlayProgress;

        private sealed class DiscoveryExperimentSession
        {
            public long ExperimentId;
            public long ScanId;
            public string Label;
            public long StartedTicks;
            public List<string> Keys;
            public Dictionary<string, string> BeforeValues;
        }

        private static long _discoveryNextExperimentId = 1;
        private static readonly Dictionary<long, DiscoveryExperimentSession> _discoveryExperiments = new Dictionary<long, DiscoveryExperimentSession>();

        private enum DynamicCheatMode
        {
            Freeze = 0,
            AutoRefill = 1,
            ClampMin = 2,
            ClampMax = 3,
            NoDecrease = 4
        }

        private sealed class DynamicCheat
        {
            public long CheatId;
            public long ScanId;
            public string Key;
            public string Fingerprint;
            public DiscoveryCandidateKind Kind;
            public WeakReference Component;
            public MemberInfo Member;
            public bool IsStatic;
            public bool CanWrite;
            public DynamicCheatMode Mode;

            public double NumericValue;
            public double SecondaryNumericValue;
            public bool BoolValue;
            public long EnumValue;

            public string MaxKey;
            public WeakReference MaxComponent;
            public MemberInfo MaxMember;
            public long LastResolvedTicks;

            public List<MethodBase> PatchedMethods;
        }

        private static long _dynamicCheatNextId = 1;
        private static readonly Dictionary<long, DynamicCheat> _dynamicCheats = new Dictionary<long, DynamicCheat>();
        private static readonly Dictionary<MethodBase, FieldInfo> _noDecreaseMethodToField = new Dictionary<MethodBase, FieldInfo>();

        private struct TelemetryKey
        {
            public int TargetHash;
            public MemberInfo Member;

            public override int GetHashCode()
            {
                unchecked
                {
                    var h = TargetHash;
                    if (Member != null)
                        h = (h * 397) ^ Member.GetHashCode();
                    return h;
                }
            }

            public override bool Equals(object obj)
            {
                if (!(obj is TelemetryKey other))
                    return false;
                return TargetHash == other.TargetHash && ReferenceEquals(Member, other.Member);
            }
        }

        private struct ObjectMemberKey
        {
            public object Target;
            public string MemberName;

            public override int GetHashCode()
            {
                unchecked
                {
                    var h = Target != null ? RuntimeHelpers.GetHashCode(Target) : 0;
                    h = (h * 397) ^ (MemberName != null ? MemberName.GetHashCode() : 0);
                    return h;
                }
            }

            public override bool Equals(object obj)
            {
                if (!(obj is ObjectMemberKey other))
                    return false;
                return ReferenceEquals(Target, other.Target) && string.Equals(MemberName, other.MemberName, StringComparison.Ordinal);
            }
        }

        private struct SceneSyncEntry
        {
            public int ParentId;
            public string Name;
            public bool Active;
            public string LocalPos;
            public string LocalRot;
            public string LocalScale;
            public string BoundsCenter;
            public string BoundsSize;
            public string ColliderCenter;
            public string ColliderSize;
            public string Components;
            public long LastFullTicks;
        }

        private sealed class GodModeOverride
        {
            public WeakReference Target;
            public MemberInfo Member;
            public object OriginalValue;
            public object OverrideValue;
        }

        private sealed class NumericTelemetry
        {
            public WeakReference Target;
            public WeakReference Root;
            public MemberInfo Member;
            public Type ValueType;
            public bool CanWrite;

            public double LastValue;
            public double MinValue;
            public double MaxValue;
            public int Samples;
            public int Changes;
            public int Increases;
            public int Decreases;
            public double AbsDeltaSum;
            public long LastSeenTicks;
            public long LastChangedTicks;
        }

        private static readonly object _telemetryLock = new object();
        private static readonly Dictionary<TelemetryKey, NumericTelemetry> _telemetry = new Dictionary<TelemetryKey, NumericTelemetry>();
        private static Array _telemetryComponents = null;
        private static int _telemetryComponentIndex = 0;
        private static long _telemetryComponentsRefreshTicks = 0;
        private static long _telemetryPruneTicks = 0;
        private static readonly object _numericMemberCacheLock = new object();
        private static readonly Dictionary<Type, FieldInfo[]> _numericFieldsCache = new Dictionary<Type, FieldInfo[]>();
        private static readonly Dictionary<Type, PropertyInfo[]> _numericPropsCache = new Dictionary<Type, PropertyInfo[]>();
        private static readonly Dictionary<Type, FieldInfo[]> _refFieldsCache = new Dictionary<Type, FieldInfo[]>();
        private static readonly Dictionary<Type, PropertyInfo[]> _refPropsCache = new Dictionary<Type, PropertyInfo[]>();

        private static int _nextTrackedId = 1000000;
        private static int _trackOps = 0;
#if NET35
        private static readonly Dictionary<int, WeakReference> _trackedObjects = new Dictionary<int, WeakReference>();
        private static readonly object _trackedObjectsLock = new object();
#else
        private static readonly ConcurrentDictionary<int, WeakReference<object>> _trackedObjects = new ConcurrentDictionary<int, WeakReference<object>>();
#endif

#if NET35
        private static readonly Dictionary<int, object> _pinnedObjects = new Dictionary<int, object>();
        private static readonly object _pinnedObjectsLock = new object();
#else
        private static readonly ConcurrentDictionary<int, object> _pinnedObjects = new ConcurrentDictionary<int, object>();
#endif

        private sealed class GameLogEntry
        {
            public long Seq;
            public long UtcTicks;
            public int Level;
            public string Message;
            public string Stack;
        }

        private static readonly object _gameLogLock = new object();
        private static long _gameLogSeq = 0;
        private static readonly List<GameLogEntry> _gameLogs = new List<GameLogEntry>(4096);
        private static object _unityLogDelegate = null;
        private static EventInfo _unityLogEvent = null;

        private static readonly object _typeResolveCacheLock = new object();
#if NET35
        private static readonly Dictionary<string, Type> _unityTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Type> _resolvedTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
#else
        private static readonly Dictionary<string, Type> _unityTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Type> _resolvedTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
#endif
        private static readonly object _memberCacheLock = new object();
        private static readonly Dictionary<string, PropertyInfo> _propertyCache = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        private static readonly Dictionary<string, MethodInfo> _methodCache = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);

        private static readonly object _factoryRegistryLock = new object();
        private static int _nextFactoryMethodId = 1;
        private static readonly Dictionary<int, FactoryMethodEntry> _factoryMethodRegistry = new Dictionary<int, FactoryMethodEntry>();
        private static readonly List<MethodInfo> _factoryCandidates = new List<MethodInfo>();
        private static long _factoryIndexTicks = 0;

        private static readonly string HarmonyId = "MyRuntimeTool.UniversalHarmony";
        private static readonly Dictionary<string, MethodBase> _appliedHarmonyPatches = new Dictionary<string, MethodBase>(StringComparer.OrdinalIgnoreCase);
        private static readonly string CheatHarmonyId = "MyRuntimeTool.UniversalCheats";
        private static readonly List<MethodBase> _godModePatchedMethods = new List<MethodBase>();
        private static readonly List<GodModeOverride> _godModeOverrides = new List<GodModeOverride>();

        private sealed class BoundNumericMember
        {
            public object Target;
            public MemberInfo Member;
            public Type ValueType;
            public double LastKnownValue;
            public bool IsValid => Target != null && Member != null && ValueType != null;
        }

        private sealed class MainThreadRequest
        {
            public Func<string> Work;
#if NET35
            public ManualResetEvent Done;
#else
            public ManualResetEventSlim Done;
#endif
            public string Result;
            public Exception Error;
        }

        private sealed class FactoryMethodEntry
        {
            public MethodInfo Method;
            public WeakReference Target;
        }
        
        /// <summary>
        /// MelonLoader mod entry point - UNIVERSAL COMPATIBILITY
        /// </summary>
        public override void OnInitializeMelon()
        {
            try
            {
                _mainThreadId = GetManagedThreadId();
                MelonLoader.MelonLogger.Msg("=== UNIVERSAL MELONMOD INITIALIZING ===");
                
                // Detect environment for universal compatibility
                DetectEnvironment();
                ReflectionHelper.IsIL2CPP = _isIL2CPP;
                
                // Initialize IPC Router and Commands
                ReflectionCommands.Register();
                ObjectCommands.Register();
                SceneCommands.Register();
                CheatCommands.Register();
                OverlayCommands.Register();
                GameCommands.Register();
                CameraCommands.Register();
                AnalysisCommands.Register();
                
                // Initialize pipe server for external tool connections
                InitializePipeServer();
                
                // Set game directory for pipe server
                SetGameDirectory();

                TryInitializeUnityLogHook();
                
                MelonLoader.MelonLogger.Msg("Universal MelonMod initialized successfully!");
                MelonLoader.MelonLogger.Msg($"Environment: IL2CPP={_isIL2CPP}, Mono={_isMono}, MelonLoader={_melonLoaderVersion}");
                MelonLoader.MelonLogger.Msg("Ready for runtime tool communication via universal pipe");
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Error($"Failed to initialize Universal MelonMod: {ex.Message}");
                MelonLoader.MelonLogger.Error(ex.StackTrace);
            }
        }

        public override void OnUpdate()
        {
            if (_mainThreadId == 0)
                _mainThreadId = GetManagedThreadId();

            UpdateFps();
            ForceRunInBackground();
            //MaintainCheats();
            CheatManager.OnUpdate();
            SceneSyncManager.OnUpdate();
            CameraManager.OnUpdate();
            BehaviorAnalyzer.OnUpdate();
            UpdateTelemetrySampler();
            UpdateDiscoveryScanJobs();
            UpdateDiscoveryObservationSessions();
            if (_freeCameraEnabled)
                UpdateFreeCameraControls();

            var processed = 0;
            while (processed < 32)
            {
                MainThreadRequest request = null;
#if NET35
                lock (_mainThreadQueueLock)
                {
                    if (_mainThreadQueue.Count > 0)
                        request = _mainThreadQueue.Dequeue();
                }
                if (request == null)
                    break;
#else
                if (!_mainThreadQueue.TryDequeue(out request))
                    break;
#endif

                processed++;
                try
                {
                    request.Result = request.Work?.Invoke() ?? "ERROR|No work";
                }
                catch (Exception ex)
                {
                    request.Error = ex;
                    request.Result = $"ERROR|{ex.Message}";
                }
                finally
                {
                    try { request.Done?.Set(); } catch { }
                }
            }
        }

        private static void MaintainCheats()
        {
            DynamicCheat[] dyn = null;
            lock (_discoverySync)
            {
                if (_dynamicCheats.Count > 0)
                    dyn = _dynamicCheats.Values.ToArray();
            }

            if (!_infiniteHealthEnabled && !_infiniteAmmoEnabled && !_infiniteStaminaEnabled && !_godModeEnabled && (dyn == null || dyn.Length == 0))
                return;

            if (_infiniteHealthEnabled || _godModeEnabled)
            {
                EnsureHealthBinding();
                if (_healthMember != null && _healthMember.IsValid)
                {
                    TrySetNumeric(_healthMember, Math.Max(_healthMember.LastKnownValue, 99999));
                }
            }

            if (_infiniteAmmoEnabled)
            {
                EnsureAmmoBinding();
                if (_ammoMember != null && _ammoMember.IsValid)
                {
                    TrySetNumeric(_ammoMember, Math.Max(_ammoMember.LastKnownValue, 99999));
                }
            }

            if (_infiniteStaminaEnabled)
            {
                EnsureStaminaBinding();
                if (_staminaMember != null && _staminaMember.IsValid)
                {
                    TrySetNumeric(_staminaMember, Math.Max(_staminaMember.LastKnownValue, 99999));
                }
            }

            if (dyn != null && dyn.Length != 0)
                MaintainDynamicCheats(dyn);
        }

        private static void MaintainDynamicCheats(DynamicCheat[] dyn)
        {
            if (dyn == null || dyn.Length == 0)
                return;

            for (var i = 0; i < dyn.Length; i++)
            {
                var cheat = dyn[i];
                if (cheat == null || cheat.Member == null)
                    continue;

                object target = null;
                if (!cheat.IsStatic)
                {
                    try { target = cheat.Component?.Target; } catch { target = null; }
                    if (target == null)
                        continue;
                }

                if (cheat.Mode == DynamicCheatMode.NoDecrease && cheat.Kind == DiscoveryCandidateKind.Numeric && cheat.CanWrite)
                {
                    if (TryReadNumericMemberValue(target, cheat.Member, cheat.IsStatic, out var current))
                    {
                        if (cheat.LastResolvedTicks == 0)
                        {
                            cheat.NumericValue = current;
                            cheat.LastResolvedTicks = Stopwatch.GetTimestamp();
                        }
                        else if (current + 1e-9 < cheat.NumericValue)
                        {
                            TryWriteNumericMemberValue(target, cheat.Member, cheat.IsStatic, cheat.NumericValue);
                        }
                        else
                        {
                            cheat.NumericValue = current;
                        }
                    }
                    continue;
                }

                if (!cheat.CanWrite)
                    continue;

                if (cheat.Kind == DiscoveryCandidateKind.Numeric)
                {
                    if (!TryReadNumericMemberValue(target, cheat.Member, cheat.IsStatic, out var current))
                        continue;

                    if (cheat.Mode == DynamicCheatMode.Freeze)
                    {
                        TryWriteNumericMemberValue(target, cheat.Member, cheat.IsStatic, cheat.NumericValue);
                        continue;
                    }

                    if (cheat.Mode == DynamicCheatMode.ClampMin)
                    {
                        if (current + 1e-9 < cheat.NumericValue)
                            TryWriteNumericMemberValue(target, cheat.Member, cheat.IsStatic, cheat.NumericValue);
                        continue;
                    }

                    if (cheat.Mode == DynamicCheatMode.ClampMax)
                    {
                        if (current - 1e-9 > cheat.NumericValue)
                            TryWriteNumericMemberValue(target, cheat.Member, cheat.IsStatic, cheat.NumericValue);
                        continue;
                    }

                    if (cheat.Mode == DynamicCheatMode.AutoRefill)
                    {
                        var max = double.NaN;
                        if (cheat.MaxMember != null)
                        {
                            object maxTarget = null;
                            if (!cheat.IsStatic)
                            {
                                try { maxTarget = cheat.MaxComponent?.Target; } catch { maxTarget = null; }
                            }
                            if (cheat.IsStatic || maxTarget != null)
                            {
                                if (!TryReadNumericMemberValue(maxTarget, cheat.MaxMember, cheat.IsStatic, out max))
                                    max = double.NaN;
                            }
                        }

                        if (double.IsNaN(max))
                            max = cheat.SecondaryNumericValue;

                        if (!double.IsNaN(max) && max > 0)
                        {
                            var threshold = cheat.NumericValue;
                            var trigger = threshold > 0 && threshold <= 1.5 ? (max * threshold) : threshold;
                            if (trigger <= 0)
                                trigger = max * 0.95;

                            if (current + 1e-9 < trigger)
                                TryWriteNumericMemberValue(target, cheat.Member, cheat.IsStatic, max);
                        }
                        continue;
                    }
                }
                else if (cheat.Kind == DiscoveryCandidateKind.Boolean)
                {
                    if (cheat.Mode == DynamicCheatMode.Freeze)
                        TryWriteBoolMemberValue(target, cheat.Member, cheat.IsStatic, cheat.BoolValue);
                }
                else if (cheat.Kind == DiscoveryCandidateKind.Enum)
                {
                    if (cheat.Mode == DynamicCheatMode.Freeze)
                        TryWriteEnumMemberValue(target, cheat.Member, cheat.IsStatic, cheat.EnumValue);
                }
                else if (cheat.Kind == DiscoveryCandidateKind.CollectionCount)
                {
                    if (cheat.Mode == DynamicCheatMode.Freeze)
                    {
                        if (TryReadNumericMemberValue(target, cheat.Member, cheat.IsStatic, out var c))
                        {
                            var min = cheat.NumericValue;
                            if (c + 1e-9 < min)
                                TryWriteNumericMemberValue(target, cheat.Member, cheat.IsStatic, min);
                        }
                    }
                }
            }
        }

        public override void OnGUI()
        {
            OverlayManager.OnGUI();

            if (!_fpsOverlayEnabled && !_colliderOverlayEnabled && IsNullOrWhiteSpace(_discoveryOverlayText))
                return;

            if (_fpsOverlayEnabled)
            {
                try
                {
                    var guiType = FindUnityType("UnityEngine.GUI");
                    var rectType = FindUnityType("UnityEngine.Rect");
                    if (guiType == null || rectType == null)
                        return;

                    var labelMethod = guiType.GetMethod("Label", new[] { rectType, typeof(string) });
                    if (labelMethod == null)
                        return;

                    var rect = Activator.CreateInstance(rectType, new object[] { 10f, 10f, 400f, 40f });
                    var memMb = (Process.GetCurrentProcess().PrivateMemorySize64 / 1024d / 1024d).ToString("F0");
                    labelMethod.Invoke(null, new object[] { rect, $"FPS: {_fps:F1} | MEM: {memMb} MB" });
                }
                catch
                {
                }
            }

            if (_colliderOverlayEnabled)
            {
                try
                {
                    DrawColliderOverlay();
                }
                catch
                {
                }
            }

            if (!IsNullOrWhiteSpace(_discoveryOverlayText))
            {
                try
                {
                    var guiType = FindUnityType("UnityEngine.GUI");
                    var rectType = FindUnityType("UnityEngine.Rect");
                    if (guiType == null || rectType == null)
                        return;

                    var labelMethod = guiType.GetMethod("Label", new[] { rectType, typeof(string) });
                    if (labelMethod == null)
                        return;

                    var pct = Math.Max(0, Math.Min(1, _discoveryOverlayProgress));
                    var rect = Activator.CreateInstance(rectType, new object[] { 10f, 54f, 800f, 40f });
                    labelMethod.Invoke(null, new object[] { rect, $"Discovery: {_discoveryOverlayText} ({(pct * 100).ToString("F0")}%)" });
                }
                catch
                {
                }
            }
        }

        private static void UpdateFps()
        {
            var ticks = _fpsStopwatch.ElapsedTicks;
            if (_lastFpsTicks == 0)
            {
                _lastFpsTicks = ticks;
                return;
            }

            var dt = (ticks - _lastFpsTicks) / (double)Stopwatch.Frequency;
            _lastFpsTicks = ticks;
            if (dt <= 0)
                return;

            var current = 1.0 / dt;
            if (_fps <= 0)
                _fps = current;
            else
                _fps = (_fps * 0.9) + (current * 0.1);
        }

        private static void DrawColliderOverlay()
        {
            var camera = GetMainCamera();
            if (camera == null)
                return;

            var colliderType = FindUnityType("UnityEngine.Collider");
            var unityObjectType = FindUnityType("UnityEngine.Object");
            var guiType = FindUnityType("UnityEngine.GUI");
            var rectType = FindUnityType("UnityEngine.Rect");
            var colorType = FindUnityType("UnityEngine.Color");
            var vector3Type = FindUnityType("UnityEngine.Vector3");
            var texture2dType = FindUnityType("UnityEngine.Texture2D");
            var screenType = FindUnityType("UnityEngine.Screen");
            var textureType = FindUnityType("UnityEngine.Texture");
            if (colliderType == null || unityObjectType == null || guiType == null || rectType == null || colorType == null || vector3Type == null || texture2dType == null || screenType == null || textureType == null)
                return;

            var now = Stopwatch.GetTimestamp();
            if (_colliderOverlayCache == null || _colliderOverlayLastScanTicks == 0 || (now - _colliderOverlayLastScanTicks) > (Stopwatch.Frequency / 2))
            {
                var findMethod = unityObjectType.GetMethod("FindObjectsOfType", new[] { typeof(Type) });
                if (findMethod == null)
                    return;

                _colliderOverlayCache = CoerceToSystemArray(findMethod.Invoke(null, new object[] { colliderType }));
                _colliderOverlayLastScanTicks = now;
            }

            if (_colliderOverlayCache == null || _colliderOverlayCache.Length == 0)
                return;

            var drawTexture = guiType.GetMethod("DrawTexture", BindingFlags.Public | BindingFlags.Static, null, new[] { rectType, textureType }, null);
            if (drawTexture == null)
                return;

            var guiColorProp = guiType.GetProperty("color", BindingFlags.Public | BindingFlags.Static);
            if (guiColorProp == null || !guiColorProp.CanWrite)
                return;

            var whiteTextureProp = texture2dType.GetProperty("whiteTexture", BindingFlags.Public | BindingFlags.Static);
            if (whiteTextureProp == null || !whiteTextureProp.CanRead)
                return;

            var worldToScreen = camera.GetType().GetMethod("WorldToScreenPoint", BindingFlags.Public | BindingFlags.Instance, null, new[] { vector3Type }, null);
            if (worldToScreen == null)
                return;

            var screenHeightProp = screenType.GetProperty("height", BindingFlags.Public | BindingFlags.Static);
            if (screenHeightProp == null || !screenHeightProp.CanRead)
                return;
            var screenHeight = Convert.ToSingle(screenHeightProp.GetValueCompat(null));

            object prevGuiColor = null;
            try { prevGuiColor = guiColorProp.GetValueCompat(null); } catch { prevGuiColor = null; }

            var overlayColor = Activator.CreateInstance(colorType, new object[] { 1f, 0.2f, 0.2f, 0.8f });
            var whiteTexture = whiteTextureProp.GetValueCompat(null);
            if (whiteTexture == null)
                return;

            try
            {
                guiColorProp.SetValueCompat(null, overlayColor);

                var boundsProp = colliderType.GetProperty("bounds", BindingFlags.Public | BindingFlags.Instance);
                if (boundsProp == null || !boundsProp.CanRead)
                    return;

                var budget = 220;
                for (var i = 0; i < _colliderOverlayCache.Length && budget-- > 0; i++)
                {
                    var c = _colliderOverlayCache.GetValue(i);
                    if (c == null)
                        continue;

                    object bounds;
                    try { bounds = boundsProp.GetValueCompat(c); } catch { bounds = null; }
                    if (bounds == null)
                        continue;

                    var bt = bounds.GetType();
                    var centerProp = bt.GetProperty("center", BindingFlags.Public | BindingFlags.Instance);
                    var extentsProp = bt.GetProperty("extents", BindingFlags.Public | BindingFlags.Instance);
                    if (centerProp == null || extentsProp == null)
                        continue;

                    object centerObj = null;
                    object extentsObj = null;
                    try { centerObj = centerProp.GetValueCompat(bounds); } catch { centerObj = null; }
                    try { extentsObj = extentsProp.GetValueCompat(bounds); } catch { extentsObj = null; }
                    if (centerObj == null || extentsObj == null)
                        continue;

                    var center = ReadVector3(centerObj);
                    var extents = ReadVector3(extentsObj);

                    float minX = float.PositiveInfinity;
                    float minY = float.PositiveInfinity;
                    float maxX = float.NegativeInfinity;
                    float maxY = float.NegativeInfinity;
                    var any = false;

                    for (var xi = 0; xi < 2; xi++)
                    for (var yi = 0; yi < 2; yi++)
                    for (var zi = 0; zi < 2; zi++)
                    {
                        var x = center.X + (xi == 0 ? -extents.X : extents.X);
                        var y = center.Y + (yi == 0 ? -extents.Y : extents.Y);
                        var z = center.Z + (zi == 0 ? -extents.Z : extents.Z);
                        var corner = Activator.CreateInstance(vector3Type, new object[] { x, y, z });

                        object screenObj = null;
                        try { screenObj = worldToScreen.Invoke(camera, new object[] { corner }); } catch { screenObj = null; }
                        if (screenObj == null)
                            continue;

                        var sp = ReadVector3(screenObj);
                        if (sp.Z <= 0.01f)
                            continue;

                        var sx = sp.X;
                        var sy = screenHeight - sp.Y;
                        if (!any)
                        {
                            minX = maxX = sx;
                            minY = maxY = sy;
                            any = true;
                        }
                        else
                        {
                            if (sx < minX) minX = sx;
                            if (sx > maxX) maxX = sx;
                            if (sy < minY) minY = sy;
                            if (sy > maxY) maxY = sy;
                        }
                    }

                    if (!any)
                        continue;

                    var w = maxX - minX;
                    var h = maxY - minY;
                    if (w < 2f || h < 2f)
                        continue;

                    var thickness = 1f;
                    var top = Activator.CreateInstance(rectType, new object[] { minX, minY, w, thickness });
                    var bottom = Activator.CreateInstance(rectType, new object[] { minX, maxY - thickness, w, thickness });
                    var left = Activator.CreateInstance(rectType, new object[] { minX, minY, thickness, h });
                    var right = Activator.CreateInstance(rectType, new object[] { maxX - thickness, minY, thickness, h });

                    drawTexture.Invoke(null, new object[] { top, whiteTexture });
                    drawTexture.Invoke(null, new object[] { bottom, whiteTexture });
                    drawTexture.Invoke(null, new object[] { left, whiteTexture });
                    drawTexture.Invoke(null, new object[] { right, whiteTexture });
                }
            }
            finally
            {
                try
                {
                    if (prevGuiColor != null)
                        guiColorProp.SetValueCompat(null, prevGuiColor);
                }
                catch
                {
                }
            }
        }

        private static void ForceRunInBackground()
        {
            try
            {
                var now = Stopwatch.GetTimestamp();
                if (_runInBackgroundForced && _lastRunInBackgroundTicks != 0 && (now - _lastRunInBackgroundTicks) < Stopwatch.Frequency * 2)
                    return;
                if (!_runInBackgroundForced && _lastRunInBackgroundTicks != 0 && (now - _lastRunInBackgroundTicks) < Stopwatch.Frequency * 1)
                    return;

                _lastRunInBackgroundTicks = now;

                var appType = FindUnityType("UnityEngine.Application");
                if (appType == null)
                    return;

                var prop = appType.GetProperty("runInBackground", BindingFlags.Public | BindingFlags.Static);
                if (prop == null || prop.PropertyType != typeof(bool) || !prop.CanWrite)
                    return;

                bool current;
                try { current = Convert.ToBoolean(prop.GetValueCompat(null)); }
                catch { current = false; }
                if (current)
                {
                    _runInBackgroundForced = true;
                    return;
                }

                try { prop.SetValueCompat(null, true); } catch { }
                try { _runInBackgroundForced = Convert.ToBoolean(prop.GetValueCompat(null)); } catch { _runInBackgroundForced = true; }
            }
            catch
            {
            }
        }

        private static string RunOnMainThread(Func<string> work, int timeoutMs = 5000)
        {
            if (GetManagedThreadId() == _mainThreadId)
                return work?.Invoke() ?? "ERROR|No work";

            var request = new MainThreadRequest
            {
                Work = work,
#if NET35
                Done = new ManualResetEvent(false)
#else
                Done = new ManualResetEventSlim(false)
#endif
            };

#if NET35
            lock (_mainThreadQueueLock)
            {
                _mainThreadQueue.Enqueue(request);
            }
            if (!request.Done.WaitOne(timeoutMs))
                return "ERROR|Main thread timeout";
#else
            _mainThreadQueue.Enqueue(request);
            if (!request.Done.Wait(timeoutMs))
                return "ERROR|Main thread timeout";
#endif

            return request.Result ?? "ERROR|No result";
        }

        private static Type FindUnityType(string fullName)
        {
            try
            {
                if (IsNullOrWhiteSpace(fullName))
                    return null;

#if NET35
                Type cached;
                lock (_typeResolveCacheLock)
                {
                    if (_unityTypeCache.Count > 10000)
                        _unityTypeCache.Clear();
                    if (_unityTypeCache.TryGetValue(fullName, out cached))
                        return cached;
                }
#else
                lock (_typeResolveCacheLock)
                {
                    if (_unityTypeCache.Count > 10000)
                        _unityTypeCache.Clear();
                    if (_unityTypeCache.TryGetValue(fullName, out var cached))
                        return cached;
                }
#endif

                try
                {
                    var direct = Type.GetType(fullName, false);
                    if (direct != null)
                    {
                        lock (_typeResolveCacheLock) { _unityTypeCache[fullName] = direct; }
                        return direct;
                    }
                }
                catch
                {
                }

                if (_isIL2CPP && fullName.StartsWith("Il2CppSystem.", StringComparison.Ordinal))
                {
                    var candidates = new[]
                    {
                        "Il2Cppmscorlib",
                        "Il2CppSystem",
                        "Il2CppInterop.Runtime"
                    };

                    for (var i = 0; i < candidates.Length; i++)
                    {
                        try
                        {
                            var qualified = Type.GetType(fullName + ", " + candidates[i], false);
                            if (qualified != null)
                            {
                                lock (_typeResolveCacheLock) { _unityTypeCache[fullName] = qualified; }
                                return qualified;
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = asm.GetType(fullName, false);
                    if (type != null)
                    {
#if NET35
                        lock (_typeResolveCacheLock) { _unityTypeCache[fullName] = type; }
#else
                        lock (_typeResolveCacheLock) { _unityTypeCache[fullName] = type; }
#endif
                        return type;
                    }
                }
            }
            catch
            {
            }

            return null;
        }
        
        /// <summary>
        /// Detect game environment using MelonLoader's built-in detection
        /// </summary>
        private static void DetectEnvironment()
        {
            try
            {
                _melonLoaderVersion = typeof(MelonLoader.MelonMod).Assembly.GetName().Version?.ToString() ?? "Unknown";

                var melonUtilsType = Type.GetType("MelonLoader.MelonUtils, MelonLoader", throwOnError: false);

                bool? TryInvokeBool(string method)
                {
                    try
                    {
                        var mi = melonUtilsType?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
                        if (mi == null || mi.ReturnType != typeof(bool))
                            return null;
                        return (bool)mi.Invoke(null, null);
                    }
                    catch
                    {
                        return null;
                    }
                }

                var il2cpp = TryInvokeBool("IsGameIl2Cpp");
                _isIL2CPP = il2cpp ?? DetectIL2CPPEnvironment();

                var oldMono = TryInvokeBool("IsOldMono");
                var gameMono = TryInvokeBool("IsGameMono");
                _isMono = gameMono ?? oldMono ?? (!_isIL2CPP || DetectMonoEnvironment());
                
                MelonLoader.MelonLogger.Msg($"Detected: MelonLoader v{_melonLoaderVersion}, IL2CPP: {_isIL2CPP}, Mono: {_isMono}");
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Warning($"Environment detection failed: {ex.Message}");
                // Default to most common configuration (IL2CPP)
                _isIL2CPP = true;
                _isMono = false;
            }
        }
        
        /// <summary>
        /// Universal IL2CPP detection across all versions
        /// </summary>
        private static bool DetectIL2CPPEnvironment()
        {
            try
            {
                // Method 1: Check for Il2Cpp assemblies in current domain
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = assembly.GetName().Name ?? "";
                    if (name.Contains("Il2Cpp") || name.Contains("GameAssembly") || name.Contains("UnityPlayer"))
                    {
                        MelonLoader.MelonLogger.Msg($"IL2CPP detected via assembly: {name}");
                        return true;
                    }
                }
                
                // Method 2: Check for Il2CppInterop presence
                Type il2CppType = Type.GetType("Il2CppInterop.Runtime.Il2CppRuntime", false);
                if (il2CppType != null)
                {
                    MelonLoader.MelonLogger.Msg("IL2CPP detected via Il2CppInterop runtime");
                    return true;
                }
                
                // Method 3: Check common IL2CPP directories
                string gameDir = AppDomain.CurrentDomain.BaseDirectory;
                string il2cppAssembliesPath = Path.Combine(Path.Combine(gameDir, "MelonLoader"), "Il2CppAssemblies");
                if (Directory.Exists(il2cppAssembliesPath))
                {
                    MelonLoader.MelonLogger.Msg("IL2CPP detected via MelonLoader Il2CppAssemblies directory");
                    return true;
                }
                
                // Method 4: Check for Unity IL2CPP specific files
                string dataDir = Path.Combine(gameDir, "*_Data");
                var dataDirs = Directory.GetDirectories(gameDir, "*_Data");
                foreach (var dir in dataDirs)
                {
                    if (File.Exists(Path.Combine(dir, "il2cpp_data")) ||
                        File.Exists(Path.Combine(Path.Combine(dir, "Resources"), "il2cpp_metadata")))
                    {
                        MelonLoader.MelonLogger.Msg("IL2CPP detected via data directory structure");
                        return true;
                    }
                }
                
                MelonLoader.MelonLogger.Msg("IL2CPP not detected - assuming Mono");
                return false;
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Warning($"IL2CPP detection failed: {ex.Message}");
                return false; // Default to Mono on failure
            }
        }
        
        /// <summary>
        /// Universal Mono detection across all versions
        /// </summary>
        private static bool DetectMonoEnvironment()
        {
            try
            {
                // Method 1: Check for Mono-specific assemblies
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = assembly.GetName().Name ?? "";
                    if (name.Contains("Mono.") || name.Contains("mono.") || name.Contains("UnityEngine") && !name.Contains("Il2Cpp"))
                    {
                        MelonLoader.MelonLogger.Msg($"Mono detected via assembly: {name}");
                        return true;
                    }
                }
                
                // Method 2: Check for Managed assemblies directory
                string gameDir = AppDomain.CurrentDomain.BaseDirectory;
                string managedPath = Path.Combine(Path.Combine(gameDir, "*_Data"), "Managed");
                var managedDirs = Directory.GetDirectories(gameDir, "*_Data");
                foreach (var dataDir in managedDirs)
                {
                    string managedDir = Path.Combine(dataDir, "Managed");
                    if (Directory.Exists(managedDir) && Directory.GetFiles(managedDir, "*.dll").Length > 0)
                    {
                        MelonLoader.MelonLogger.Msg("Mono detected via Managed assemblies directory");
                        return true;
                    }
                }
                
                // Method 3: Check if we're not IL2CPP (fallback)
                if (!DetectIL2CPPEnvironment())
                {
                    MelonLoader.MelonLogger.Msg("Mono detected via IL2CPP absence");
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Warning($"Mono detection failed: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Initialize pipe client for communication with external tool
        /// </summary>
        private static void InitializePipeServer()
        {
            try
            {
                _pipeThread = new Thread(PipeServerThread);
                _pipeThread.IsBackground = true;
                _pipeThread.Start();
                
                MelonLoader.MelonLogger.Msg("Universal pipe server initialized - waiting for external tool connections...");
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Error($"Failed to initialize pipe server: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Pipe server thread for universal communication - listens for external tool connections
        /// </summary>
        private static void PipeServerThread()
        {
            while (true)
            {
                NamedPipeServerStream pipeServer = null;
                StreamReader reader = null;
                StreamWriter writer = null;
                
                try
                {
                    // Create pipe server and wait for connections
                    pipeServer = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, -1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    MelonLoader.MelonLogger.Msg($"Universal pipe server created (pipe='{_pipeName}', pid={System.Diagnostics.Process.GetCurrentProcess().Id}) - waiting for external tool connection...");
                    
                    // Wait for connection from external tool
                    pipeServer.WaitForConnection();
                    _isConnected = true;
                    
                    MelonLoader.MelonLogger.Msg("External tool connected to universal MelonMod pipe!");
                    
                    // Set up stream readers/writers
                    reader = new StreamReader(pipeServer);
                    writer = new StreamWriter(pipeServer) { AutoFlush = true };
                    
                    // Set game directory after successful connection
                    SetGameDirectory();
                    
                    // Main communication loop
                    while (_isConnected && pipeServer.IsConnected)
                    {
                        try
                        {
                            // Read command from external tool
                            string command = reader.ReadLine();
                            if (command == null)
                                break;
                            if (command.Length == 0)
                                continue;

                            string response;
                            try
                            {
                                response = ProcessCommand(command) ?? "ERROR|Null response";
                            }
                            catch (Exception ex)
                            {
                                MelonLoader.MelonLogger.Error($"[IPC] ProcessCommand error: {ex}");
                                response = $"ERROR|{ex.Message}";
                            }

                            try
                            {
                                writer.WriteLine(response);
                            }
                            catch (Exception ex)
                            {
                                MelonLoader.MelonLogger.Error($"[IPC] Pipe write error: {ex}");
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            MelonLoader.MelonLogger.Error($"Pipe communication error: {ex.Message}");
                            break;
                        }
                    }
                    
                    _isConnected = false;
                    MelonLoader.MelonLogger.Msg("External tool disconnected from universal MelonMod pipe");
                }
                catch (Exception ex)
                {
                    _isConnected = false;
                    MelonLoader.MelonLogger.Error($"Pipe server error: {ex.Message}");
                }
                finally
                {
                    // Clean up resources
                    try { writer?.Dispose(); } catch { }
                    try { reader?.Dispose(); } catch { }
                    try { pipeServer?.Dispose(); } catch { }
                }
                
                Thread.Sleep(_reconnectInterval);
            }
        }
        
        /// <summary>
        /// Process commands from external tool with universal compatibility
        /// </summary>
        private static string ProcessCommand(string command)
        {
            try
            {
                // Try new IPC Router first
                if (IpcRouter.TryProcess(command, out var result))
                    return result;

                // Fallback to legacy switch statement
                string[] parts = null;
                string cmdType;
                string args = null;

                if (command.Contains("|"))
                {
                    parts = command.Split('|');
                    if (parts.Length == 0)
                        return "ERROR|Invalid command format";
                    cmdType = parts[0].ToUpperInvariant();
                }
                else
                {
                    var trimmed = command.Trim();
                    var spaceIndex = trimmed.IndexOf(' ');
                    if (spaceIndex < 0)
                    {
                        cmdType = trimmed.ToUpperInvariant();
                    }
                    else
                    {
                        cmdType = trimmed.Substring(0, spaceIndex).ToUpperInvariant();
                        args = trimmed.Substring(spaceIndex + 1).Trim();
                    }
                }
                
                switch (cmdType)
                {
                    case "PING":
                        return "PONG|UniversalMelonMod|v1.0";
                    
                    case "GAME_STATUS":
                        return $"GAME_STATUS|{IsGameConnected()}";
                    
                    case "GET_GAME_DIRECTORY":
                        return $"GAME_DIR|{_gameDirectory ?? "Unknown"}";

                    case "GET_CAPABILITIES":
                        return RunOnMainThread(GetCapabilitiesInfo);

                    case "GET_BINDINGS":
                        return RunOnMainThread(GetBindingsInfo);

                    case "GET_KEY_OBJECTS":
                        return RunOnMainThread(GetKeyObjectsInfo);

                    case "GET_COMPONENTS":
                        return parts.Length > 1 ? RunOnMainThread(() => GetComponentsInfo(parts[1])) : "ERROR|Invalid get components format";

                    case "GET_NUMERIC_MEMBERS":
                        return parts.Length > 1 ? RunOnMainThread(() => GetNumericMembersInfo(parts[1])) : "ERROR|Invalid get numeric members format";

                    case "SET_BINDING":
                        return parts.Length > 5 ? RunOnMainThread(() => SetNumericBinding(parts[1], parts[2], parts[3], parts[4], parts[5])) : "ERROR|Invalid set binding format";

                    case "SET_BINDING_KEY":
                        return parts.Length > 6 ? RunOnMainThread(() => SetNumericBindingByKey(parts[1], parts[2], parts[3], parts[4], parts[5], parts[6])) : "ERROR|Invalid set binding key format";

                    case "AUTO_DETECT_BINDING":
                        return parts.Length > 1 ? RunOnMainThread(() => AutoDetectBinding(parts[1])) : "ERROR|Invalid auto detect binding format";

                    case "VALUE_SCAN":
                        return parts.Length > 1 ? RunOnMainThread(() => ValueScan(parts)) : "ERROR|Invalid value scan format";

                    case "DISCOVERY_SCAN":
                        return RunOnMainThread(() => DiscoveryScan(parts));

                    case "DISCOVERY_SCAN_STATUS":
                        return parts.Length > 1 ? RunOnMainThread(() => DiscoveryScanStatus(parts[1])) : "ERROR|Invalid discovery scan status format";

                    case "DISCOVERY_SCAN_CANCEL":
                        return parts.Length > 1 ? RunOnMainThread(() => DiscoveryScanCancel(parts[1])) : "ERROR|Invalid discovery scan cancel format";

                    case "DISCOVERY_SCAN_PAGE":
                        return parts.Length > 3 ? DiscoveryScanPage(parts[1], parts[2], parts[3]) : "ERROR|Invalid discovery scan page format";

                    case "DISCOVERY_SNAPSHOT":
                        return parts.Length > 2 ? RunOnMainThread(() => DiscoverySnapshot(parts)) : "ERROR|Invalid discovery snapshot format";

                    case "DISCOVERY_OBSERVE_START":
                        return parts.Length > 3 ? RunOnMainThread(() => DiscoveryObserveStart(parts)) : "ERROR|Invalid discovery observe start format";

                    case "DISCOVERY_OBSERVE_STOP":
                        return parts.Length > 1 ? RunOnMainThread(() => DiscoveryObserveStop(parts[1])) : "ERROR|Invalid discovery observe stop format";

                    case "DISCOVERY_OBSERVE_PULL":
                        return parts.Length > 1 ? RunOnMainThread(() => DiscoveryObservePull(parts)) : "ERROR|Invalid discovery observe pull format";

                    case "DISCOVERY_OBSERVE_STATUS":
                        return parts.Length > 1 ? RunOnMainThread(() => DiscoveryObserveStatus(parts[1])) : "ERROR|Invalid discovery observe status format";

                    case "DISCOVERY_OBSERVE_SUMMARY":
                        return parts.Length > 1 ? RunOnMainThread(() => DiscoveryObserveSummary(parts[1])) : "ERROR|Invalid discovery observe summary format";

                    case "DISCOVERY_MARK_EVENT":
                        return parts.Length > 2 ? RunOnMainThread(() => DiscoveryMarkEvent(parts[1], parts[2])) : "ERROR|Invalid discovery mark event format";

                    case "DISCOVERY_EXPERIMENT_BEGIN":
                        return parts.Length > 3 ? RunOnMainThread(() => DiscoveryExperimentBegin(parts)) : "ERROR|Invalid discovery experiment begin format";

                    case "DISCOVERY_EXPERIMENT_END":
                        return parts.Length > 1 ? RunOnMainThread(() => DiscoveryExperimentEnd(parts)) : "ERROR|Invalid discovery experiment end format";

                    case "DISCOVERY_EXPERIMENT_CANCEL":
                        return parts.Length > 1 ? RunOnMainThread(() => DiscoveryExperimentCancel(parts[1])) : "ERROR|Invalid discovery experiment cancel format";

                    case "DISCOVERY_CHEAT_BIND":
                        return parts.Length > 3 ? RunOnMainThread(() => DiscoveryCheatBind(parts)) : "ERROR|Invalid discovery cheat bind format";

                    case "DISCOVERY_CHEAT_UNBIND":
                        return parts.Length > 1 ? RunOnMainThread(() => DiscoveryCheatUnbind(parts[1])) : "ERROR|Invalid discovery cheat unbind format";

                    case "DISCOVERY_CHEAT_LIST":
                        return RunOnMainThread(DiscoveryCheatList);

                    case "DISCOVERY_WRITE_PROBE":
                        return parts.Length > 2 ? RunOnMainThread(() => DiscoveryWriteProbe(parts)) : "ERROR|Invalid discovery write probe format";
                    
                    case "GET_STATS_BATCH":
                        return RunOnMainThread(() => BuildStatsBatch(parts));
                    
                    case "GET_FPS":
                        return $"FPS|{_fps:F1}";
                    
                    case "GET_MEMORY":
                        return $"MEMORY|{(Process.GetCurrentProcess().PrivateMemorySize64 / 1024d / 1024d):F0}";
                    
                    case "GET_TIME":
                        return RunOnMainThread(GetTimeInfo);
                    
                    case "GET_LEVEL":
                        return RunOnMainThread(GetLevelInfo);
                    
                    case "GET_POSITION":
                        return RunOnMainThread(GetPlayerPositionInfo);
                    
                    case "GET_HEALTH":
                        return RunOnMainThread(GetPlayerHealthInfo);
                        
                    case "GET_ASSEMBLIES":
                        return GetAssembliesInfo();
                        
                    case "GET_ASSEMBLY_DETAILS":
                        return parts.Length > 1 ? GetAssemblyDetails(parts[1]) : "ERROR|Invalid assembly details format";
                        
                    case "GET_TYPES":
                        return parts.Length > 1 ? GetTypesInfo(parts[1]) : "ERROR|Invalid types format";
                        
                    case "GET_TYPE_DETAILS":
                        return parts.Length > 1 ? GetTypeDetails(parts[1]) : "ERROR|Invalid type details format";
                        
                    case "GET_METHODS":
                        return parts.Length > 1 ? GetMethodsInfo(parts[1]) : "ERROR|Invalid methods format";
                        
                    case "GET_FIELDS":
                        return parts.Length > 1 ? GetFieldsInfo(parts[1]) : "ERROR|Invalid fields format";
                        
                    case "GET_PROPERTIES":
                        return parts.Length > 1 ? GetPropertiesInfo(parts[1]) : "ERROR|Invalid properties format";
                        
                    case "INVOKE":
                        return parts.Length > 3 ? InvokeMethod(parts[1], parts[2], parts[3]) : "ERROR|Invalid invoke format";
                        
                    case "INVOKE_INSTANCE":
                        return parts.Length > 4 ? InvokeInstanceMethod(parts[1], parts[2], parts[3], parts[4]) : "ERROR|Invalid instance invoke format";
                        
                    case "GET_FIELD":
                        return parts.Length > 2 ? GetFieldValue(parts[1], parts[2]) : "ERROR|Invalid get field format";
                        
                    case "GET_FIELD_INSTANCE":
                        return parts.Length > 3 ? GetInstanceFieldValue(parts[1], parts[2], parts[3]) : "ERROR|Invalid get instance field format";
                        
                    case "SET_FIELD":
                        return parts.Length > 3 ? SetFieldValue(parts[1], parts[2], parts[3]) : "ERROR|Invalid set field format";
                        
                    case "SET_FIELD_INSTANCE":
                        return parts.Length > 4 ? SetInstanceFieldValue(parts[1], parts[2], parts[3], parts[4]) : "ERROR|Invalid set instance field format";

                    case "GET_PROPERTY":
                        return parts.Length > 2 ? GetPropertyValue(parts[1], parts[2]) : "ERROR|Invalid get property format";

                    case "SET_PROPERTY":
                        return parts.Length > 3 ? SetPropertyValue(parts[1], parts[2], parts[3]) : "ERROR|Invalid set property format";

                    case "GET_PROPERTY_INSTANCE":
                        return parts.Length > 3 ? GetInstancePropertyValue(parts[1], parts[2], parts[3]) : "ERROR|Invalid get instance property format";

                    case "SET_PROPERTY_INSTANCE":
                        return parts.Length > 4 ? SetInstancePropertyValue(parts[1], parts[2], parts[3], parts[4]) : "ERROR|Invalid set instance property format";
                        
                    case "CREATE_INSTANCE":
                        return parts.Length > 2 ? CreateInstance(parts[1], parts[2]) : "ERROR|Invalid create instance format";
                        
                    case "GET_INSTANCE_FIELDS":
                        return parts.Length > 2 ? GetInstanceFields(parts[1], parts[2]) : "ERROR|Invalid get instance fields format";
                        
                    case "GET_INSTANCE_PROPERTIES":
                        return parts.Length > 2 ? GetInstanceProperties(parts[1], parts[2]) : "ERROR|Invalid get instance properties format";
                        
                    case "GET_INSTANCE_METHODS":
                        return parts.Length > 2 ? GetInstanceMethods(parts[1], parts[2]) : "ERROR|Invalid get instance methods format";
                        
                    case "ENUMERATE_OBJECTS":
                        return parts.Length > 1 ? EnumerateObjects(parts[1]) : "ERROR|Invalid enumerate objects format";
                        
                    case "FIND_OBJECTS":
                        return parts.Length > 2 ? FindObjects(parts[1], parts[2]) : "ERROR|Invalid find objects format";
                        
                    case "GET_OBJECT_INFO":
                        return parts.Length > 2 ? GetObjectInfo(parts[1], parts[2]) : "ERROR|Invalid get object info format";

                    case "FIND_FACTORY_METHODS":
                        return parts.Length > 1 ? FindFactoryMethods(parts[1]) : FindFactoryMethods("");

                    case "FIND_INSTANCE_FACTORY_METHODS":
                        return parts.Length > 2 ? FindInstanceFactoryMethods(parts[1], parts[2]) : "ERROR|Invalid find instance factory format";

                    case "INVOKE_FACTORY_B64":
                        return parts.Length > 1 ? InvokeFactoryB64(parts) : "ERROR|Invalid invoke factory format";
                        
                    case "ENV_INFO":
                        return $"ENV|IL2CPP={_isIL2CPP}|MONO={_isMono}|ML_VERSION={_melonLoaderVersion}|GAME_CONNECTED={IsGameConnected()}";
                    
                    case "PAUSE_GAME":
                        return RunOnMainThread(PauseGame);
                    
                    case "RESUME_GAME":
                        return RunOnMainThread(ResumeGame);
                    
                    case "SET_TIMESCALE":
                        return RunOnMainThread(() => SetTimeScale(args));
                    
                    case "FREEZE_TIME":
                        return RunOnMainThread(ToggleFreezeTime);
                    
                    case "SET_DAYTIME":
                        return RunOnMainThread(SetDayTime);
                    
                    case "SET_NIGHTTIME":
                        return RunOnMainThread(SetNightTime);

                    case "TOGGLE_GODMODE":
                        return RunOnMainThread(ToggleGodMode);
                    
                    case "TOGGLE_INFINITE_HEALTH":
                        return RunOnMainThread(ToggleInfiniteHealth);
                    
                    case "TOGGLE_INFINITE_AMMO":
                        return RunOnMainThread(ToggleInfiniteAmmo);

                    case "TOGGLE_INFINITE_STAMINA":
                        return RunOnMainThread(ToggleInfiniteStamina);

                    case "SET_CAPABILITY_STATE":
                        return parts.Length > 2 ? RunOnMainThread(() => SetCapabilityState(parts[1], parts[2])) : "ERROR|Invalid set capability state format";
                    
                    case "ADD_HEALTH":
                        return RunOnMainThread(() => AddToHealth(args));
                    
                    case "ADD_MONEY":
                        return RunOnMainThread(() => AddToMoney(args));
                    
                    case "ADD_XP":
                        return RunOnMainThread(() => AddToXp(args));

                    case "ADD_STAMINA":
                        return RunOnMainThread(() => AddToStamina(args));
                    
                    case "SKIP_LEVEL":
                        return RunOnMainThread(SkipLevel);
                    
                    case "RESTART_LEVEL":
                        return RunOnMainThread(RestartLevel);
                    
                    case "UNLOCK_ALL":
                        return "ERROR|Unlock all not implemented";
                    
                    case "FREE_CAMERA":
                        return RunOnMainThread(ToggleFreeCamera);
                    
                    case "TOGGLE_NOCLIP":
                        return RunOnMainThread(ToggleNoclip);
                    
                    case "ZOOM_OUT":
                        return RunOnMainThread(ToggleZoomOut);
                    
                    case "FIRST_PERSON":
                        return RunOnMainThread(EnableFirstPerson);
                    
                    case "THIRD_PERSON":
                        return RunOnMainThread(EnableThirdPerson);
                    
                    case "RESET_CAMERA":
                        return RunOnMainThread(ResetCamera);
                    
                    case "SPAWN_ENTITY":
                        return RunOnMainThread(() =>
                        {
                            var effectiveArgs = args;
                            if (parts != null && parts.Length > 1)
                                effectiveArgs = string.Join("|", parts.Skip(1).ToArray());
                            return SpawnEntity(effectiveArgs);
                        });

                    case "DESPAWN":
                        return RunOnMainThread(() => parts.Length > 1 ? DespawnObject(parts[1]) : "ERROR|Invalid despawn format");
                    
                    case "TOGGLE_FPS_DISPLAY":
                        _fpsOverlayEnabled = !_fpsOverlayEnabled;
                        return $"SUCCESS|FPS_DISPLAY|{_fpsOverlayEnabled}";
                    
                    case "TOGGLE_COLLIDERS":
                        return RunOnMainThread(ToggleColliders);
                    
                    case "TOGGLE_WIREFRAME":
                        return RunOnMainThread(ToggleWireframe);
                    
                    case "RELOAD_SCENE":
                        return RunOnMainThread(ReloadScene);
                    
                    case "CRASH_GAME":
                        return RunOnMainThread(CrashGame);
                    
                    case "OPEN_DEBUG_CONSOLE":
                        return "ERROR|Debug console not supported";

                    case "GET_SCENE_INFO":
                        return RunOnMainThread(GetSceneInfo);

                    case "SCENE_SYNC_START":
                        return RunOnMainThread(() => StartSceneSync(parts));

                    case "SCENE_SYNC_STOP":
                        return StopSceneSync(parts);

                    case "SCENE_SYNC_POLL":
                        return PollSceneSync(parts);

                    case "SCENE_GET_MESH":
                        return RunOnMainThread(() => parts.Length > 1 ? GetMeshInfo(parts) : "ERROR|Invalid mesh format");

                    case "GET_ACTIVE_OBJECTS":
                        return RunOnMainThread(GetActiveObjectsInfo);

                    case "GET_SCENE_ROOTS":
                        return RunOnMainThread(() => GetSceneRootsInfo(parts.Length > 1 ? parts[1] : null));

                    case "GET_CHILDREN":
                        return RunOnMainThread(() => parts.Length > 1 ? GetChildrenInfo(parts[1]) : "ERROR|Invalid children format");

                    case "GET_TRANSFORM":
                        return RunOnMainThread(() => parts.Length > 1 ? GetTransformInfo(parts[1]) : "ERROR|Invalid transform format");

                    case "SET_TRANSFORM_LOCAL":
                        return RunOnMainThread(() => parts.Length > 4 ? SetTransformLocal(parts[1], parts[2], parts[3], parts[4]) : "ERROR|Invalid set transform format");

                    case "SET_TRANSFORM_WORLD":
                        return RunOnMainThread(() => parts.Length > 4 ? SetTransformWorld(parts[1], parts[2], parts[3], parts[4]) : "ERROR|Invalid set transform format");

                    case "SET_ACTIVE":
                        return RunOnMainThread(() => parts.Length > 2 ? SetObjectActive(parts[1], parts[2]) : "ERROR|Invalid set active format");

                    case "SET_NAME":
                        return RunOnMainThread(() => parts.Length > 2 ? SetObjectName(parts[1], parts[2]) : "ERROR|Invalid set name format");

                    case "SET_TAG":
                        return RunOnMainThread(() => parts.Length > 2 ? SetObjectTag(parts[1], parts[2]) : "ERROR|Invalid set tag format");

                    case "SET_LAYER":
                        return RunOnMainThread(() => parts.Length > 2 ? SetObjectLayer(parts[1], parts[2]) : "ERROR|Invalid set layer format");

                    case "GET_ENABLED":
                        return RunOnMainThread(() => parts.Length > 1 ? GetEnabledInfo(parts[1]) : "ERROR|Invalid get enabled format");

                    case "SET_ENABLED":
                        return RunOnMainThread(() => parts.Length > 2 ? SetEnabled(parts[1], parts[2]) : "ERROR|Invalid set enabled format");

                    case "DESTROY":
                        return RunOnMainThread(() => parts.Length > 1 ? DestroyObject(parts[1]) : "ERROR|Invalid destroy format");

                    case "DUPLICATE":
                        return RunOnMainThread(() => parts.Length > 1 ? DuplicateObject(parts[1]) : "ERROR|Invalid duplicate format");

                    case "ADD_COMPONENT":
                        return RunOnMainThread(() => parts.Length > 2 ? AddComponentToObject(parts[1], parts[2]) : "ERROR|Invalid add component format");

                    case "GET_HIERARCHY_PATH":
                        return RunOnMainThread(() => parts.Length > 1 ? GetHierarchyPath(parts[1]) : "ERROR|Invalid path format");

                    case "GET_NODE_INFO":
                        return RunOnMainThread(() => parts.Length > 1 ? GetNodeInfo(parts[1]) : "ERROR|Invalid node info format");

                    case "PIN_OBJECT":
                        return RunOnMainThread(() => parts.Length > 1 ? PinObject(parts[1]) : "ERROR|Invalid pin format");

                    case "UNPIN_OBJECT":
                        return RunOnMainThread(() => parts.Length > 1 ? UnpinObject(parts[1]) : "ERROR|Invalid unpin format");

                    case "DUMP_JSON":
                        return RunOnMainThread(() => parts.Length > 1 ? DumpJson(parts) : "ERROR|Invalid dump format");

                    case "LOG_PULL":
                        return parts.Length > 0 ? PullLogs(parts) : "ERROR|Invalid log pull format";

                    case "LOG_CLEAR":
                        return ClearLogs();

                    case "SET_PARENT":
                        return RunOnMainThread(() => parts.Length > 2 ? SetParent(parts[1], parts[2]) : "ERROR|Invalid set parent format");

                    case "SET_SIBLING_INDEX":
                        return RunOnMainThread(() => parts.Length > 2 ? SetSiblingIndex(parts[1], parts[2]) : "ERROR|Invalid set sibling format");

                    case "CREATE_GAMEOBJECT":
                        return RunOnMainThread(() => parts.Length > 2 ? CreateGameObject(parts[1], parts[2]) : "ERROR|Invalid create gameobject format");
                        
                    case "APPLY_HARMONY_PATCH":
                        return parts.Length > 4 ? ApplyHarmonyPatch(parts[1], parts[2], parts[3], parts[4]) : "ERROR|Invalid harmony patch format";
                        
                    case "REMOVE_HARMONY_PATCH":
                        return parts.Length > 1 ? RemoveHarmonyPatch(parts[1]) : "ERROR|Invalid remove harmony patch format";
                        
                    default:
                        return $"ERROR|Unknown command: {cmdType}";
                }
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        // Universal command implementations
        private static string GetAssembliesInfo()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            return $"ASSEMBLIES|{assemblies.Length}";
        }
        
        private static string GetTypesInfo(string assemblyName)
        {
            try
            {
                Assembly assembly = string.IsNullOrEmpty(assemblyName) 
                    ? Assembly.GetExecutingAssembly() 
                    : Assembly.Load(assemblyName);
                
                return $"TYPES|{assembly.GetTypes().Length}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static string InvokeMethod(string typeName, string methodName, string parameters)
        {
            try
            {
                // Find the type
                Type targetType = Type.GetType(typeName);
                if (targetType == null)
                {
                    // Search in all loaded assemblies
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        targetType = assembly.GetType(typeName);
                        if (targetType != null) break;
                    }
                }
                
                if (targetType == null)
                    return $"ERROR|Type not found: {typeName}";
                
                // Find the method
                var method = targetType.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                if (method == null)
                    return $"ERROR|Method not found: {methodName}";
                
                // Parse parameters (simple comma-separated values)
                object[] paramValues = null;
                if (!string.IsNullOrEmpty(parameters) && parameters != "null")
                {
                    var paramStrings = parameters.Split(',');
                    paramValues = new object[paramStrings.Length];
                    var methodParams = method.GetParameters();
                    
                    for (int i = 0; i < paramStrings.Length; i++)
                    {
                        if (i < methodParams.Length)
                        {
                            // Simple type conversion - in real implementation, you'd need proper parsing
                            paramValues[i] = Convert.ChangeType(paramStrings[i], methodParams[i].ParameterType);
                        }
                    }
                }
                
                // Invoke the method
                object result = method.Invoke(null, paramValues);
                return $"SUCCESS|{FormatValue(result)}";
            }
            catch (Exception ex)
            {
                return $"ERROR|Invoke failed: {ex.Message}";
            }
        }
        
        private static string GetFieldValue(string typeName, string fieldName)
        {
            try
            {
                // Find the type
                Type targetType = Type.GetType(typeName);
                if (targetType == null)
                {
                    // Search in all loaded assemblies
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        targetType = assembly.GetType(typeName);
                        if (targetType != null) break;
                    }
                }
                
                if (targetType == null)
                    return $"ERROR|Type not found: {typeName}";
                
                // Find the field
                var field = targetType.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                if (field == null)
                    return $"ERROR|Field not found: {fieldName}";
                
                // Get field value
                object value = field.GetValue(null); // For static fields
                return $"SUCCESS|{FormatValue(value)}";
            }
            catch (Exception ex)
            {
                return $"ERROR|Get field failed: {ex.Message}";
            }
        }
        
        private static string SetFieldValue(string typeName, string fieldName, string value)
        {
            try
            {
                // Find the type
                Type targetType = Type.GetType(typeName);
                if (targetType == null)
                {
                    // Search in all loaded assemblies
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        targetType = assembly.GetType(typeName);
                        if (targetType != null) break;
                    }
                }
                
                if (targetType == null)
                    return $"ERROR|Type not found: {typeName}";
                
                // Find the field
                var field = targetType.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                if (field == null)
                    return $"ERROR|Field not found: {fieldName}";
                
                // Convert and set field value
                object convertedValue = ConvertStringToType(value, field.FieldType);
                field.SetValue(null, convertedValue); // For static fields
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                return $"ERROR|Set field failed: {ex.Message}";
            }
        }

        private static string GetPropertyValue(string typeName, string propertyName)
        {
            try
            {
                Type targetType = Type.GetType(typeName);
                if (targetType == null)
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        targetType = assembly.GetType(typeName);
                        if (targetType != null) break;
                    }
                }

                if (targetType == null)
                    return $"ERROR|Type not found: {typeName}";

                var prop = targetType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                if (prop == null)
                    return $"ERROR|Property not found: {propertyName}";

                if (!prop.CanRead)
                    return "ERROR|Property is not readable";

                var getter = prop.GetGetMethod(true);
                if (getter == null || !getter.IsStatic)
                    return "ERROR|Only static properties are supported";

                var value = prop.GetValueCompat(null);
                return $"SUCCESS|{FormatValue(value)}";
            }
            catch (Exception ex)
            {
                return $"ERROR|Get property failed: {ex.Message}";
            }
        }

        private static string SetPropertyValue(string typeName, string propertyName, string value)
        {
            try
            {
                Type targetType = Type.GetType(typeName);
                if (targetType == null)
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        targetType = assembly.GetType(typeName);
                        if (targetType != null) break;
                    }
                }

                if (targetType == null)
                    return $"ERROR|Type not found: {typeName}";

                var prop = targetType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                if (prop == null)
                    return $"ERROR|Property not found: {propertyName}";

                if (!prop.CanWrite)
                    return "ERROR|Property is not writable";

                var setter = prop.GetSetMethod(true);
                if (setter == null || !setter.IsStatic)
                    return "ERROR|Only static properties are supported";

                var converted = ConvertStringToType(value, prop.PropertyType);
                prop.SetValueCompat(null, converted);
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                return $"ERROR|Set property failed: {ex.Message}";
            }
        }
        
        /// <summary>
        /// Check if the MelonLoader mod is connected to a Unity game
        /// </summary>
        private static bool IsGameConnected()
        {
            try
            {
                // Check if we're running in a Unity game by looking for Unity-specific assemblies
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                bool hasUnityEngine = assemblies.Any(a => a.FullName.Contains("UnityEngine"));
                bool hasUnityEditor = assemblies.Any(a => a.FullName.Contains("UnityEditor"));
                
                // Also check if we have a valid game directory
                bool hasGameDirectory = !string.IsNullOrEmpty(_gameDirectory) && _gameDirectory != "Unknown";
                
                // We're connected to a game if we have Unity assemblies and a game directory
                return (hasUnityEngine || hasUnityEditor) && hasGameDirectory;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Cleanup on mod unload
        /// </summary>
        public override void OnDeinitializeMelon()
        {
            _isConnected = false;
            // Clean up pipe thread (server architecture - no pipe client to dispose)
            // Thread will automatically terminate as it's marked as background thread
            TryRemoveUnityLogHook();
            
            MelonLoader.MelonLogger.Msg("Universal MelonMod shutting down...");
        }
        
        /// <summary>
        /// Set the game directory for pipe server communication
        /// </summary>
        private static void SetGameDirectory()
        {
            try
            {
                string gameDir = TryGetGameDirectoryFromMelonEnvironment();

                if (IsNullOrWhiteSpace(gameDir))
                    gameDir = TryGetProcessDirectory();

                if (IsNullOrWhiteSpace(gameDir))
                    gameDir = AppDomain.CurrentDomain.BaseDirectory;

                if (IsNullOrWhiteSpace(gameDir))
                    gameDir = Environment.CurrentDirectory;

                if (!IsNullOrWhiteSpace(gameDir))
                    gameDir = Path.GetFullPath(gameDir);

                _gameDirectory = gameDir;
                MelonLoader.MelonLogger.Msg($"Game directory detected: '{_gameDirectory ?? "<null>"}' (len={(_gameDirectory ?? "").Length})");
                
                // Don't send commands to the external tool - wait for it to request information
                // The external tool will send GET_GAME_DIRECTORY when it needs this information
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Warning($"Failed to set game directory: {ex.Message}");
            }
        }

        private static string TryGetGameDirectoryFromMelonEnvironment()
        {
            try
            {
                var envType = Type.GetType("MelonLoader.MelonEnvironment, MelonLoader", throwOnError: false);
                if (envType == null)
                    return null;

                var candidates = new[]
                {
                    "GameRootDirectory",
                    "GameDirectory",
                    "GameRootPath",
                    "GamePath",
                    "GameExecutablePath",
                    "GameExePath"
                };

                foreach (var name in candidates)
                {
                    var prop = envType.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                    var value = prop?.GetValueCompat(null)?.ToString();
                    if (IsNullOrWhiteSpace(value))
                        continue;

                    if (File.Exists(value))
                        return Path.GetDirectoryName(value);

                    if (Directory.Exists(value))
                        return value;

                    return value;
                }
            }
            catch
            {
            }

            return null;
        }

        private static string TryGetProcessDirectory()
        {
            try
            {
                var path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (IsNullOrWhiteSpace(path))
                    return null;

                return Path.GetDirectoryName(path);
            }
            catch
            {
                return null;
            }
        }
        
        // ========== ENHANCED COMMAND HANDLERS ==========
        
        private static string GetAssemblyDetails(string assemblyName)
        {
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase));
                
                if (assembly == null)
                    return $"ERROR|Assembly not found: {assemblyName}";
                
                return $"ASSEMBLY_DETAILS|{assembly.GetName().Name}|{assembly.GetName().Version}|{assembly.Location}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static string GetTypeDetails(string typeName)
        {
            try
            {
                var type = Type.GetType(typeName) ?? AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => a.GetTypes())
                    .FirstOrDefault(t => t.FullName == typeName);
                
                if (type == null)
                    return $"ERROR|Type not found: {typeName}";
                
                return $"TYPE_DETAILS|{type.FullName}|{type.Namespace}|{type.IsPublic}|{type.IsClass}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static string GetMethodsInfo(string typeName)
        {
            try
            {
                var type = Type.GetType(typeName) ?? AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => a.GetTypes())
                    .FirstOrDefault(t => t.FullName == typeName);
                
                if (type == null)
                    return $"ERROR|Type not found: {typeName}";
                
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                return $"METHODS|{methods.Length}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static string GetFieldsInfo(string typeName)
        {
            try
            {
                var type = Type.GetType(typeName) ?? AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => a.GetTypes())
                    .FirstOrDefault(t => t.FullName == typeName);
                
                if (type == null)
                    return $"ERROR|Type not found: {typeName}";
                
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                return $"FIELDS|{fields.Length}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static string GetPropertiesInfo(string typeName)
        {
            try
            {
                var type = Type.GetType(typeName) ?? AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => a.GetTypes())
                    .FirstOrDefault(t => t.FullName == typeName);
                
                if (type == null)
                    return $"ERROR|Type not found: {typeName}";
                
                var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                return $"PROPERTIES|{properties.Length}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static string InvokeInstanceMethod(string typeName, string instanceId, string methodName, string parameters)
        {
            try
            {
                var type = Type.GetType(typeName) ?? AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => a.GetTypes())
                    .FirstOrDefault(t => t.FullName == typeName);
                
                if (type == null)
                    return $"ERROR|Type not found: {typeName}";
                
                // Find the method
                var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (method == null)
                    return $"ERROR|Method not found: {methodName}";
                
                // Parse instance ID (for now, using hash code as ID)
                if (!int.TryParse(instanceId, out int instanceHash))
                    return $"ERROR|Invalid instance ID: {instanceId}";
                
                // Find the instance (simplified - in real implementation, you'd track instances)
                object instance = FindObjectInstance(instanceHash);
                if (instance == null)
                    return $"ERROR|Instance not found: {instanceId}";
                
                // Parse parameters
                object[] paramValues = null;
                if (!string.IsNullOrEmpty(parameters) && parameters != "null")
                {
                    var paramStrings = parameters.Split(',');
                    paramValues = new object[paramStrings.Length];
                    var methodParams = method.GetParameters();
                    
                    for (int i = 0; i < paramStrings.Length; i++)
                    {
                        if (i < methodParams.Length)
                        {
                            paramValues[i] = Convert.ChangeType(paramStrings[i], methodParams[i].ParameterType);
                        }
                    }
                }
                
                // Invoke the method on the instance
                object result = method.Invoke(instance, paramValues);
                return $"SUCCESS|{FormatValue(result)}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static object FindObjectInstance(int instanceHash)
        {
            try
            {
#if NET35
                WeakReference weak = null;
                lock (_trackedObjectsLock)
                {
                    if (_trackedObjects.ContainsKey(instanceHash))
                        weak = _trackedObjects[instanceHash];
                }

                if (weak == null)
                    return null;

                var target = weak.IsAlive ? weak.Target : null;
                if (target == null)
                {
                    lock (_trackedObjectsLock)
                    {
                        _trackedObjects.Remove(instanceHash);
                    }
                }
                return target;
#else
                if (_trackedObjects.TryGetValue(instanceHash, out var weak))
                {
                    if (weak != null && weak.TryGetTarget(out var target) && target != null)
                        return target;

                    _trackedObjects.TryRemove(instanceHash, out _);
                    return null;
                }

                return null;
#endif
            }
            catch
            {
                return null;
            }
        }

        private static int TrackObject(object obj)
        {
            if (obj == null)
                return 0;

            var ops = Interlocked.Increment(ref _trackOps);
            if ((ops % 500) == 0)
                PruneTrackedObjects();

            var unityId = TryGetUnityInstanceId(obj, out var id) ? id : 0;
            if (unityId != 0)
            {
#if NET35
                lock (_trackedObjectsLock)
                {
                    _trackedObjects[unityId] = new WeakReference(obj);
                }
#else
                _trackedObjects[unityId] = new WeakReference<object>(obj);
#endif
                return unityId;
            }

            var trackedId = Interlocked.Increment(ref _nextTrackedId);
#if NET35
            lock (_trackedObjectsLock)
            {
                _trackedObjects[trackedId] = new WeakReference(obj);
            }
#else
            _trackedObjects[trackedId] = new WeakReference<object>(obj);
#endif
            return trackedId;
        }

        private static void PruneTrackedObjects()
        {
            try
            {
#if NET35
                List<int> remove = null;
                lock (_trackedObjectsLock)
                {
                    if (_trackedObjects.Count > 60000)
                    {
                        _trackedObjects.Clear();
                        return;
                    }

                    foreach (var kv in _trackedObjects)
                    {
                        var weak = kv.Value;
                        var alive = weak != null && weak.IsAlive && weak.Target != null;
                        if (alive)
                            continue;
                        remove ??= new List<int>();
                        remove.Add(kv.Key);
                        if (remove.Count >= 2000)
                            break;
                    }

                    if (remove != null)
                    {
                        for (var i = 0; i < remove.Count; i++)
                            _trackedObjects.Remove(remove[i]);
                    }
                }
#else
                if (_trackedObjects.Count > 80000)
                {
                    _trackedObjects.Clear();
                    return;
                }

                var removed = 0;
                foreach (var kv in _trackedObjects)
                {
                    var weak = kv.Value;
                    if (weak != null && weak.TryGetTarget(out var target) && target != null)
                        continue;
                    _trackedObjects.TryRemove(kv.Key, out _);
                    removed++;
                    if (removed >= 2000)
                        break;
                }
#endif
            }
            catch
            {
            }
        }

        private static bool TryGetUnityInstanceId(object obj, out int id)
        {
            id = 0;
            if (obj == null)
                return false;
            try
            {
                var t = obj.GetType();
                var getInstanceId = GetMethodCached(t, "GetInstanceID", Type.EmptyTypes);
                if (getInstanceId == null)
                    return false;
                var value = getInstanceId.Invoke(obj, null);
                if (value == null)
                    return false;
                id = Convert.ToInt32(value);
                return id != 0;
            }
            catch
            {
                return false;
            }
        }

        private static Type ResolveTypeByName(string typeName)
        {
            if (IsNullOrWhiteSpace(typeName))
                return null;

            try
            {
#if NET35
                Type cached;
                lock (_typeResolveCacheLock)
                {
                    if (_resolvedTypeCache.Count > 20000)
                        _resolvedTypeCache.Clear();
                    if (_resolvedTypeCache.TryGetValue(typeName, out cached))
                        return cached;
                }
#else
                lock (_typeResolveCacheLock)
                {
                    if (_resolvedTypeCache.Count > 20000)
                        _resolvedTypeCache.Clear();
                    if (_resolvedTypeCache.TryGetValue(typeName, out var cached))
                        return cached;
                }
#endif

                var direct = Type.GetType(typeName, false);
                if (direct != null)
                {
                    lock (_typeResolveCacheLock) { _resolvedTypeCache[typeName] = direct; }
                    return direct;
                }

                if (_isIL2CPP && typeName.StartsWith("Il2CppSystem.", StringComparison.Ordinal))
                {
                    var candidates = new[]
                    {
                        "Il2Cppmscorlib",
                        "Il2CppSystem",
                        "Il2CppInterop.Runtime"
                    };

                    for (var i = 0; i < candidates.Length; i++)
                    {
                        try
                        {
                            var qualified = Type.GetType(typeName + ", " + candidates[i], false);
                            if (qualified != null)
                            {
                                lock (_typeResolveCacheLock) { _resolvedTypeCache[typeName] = qualified; }
                                return qualified;
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = asm.GetType(typeName, false);
                        if (t != null)
                        {
                            lock (_typeResolveCacheLock) { _resolvedTypeCache[typeName] = t; }
                            return t;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static PropertyInfo GetPropertyCached(Type type, string name)
        {
            if (type == null || IsNullOrWhiteSpace(name))
                return null;
            var key = (type.FullName ?? type.Name) + "|P|" + name;
            lock (_memberCacheLock)
            {
                if (_propertyCache.Count > 20000)
                    _propertyCache.Clear();
                if (_propertyCache.TryGetValue(key, out var cached))
                    return cached;
            }
            try
            {
                var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                lock (_memberCacheLock) { _propertyCache[key] = p; }
                return p;
            }
            catch
            {
                lock (_memberCacheLock) { _propertyCache[key] = null; }
                return null;
            }
        }

        private static MethodInfo GetMethodCached(Type type, string name, Type[] parameters)
        {
            if (type == null || IsNullOrWhiteSpace(name))
                return null;
            var sig = parameters == null || parameters.Length == 0
                ? ""
                : string.Join(",", parameters.Select(p => p.FullName ?? p.Name).ToArray());
            var key = (type.FullName ?? type.Name) + "|M|" + name + "|" + sig;
            lock (_memberCacheLock)
            {
                if (_methodCache.Count > 20000)
                    _methodCache.Clear();
                if (_methodCache.TryGetValue(key, out var cached))
                    return cached;
            }
            try
            {
                var mi = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, parameters ?? Type.EmptyTypes, null);
                lock (_memberCacheLock) { _methodCache[key] = mi; }
                return mi;
            }
            catch
            {
                lock (_memberCacheLock) { _methodCache[key] = null; }
                return null;
            }
        }

        private static object ResolveTrackedObject(string instanceId)
        {
            if (IsNullOrWhiteSpace(instanceId))
                return null;
            if (!int.TryParse(instanceId, out var id))
                return null;
            return FindObjectInstance(id);
        }

        private static string PinObject(string instanceId)
        {
            try
            {
                if (!int.TryParse(instanceId, out var id) || id == 0)
                    return "ERROR|Invalid id";

                var obj = FindObjectInstance(id);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

#if NET35
                lock (_pinnedObjectsLock)
                {
                    _pinnedObjects[id] = obj;
                }
#else
                _pinnedObjects[id] = obj;
#endif

                return $"SUCCESS|pinned=1|id={id}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string UnpinObject(string instanceId)
        {
            try
            {
                if (!int.TryParse(instanceId, out var id) || id == 0)
                    return "ERROR|Invalid id";

                var removed = false;
#if NET35
                lock (_pinnedObjectsLock)
                {
                    removed = _pinnedObjects.Remove(id);
                }
#else
                removed = _pinnedObjects.TryRemove(id, out _);
#endif
                return $"SUCCESS|unpinned={(removed ? "1" : "0")}|id={id}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DumpJson(string[] parts)
        {
            try
            {
                var instanceId = parts.Length > 1 ? parts[1] : null;
                if (IsNullOrWhiteSpace(instanceId))
                    return "ERROR|Missing id";

                var depth = 2;
                var maxChars = 200000;
                for (var i = 2; i < parts.Length; i++)
                {
                    var p = parts[i] ?? "";
                    var eq = p.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    var k = p.Substring(0, eq);
                    var v = p.Substring(eq + 1);
                    if (k.Equals("depth", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out var d))
                        depth = Math.Max(0, Math.Min(6, d));
                    else if (k.Equals("maxChars", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out var mc))
                        maxChars = Math.Max(1024, Math.Min(2000000, mc));
                }

                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var json = SerializeToJson(obj, depth, maxChars);
                var b64 = ToBase64Utf8(json);
                return $"JSON|id={instanceId}|b64={b64}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string PullLogs(string[] parts)
        {
            try
            {
                var after = 0L;
                var max = 200;
                for (var i = 1; i < parts.Length; i++)
                {
                    var p = parts[i] ?? "";
                    var eq = p.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    var k = p.Substring(0, eq);
                    var v = p.Substring(eq + 1);
                    if (k.Equals("after", StringComparison.OrdinalIgnoreCase) && long.TryParse(v, out var a))
                        after = Math.Max(0, a);
                    else if (k.Equals("max", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out var m))
                        max = Math.Max(1, Math.Min(500, m));
                }

                List<GameLogEntry> batch = null;
                long lastSeq = 0;
                lock (_gameLogLock)
                {
                    if (_gameLogs.Count == 0)
                        return $"LOG|last={_gameLogSeq}|count=0";

                    for (var i = 0; i < _gameLogs.Count; i++)
                    {
                        var e = _gameLogs[i];
                        if (e == null || e.Seq <= after)
                            continue;
                        batch ??= new List<GameLogEntry>(Math.Min(max, 256));
                        batch.Add(e);
                        if (batch.Count >= max)
                            break;
                    }

                    lastSeq = _gameLogSeq;
                }

                if (batch == null || batch.Count == 0)
                    return $"LOG|last={lastSeq}|count=0";

                var sb = new StringBuilder();
                sb.Append("LOG");
                sb.Append("|last=");
                sb.Append(lastSeq);
                sb.Append("|count=");
                sb.Append(batch.Count);

                for (var i = 0; i < batch.Count; i++)
                {
                    var e = batch[i];
                    sb.Append("|e=");
                    sb.Append(e.Seq);
                    sb.Append(';');
                    sb.Append(e.UtcTicks);
                    sb.Append(';');
                    sb.Append(e.Level);
                    sb.Append(';');
                    sb.Append(ToBase64Utf8(e.Message ?? ""));
                    sb.Append(';');
                    sb.Append(ToBase64Utf8(e.Stack ?? ""));
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string ClearLogs()
        {
            try
            {
                lock (_gameLogLock)
                {
                    _gameLogs.Clear();
                    _gameLogSeq = 0;
                }
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static void TryInitializeUnityLogHook()
        {
            try
            {
                if (_unityLogDelegate != null)
                    return;

                var appType = FindUnityType("UnityEngine.Application");
                if (appType == null)
                    return;

                var evt = appType.GetEvent("logMessageReceivedThreaded", BindingFlags.Public | BindingFlags.Static)
                          ?? appType.GetEvent("logMessageReceived", BindingFlags.Public | BindingFlags.Static);
                if (evt == null)
                    return;

                var handlerType = evt.EventHandlerType;
                if (handlerType == null)
                    return;

                var invoke = handlerType.GetMethod("Invoke");
                if (invoke == null)
                    return;

                var ps = invoke.GetParameters();
                if (ps == null || ps.Length != 3)
                    return;

                if (ps[0].ParameterType != typeof(string) || ps[1].ParameterType != typeof(string))
                    return;

                var logType = ps[2].ParameterType;

                var dm = new DynamicMethod(
                    "PhantomLink_UnityLogHook",
                    typeof(void),
                    new[] { typeof(string), typeof(string), logType },
                    typeof(UniversalMelonMod),
                    true);

                var il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                if (logType.IsValueType)
                    il.Emit(OpCodes.Box, logType);
                il.Emit(OpCodes.Call, typeof(UniversalMelonMod).GetMethod("OnUnityLogBridge", BindingFlags.NonPublic | BindingFlags.Static));
                il.Emit(OpCodes.Ret);

                var del = dm.CreateDelegate(handlerType);
                evt.AddEventHandler(null, del);

                _unityLogEvent = evt;
                _unityLogDelegate = del;
            }
            catch
            {
            }
        }

        private static void TryRemoveUnityLogHook()
        {
            try
            {
                if (_unityLogEvent == null || _unityLogDelegate == null)
                    return;

                _unityLogEvent.RemoveEventHandler(null, (Delegate)_unityLogDelegate);
            }
            catch
            {
            }
            finally
            {
                _unityLogEvent = null;
                _unityLogDelegate = null;
            }
        }

        private static void OnUnityLogBridge(string condition, string stackTrace, object logType)
        {
            try
            {
                var level = 0;
                if (logType != null)
                {
                    try
                    {
                        level = Convert.ToInt32(logType);
                    }
                    catch
                    {
                        level = 0;
                    }
                }

                var entry = new GameLogEntry
                {
                    Seq = 0,
                    UtcTicks = DateTime.UtcNow.Ticks,
                    Level = level,
                    Message = condition ?? "",
                    Stack = stackTrace ?? ""
                };

                lock (_gameLogLock)
                {
                    entry.Seq = ++_gameLogSeq;
                    _gameLogs.Add(entry);
                    if (_gameLogs.Count > 12000)
                        _gameLogs.RemoveRange(0, 2000);
                }
            }
            catch
            {
            }
        }

        private static string ToBase64Utf8(string value)
        {
            try
            {
                if (value == null)
                    value = "";
                var bytes = Encoding.UTF8.GetBytes(value);
                return Convert.ToBase64String(bytes);
            }
            catch
            {
                return "";
            }
        }

        private static string SerializeToJson(object obj, int depth, int maxChars)
        {
            var sb = new StringBuilder(Math.Min(maxChars, 8192));
            var visited = new HashSet<int>();
            WriteJsonValue(sb, obj, depth, visited, maxChars);
            if (sb.Length > maxChars)
                sb.Length = maxChars;
            return sb.ToString();
        }

        private static void WriteJsonValue(StringBuilder sb, object value, int depth, HashSet<int> visited, int maxChars)
        {
            if (sb.Length >= maxChars)
                return;

            if (value == null)
            {
                sb.Append("null");
                return;
            }

            var t = value.GetType();

            if (t == typeof(string))
            {
                WriteJsonString(sb, (string)value, maxChars);
                return;
            }

            if (t == typeof(bool))
            {
                sb.Append(((bool)value) ? "true" : "false");
                return;
            }

            if (t.IsEnum)
            {
                WriteJsonString(sb, value.ToString(), maxChars);
                return;
            }

            if (t == typeof(byte) || t == typeof(sbyte) ||
                t == typeof(short) || t == typeof(ushort) ||
                t == typeof(int) || t == typeof(uint) ||
                t == typeof(long) || t == typeof(ulong) ||
                t == typeof(float) || t == typeof(double) ||
                t == typeof(decimal))
            {
                try
                {
                    sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                }
                catch
                {
                    WriteJsonString(sb, value.ToString(), maxChars);
                }
                return;
            }

            if (depth <= 0)
            {
                WriteJsonObjectStub(sb, value, maxChars);
                return;
            }

            var key = GetStableObjectKey(value);
            if (key != 0)
            {
                if (visited.Contains(key))
                {
                    sb.Append("{\"$ref\":");
                    sb.Append(key);
                    sb.Append('}');
                    return;
                }
                visited.Add(key);
            }

            if (value is Array arr)
            {
                sb.Append('[');
                var len = arr.Length;
                for (var i = 0; i < len; i++)
                {
                    if (sb.Length >= maxChars)
                        break;
                    if (i > 0)
                        sb.Append(',');
                    WriteJsonValue(sb, arr.GetValue(i), depth - 1, visited, maxChars);
                }
                sb.Append(']');
                return;
            }

            var dict = value as System.Collections.IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                var first = true;
                foreach (System.Collections.DictionaryEntry de in dict)
                {
                    if (sb.Length >= maxChars)
                        break;
                    var k = de.Key?.ToString() ?? "";
                    if (!first) sb.Append(',');
                    first = false;
                    WriteJsonString(sb, k, maxChars);
                    sb.Append(':');
                    WriteJsonValue(sb, de.Value, depth - 1, visited, maxChars);
                }
                sb.Append('}');
                return;
            }

            var enumerable = value as System.Collections.IEnumerable;
            if (enumerable != null && !(value is string))
            {
                sb.Append('[');
                var first = true;
                foreach (var item in enumerable)
                {
                    if (sb.Length >= maxChars)
                        break;
                    if (!first) sb.Append(',');
                    first = false;
                    WriteJsonValue(sb, item, depth - 1, visited, maxChars);
                }
                sb.Append(']');
                return;
            }

            sb.Append('{');
            WriteJsonString(sb, "$type", maxChars);
            sb.Append(':');
            WriteJsonString(sb, t.FullName ?? t.Name, maxChars);

            if (TryGetUnityInstanceId(value, out var unityId) && unityId != 0)
            {
                sb.Append(',');
                WriteJsonString(sb, "$id", maxChars);
                sb.Append(':');
                sb.Append(unityId);

                var name = GetUnityObjectName(value);
                if (!IsNullOrWhiteSpace(name))
                {
                    sb.Append(',');
                    WriteJsonString(sb, "name", maxChars);
                    sb.Append(':');
                    WriteJsonString(sb, name, maxChars);
                }
            }

            var flags = BindingFlags.Instance | BindingFlags.Public;
            FieldInfo[] fields = null;
            PropertyInfo[] props = null;
            try { fields = t.GetFields(flags); } catch { fields = null; }
            try { props = t.GetProperties(flags); } catch { props = null; }

            if (fields != null)
            {
                for (var i = 0; i < fields.Length; i++)
                {
                    if (sb.Length >= maxChars)
                        break;
                    var f = fields[i];
                    if (f == null)
                        continue;
                    sb.Append(',');
                    WriteJsonString(sb, f.Name, maxChars);
                    sb.Append(':');
                    object fv = null;
                    try { fv = f.GetValue(value); } catch { fv = null; }
                    WriteJsonValue(sb, fv, depth - 1, visited, maxChars);
                }
            }

            if (props != null)
            {
                for (var i = 0; i < props.Length; i++)
                {
                    if (sb.Length >= maxChars)
                        break;
                    var p = props[i];
                    if (p == null)
                        continue;
                    if (!p.CanRead)
                        continue;
                    var idx = p.GetIndexParameters();
                    if (idx != null && idx.Length != 0)
                        continue;
                    sb.Append(',');
                    WriteJsonString(sb, p.Name, maxChars);
                    sb.Append(':');
                    object pv = null;
                    try { pv = p.GetValueCompat(value); } catch { pv = null; }
                    WriteJsonValue(sb, pv, depth - 1, visited, maxChars);
                }
            }

            sb.Append('}');
        }

        private static int GetStableObjectKey(object value)
        {
            try
            {
                if (value == null)
                    return 0;
                if (TryGetUnityInstanceId(value, out var unityId) && unityId != 0)
                    return unityId;
                return value.GetHashCode();
            }
            catch
            {
                return 0;
            }
        }

        private static void WriteJsonObjectStub(StringBuilder sb, object value, int maxChars)
        {
            sb.Append('{');
            WriteJsonString(sb, "$type", maxChars);
            sb.Append(':');
            WriteJsonString(sb, value.GetType().FullName ?? value.GetType().Name, maxChars);
            sb.Append('}');
        }

        private static void WriteJsonString(StringBuilder sb, string value, int maxChars)
        {
            if (sb.Length >= maxChars)
                return;
            sb.Append('\"');
            if (!string.IsNullOrEmpty(value))
            {
                for (var i = 0; i < value.Length; i++)
                {
                    if (sb.Length >= maxChars)
                        break;
                    var c = value[i];
                    if (c == '\"' || c == '\\')
                    {
                        sb.Append('\\');
                        sb.Append(c);
                    }
                    else if (c == '\n')
                    {
                        sb.Append("\\n");
                    }
                    else if (c == '\r')
                    {
                        sb.Append("\\r");
                    }
                    else if (c == '\t')
                    {
                        sb.Append("\\t");
                    }
                    else if (c < 32)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }
            sb.Append('\"');
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
#if NET35
            if (value == null)
                return true;
            if (value.Length == 0)
                return true;
            for (var i = 0; i < value.Length; i++)
            {
                if (!char.IsWhiteSpace(value[i]))
                    return false;
            }
            return true;
#else
            return string.IsNullOrWhiteSpace(value);
#endif
        }

        private static int GetManagedThreadId()
        {
#if NET35
            return Thread.CurrentThread.ManagedThreadId;
#else
            return Environment.CurrentManagedThreadId;
#endif
        }

        private static string FormatValue(object value)
        {
            return FormatValue(value, 0, null);
        }

        private static string FormatValue(object value, int depth, HashSet<int> seen)
        {
            if (value == null)
                return "null";

            if (depth >= 4)
                return (value.GetType().FullName ?? value.GetType().Name).Replace("|", " ");

            try
            {
                if (value is string s)
                    return SanitizePipeValue(s);

                var t = value.GetType();

                if (!t.IsValueType)
                {
                    seen ??= new HashSet<int>();
                    var key = RuntimeHelpers.GetHashCode(value);
                    if (!seen.Add(key))
                        return (t.FullName ?? t.Name).Replace("|", " ");
                }

                if (TryGetUnityInstanceId(value, out var unityId) && unityId != 0)
                {
                    var name = GetUnityObjectName(value) ?? (t.FullName ?? t.Name);
                    return SanitizePipeValue($"{name} (id={unityId})");
                }

                if (value is Array arr)
                {
                    var len = 0;
                    try { len = arr.Length; } catch { }
                    var max = Math.Min(len, 20);
                    var sb = new StringBuilder();
                    sb.Append('[');
                    sb.Append(len);
                    sb.Append("] {");

                    for (var i = 0; i < max; i++)
                    {
                        if (i != 0)
                            sb.Append(", ");
                        object elem = null;
                        try { elem = arr.GetValue(i); } catch { }
                        sb.Append(FormatValue(elem, depth + 1, seen));
                    }

                    if (len > max)
                    {
                        if (max > 0)
                            sb.Append(", ");
                        sb.Append("...");
                    }

                    sb.Append('}');
                    return SanitizePipeValue(sb.ToString());
                }

                if (value is System.Collections.IDictionary dict)
                {
                    var sb = new StringBuilder();
                    int count = 0;
                    try { count = dict.Count; } catch { }
                    sb.Append('{');
                    sb.Append(count);
                    sb.Append("} ");

                    var emitted = 0;
                    foreach (System.Collections.DictionaryEntry entry in dict)
                    {
                        if (emitted >= 12)
                            break;
                        if (emitted != 0)
                            sb.Append(", ");
                        sb.Append(FormatValue(entry.Key, depth + 1, seen));
                        sb.Append(": ");
                        sb.Append(FormatValue(entry.Value, depth + 1, seen));
                        emitted++;
                    }

                    if (count > emitted)
                        sb.Append(", ...");

                    return SanitizePipeValue(sb.ToString());
                }

                if (value is System.Collections.IEnumerable enumerable)
                {
                    var sb = new StringBuilder();
                    sb.Append('[');
                    var emitted = 0;
                    foreach (var item in enumerable)
                    {
                        if (emitted >= 20)
                            break;
                        if (emitted != 0)
                            sb.Append(", ");
                        sb.Append(FormatValue(item, depth + 1, seen));
                        emitted++;
                    }
                    if (emitted >= 20)
                        sb.Append(", ...");
                    sb.Append(']');
                    return SanitizePipeValue(sb.ToString());
                }

                if (value is IFormattable f)
                    return SanitizePipeValue(f.ToString(null, System.Globalization.CultureInfo.InvariantCulture));

                return SanitizePipeValue(value.ToString() ?? "null");
            }
            catch
            {
                return "null";
            }
        }

        private static string SanitizePipeValue(string text)
        {
            if (text == null)
                return "null";
            return text.Replace("|", " ")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("=", " ")
                .Replace(";", " ");
        }

        private static object ConvertStringToType(string text, Type targetType)
        {
            if (targetType == null)
                return null;

            if (IsNullOrWhiteSpace(text) || text.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null)
                    return null;
                return Activator.CreateInstance(targetType);
            }

            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
                targetType = underlying;

            if (targetType == typeof(string))
                return text;

            if (targetType.FullName == "Il2CppSystem.String")
            {
                try
                {
                    var implicitOp = targetType.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                    if (implicitOp != null && targetType.IsAssignableFrom(implicitOp.ReturnType))
                        return implicitOp.Invoke(null, new object[] { text });
                }
                catch
                {
                }

                try
                {
                    var ctor = targetType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                    if (ctor != null)
                        return ctor.Invoke(new object[] { text });
                }
                catch
                {
                }

                return null;
            }

            if ((targetType.FullName ?? "").StartsWith("Il2CppSystem.", StringComparison.Ordinal) && TryGetIl2CppNumericSystemType(targetType, out var numericSystemType))
            {
                object parsed = null;
                if (numericSystemType == typeof(int))
                    parsed = int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                else if (numericSystemType == typeof(long))
                    parsed = long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                else if (numericSystemType == typeof(float))
                    parsed = float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                else if (numericSystemType == typeof(double))
                    parsed = double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                else if (numericSystemType == typeof(short))
                    parsed = short.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                else if (numericSystemType == typeof(uint))
                    parsed = uint.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                else if (numericSystemType == typeof(ulong))
                    parsed = ulong.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                else if (numericSystemType == typeof(byte))
                    parsed = byte.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                else if (numericSystemType == typeof(decimal))
                    parsed = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

                if (parsed == null)
                    return null;

                if (TryConvertSystemNumericToIl2Cpp(parsed, targetType, out var il2cppValue))
                    return il2cppValue;
                return null;
            }

            if (targetType.IsEnum)
                return Enum.Parse(targetType, text, true);

            if (targetType == typeof(bool))
            {
                if (text == "1") return true;
                if (text == "0") return false;
                return bool.Parse(text);
            }

            if (targetType == typeof(int))
                return int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            if (targetType == typeof(long))
                return long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            if (targetType == typeof(float))
                return float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            if (targetType == typeof(double))
                return double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            if (targetType == typeof(decimal))
                return decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

            var vector3Type = FindUnityType("UnityEngine.Vector3");
            if (vector3Type != null && targetType == vector3Type)
            {
                var parts = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                    throw new FormatException("Vector3 requires 3 components");
                var x = float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                var y = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                var z = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                return Activator.CreateInstance(vector3Type, new object[] { x, y, z });
            }

            var colorType = FindUnityType("UnityEngine.Color");
            if (colorType != null && targetType == colorType)
            {
                var parts = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                    throw new FormatException("Color requires at least 3 components");
                var r = float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                var g = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                var b = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                var a = parts.Length >= 4 ? float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture) : 1f;
                return Activator.CreateInstance(colorType, new object[] { r, g, b, a });
            }

            return Convert.ChangeType(text, targetType, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static Array FindUnityObjectsOfType(Type type)
        {
            if (type == null)
                return null;

            try
            {
                if (TryInvokeGenericFinder("UnityEngine.Resources", "FindObjectsOfTypeAll", type, null, out var arr))
                    return arr;
            }
            catch
            {
            }

            try
            {
                if (TryInvokeStaticFinderWithTypeArg("UnityEngine.Resources", "FindObjectsOfTypeAll", type, out var arr))
                    return arr;
            }
            catch
            {
            }

            try
            {
                if (TryInvokeGenericFinder("UnityEngine.Object", "FindObjectsOfType", type, null, out var arr))
                    return arr;
            }
            catch
            {
            }

            try
            {
                if (TryInvokeGenericFindObjectsByType(type, out var arr))
                    return arr;
            }
            catch
            {
            }

            try
            {
                if (TryInvokeStaticFinderWithTypeArg("UnityEngine.Object", "FindObjectsOfType", type, out var arr))
                    return arr;
            }
            catch
            {
            }

            try
            {
                var unityObjectType = FindUnityType("UnityEngine.Object");
                var findWithInactive = unityObjectType?.GetMethod("FindObjectsOfType", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Type), typeof(bool) }, null);
                if (findWithInactive != null)
                {
                    var result = findWithInactive.Invoke(null, new object[] { type, true });
                    var arr = CoerceToSystemArray(result);
                    if (arr != null)
                        return arr;
                }
            }
            catch
            {
            }

            return null;
        }

        private static List<object> FindSceneGameObjects(Type gameObjectType, int maxObjects)
        {
            if (gameObjectType == null)
                return null;

            if (maxObjects <= 0)
                maxObjects = 8000;
            if (maxObjects > 20000)
                maxObjects = 20000;

            var results = new List<object>(Math.Min(8192, maxObjects));
            try
            {
                var sceneManager = FindUnityType("UnityEngine.SceneManagement.SceneManager");
                if (sceneManager == null)
                    return null;

                var sceneCountProp = sceneManager.GetProperty("sceneCount", BindingFlags.Public | BindingFlags.Static);
                if (sceneCountProp == null)
                    return null;

                var sceneCountObj = sceneCountProp.GetValueCompat(null);
                var sceneCount = 0;
                try { sceneCount = Convert.ToInt32(sceneCountObj, System.Globalization.CultureInfo.InvariantCulture); } catch { sceneCount = 0; }
                if (sceneCount <= 0)
                    return results;

                var getSceneAt = sceneManager.GetMethod("GetSceneAt", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
                if (getSceneAt == null)
                    return null;

                var sceneType = FindUnityType("UnityEngine.SceneManagement.Scene");
                if (sceneType == null)
                    return null;

                var isLoadedProp = sceneType.GetProperty("isLoaded", BindingFlags.Public | BindingFlags.Instance);
                var isValidMethod = sceneType.GetMethod("IsValid", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

                var getRootsNoArgs = sceneType.GetMethod("GetRootGameObjects", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                var getRootsWithList = sceneType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                    {
                        if (m == null || !string.Equals(m.Name, "GetRootGameObjects", StringComparison.Ordinal))
                            return false;
                        var ps = m.GetParameters();
                        return ps != null && ps.Length == 1;
                    });

                var visited = new HashSet<int>();

                for (var si = 0; si < sceneCount && results.Count < maxObjects; si++)
                {
                    object sceneObj;
                    try { sceneObj = getSceneAt.Invoke(null, new object[] { si }); }
                    catch { continue; }

                    if (sceneObj == null)
                        continue;

                    var valid = true;
                    if (isValidMethod != null)
                    {
                        try { valid = Convert.ToBoolean(isValidMethod.Invoke(sceneObj, null), System.Globalization.CultureInfo.InvariantCulture); }
                        catch { valid = true; }
                    }
                    if (!valid)
                        continue;

                    if (isLoadedProp != null)
                    {
                        try
                        {
                            var loaded = Convert.ToBoolean(isLoadedProp.GetValueCompat(sceneObj), System.Globalization.CultureInfo.InvariantCulture);
                            if (!loaded)
                                continue;
                        }
                        catch
                        {
                        }
                    }

                    var roots = new List<object>(256);
                    if (getRootsNoArgs != null)
                    {
                        try
                        {
                            var raw = getRootsNoArgs.Invoke(sceneObj, null);
                            var arr = CoerceToSystemArray(raw);
                            if (arr != null)
                            {
                                var len = arr.Length;
                                for (var i = 0; i < len; i++)
                                {
                                    var go = arr.GetValue(i);
                                    if (go != null)
                                        roots.Add(go);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                    else if (getRootsWithList != null)
                    {
                        try
                        {
                            var listType = typeof(List<>).MakeGenericType(gameObjectType);
                            var list = Activator.CreateInstance(listType);
                            getRootsWithList.Invoke(sceneObj, new[] { list });

                            if (list is System.Collections.IEnumerable enumerable)
                            {
                                foreach (var go in enumerable)
                                {
                                    if (go != null)
                                        roots.Add(go);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }

                    if (roots.Count == 0)
                        continue;

                    var stack = new Stack<object>(roots.Count * 2);
                    for (var i = 0; i < roots.Count; i++)
                        stack.Push(roots[i]);

                    while (stack.Count > 0 && results.Count < maxObjects)
                    {
                        var go = stack.Pop();
                        if (go == null)
                            continue;

                        if (TryGetUnityInstanceId(go, out var unityId) && unityId != 0)
                        {
                            if (!visited.Add(unityId))
                                continue;
                        }

                        results.Add(go);

                        if (!TryGetTransform(go, out var tr) || tr == null)
                            continue;

                        var t = tr.GetType();
                        var childCountProp = GetPropertyCached(t, "childCount");
                        var getChildMethod = GetMethodCached(t, "GetChild", new[] { typeof(int) });
                        var gameObjectProp = GetPropertyCached(t, "gameObject");
                        if (childCountProp == null || getChildMethod == null || gameObjectProp == null)
                            continue;

                        var childCountObj = childCountProp.GetValueCompat(tr);
                        var childCount = 0;
                        try { childCount = Convert.ToInt32(childCountObj, System.Globalization.CultureInfo.InvariantCulture); } catch { childCount = 0; }
                        if (childCount <= 0)
                            continue;

                        for (var ci = 0; ci < childCount; ci++)
                        {
                            object childTr;
                            try { childTr = getChildMethod.Invoke(tr, new object[] { ci }); }
                            catch { continue; }

                            if (childTr == null)
                                continue;

                            object childGo;
                            try { childGo = gameObjectProp.GetValueCompat(childTr); }
                            catch { childGo = null; }

                            if (childGo != null)
                                stack.Push(childGo);
                        }
                    }
                }
            }
            catch
            {
                return null;
            }

            return results;
        }

        private static List<object> GetLoadedSceneRootGameObjects(Type gameObjectType, int maxRoots)
        {
            if (gameObjectType == null)
                return null;

            if (maxRoots <= 0)
                maxRoots = 4096;
            if (maxRoots > 20000)
                maxRoots = 20000;

            var roots = new List<object>(Math.Min(512, maxRoots));
            try
            {
                var sceneManager = FindUnityType("UnityEngine.SceneManagement.SceneManager");
                if (sceneManager == null)
                    return null;

                var sceneCountProp = sceneManager.GetProperty("sceneCount", BindingFlags.Public | BindingFlags.Static);
                if (sceneCountProp == null)
                    return null;

                var sceneCountObj = sceneCountProp.GetValueCompat(null);
                var sceneCount = 0;
                try { sceneCount = Convert.ToInt32(sceneCountObj, System.Globalization.CultureInfo.InvariantCulture); } catch { sceneCount = 0; }
                if (sceneCount <= 0)
                    return roots;

                var getSceneAt = sceneManager.GetMethod("GetSceneAt", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
                if (getSceneAt == null)
                    return null;

                var sceneType = FindUnityType("UnityEngine.SceneManagement.Scene");
                if (sceneType == null)
                    return null;

                var isLoadedProp = sceneType.GetProperty("isLoaded", BindingFlags.Public | BindingFlags.Instance);
                var isValidMethod = sceneType.GetMethod("IsValid", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

                var getRootsNoArgs = sceneType.GetMethod("GetRootGameObjects", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                var getRootsWithList = sceneType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                    {
                        if (m == null || !string.Equals(m.Name, "GetRootGameObjects", StringComparison.Ordinal))
                            return false;
                        var ps = m.GetParameters();
                        return ps != null && ps.Length == 1;
                    });

                for (var si = 0; si < sceneCount && roots.Count < maxRoots; si++)
                {
                    object sceneObj;
                    try { sceneObj = getSceneAt.Invoke(null, new object[] { si }); }
                    catch { continue; }

                    if (sceneObj == null)
                        continue;

                    var valid = true;
                    if (isValidMethod != null)
                    {
                        try { valid = Convert.ToBoolean(isValidMethod.Invoke(sceneObj, null), System.Globalization.CultureInfo.InvariantCulture); }
                        catch { valid = true; }
                    }
                    if (!valid)
                        continue;

                    if (isLoadedProp != null)
                    {
                        try
                        {
                            var loaded = Convert.ToBoolean(isLoadedProp.GetValueCompat(sceneObj), System.Globalization.CultureInfo.InvariantCulture);
                            if (!loaded)
                                continue;
                        }
                        catch
                        {
                        }
                    }

                    if (getRootsNoArgs != null)
                    {
                        try
                        {
                            var raw = getRootsNoArgs.Invoke(sceneObj, null);
                            var arr = CoerceToSystemArray(raw);
                            if (arr == null)
                                continue;

                            var len = arr.Length;
                            for (var i = 0; i < len && roots.Count < maxRoots; i++)
                            {
                                var go = arr.GetValue(i);
                                if (go != null)
                                    roots.Add(go);
                            }
                        }
                        catch
                        {
                        }
                        continue;
                    }

                    if (getRootsWithList != null)
                    {
                        try
                        {
                            var listType = typeof(List<>).MakeGenericType(gameObjectType);
                            var list = Activator.CreateInstance(listType);
                            getRootsWithList.Invoke(sceneObj, new[] { list });

                            if (list is System.Collections.IEnumerable enumerable)
                            {
                                foreach (var go in enumerable)
                                {
                                    if (go == null)
                                        continue;
                                    roots.Add(go);
                                    if (roots.Count >= maxRoots)
                                        break;
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
                return null;
            }

            return roots;
        }

        private static void PushGameObjectChildren(object go, Stack<object> stack)
        {
            if (go == null || stack == null)
                return;

            try
            {
                if (!TryGetTransform(go, out var tr) || tr == null)
                    return;

                var t = tr.GetType();
                var childCountProp = GetPropertyCached(t, "childCount");
                var getChildMethod = GetMethodCached(t, "GetChild", new[] { typeof(int) });
                var gameObjectProp = GetPropertyCached(t, "gameObject");
                if (childCountProp == null || getChildMethod == null || gameObjectProp == null)
                    return;

                var childCountObj = childCountProp.GetValueCompat(tr);
                var childCount = 0;
                try { childCount = Convert.ToInt32(childCountObj, System.Globalization.CultureInfo.InvariantCulture); } catch { childCount = 0; }
                if (childCount <= 0)
                    return;

                for (var i = 0; i < childCount; i++)
                {
                    object childTr;
                    try { childTr = getChildMethod.Invoke(tr, new object[] { i }); }
                    catch { continue; }

                    if (childTr == null)
                        continue;

                    object childGo;
                    try { childGo = gameObjectProp.GetValueCompat(childTr); }
                    catch { childGo = null; }

                    if (childGo != null)
                        stack.Push(childGo);
                }
            }
            catch
            {
            }
        }

        private static bool TryInvokeStaticFinderWithTypeArg(string declaringTypeName, string methodName, Type desiredType, out Array resultArray)
        {
            resultArray = null;
            if (IsNullOrWhiteSpace(declaringTypeName) || IsNullOrWhiteSpace(methodName) || desiredType == null)
                return false;

            var declaringType = FindUnityType(declaringTypeName);
            if (declaringType == null)
                return false;

            var methods = declaringType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            for (var i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (m == null)
                    continue;
                if (!m.Name.Equals(methodName, StringComparison.Ordinal))
                    continue;
                if (m.IsGenericMethodDefinition)
                    continue;

                var ps = m.GetParameters();
                if (ps == null || ps.Length != 1)
                    continue;

                var arg = TryConvertTypeArgument(desiredType, ps[0].ParameterType);
                if (arg == null)
                    continue;

                object raw;
                try { raw = m.Invoke(null, new[] { arg }); }
                catch { continue; }

                var arr = CoerceToSystemArray(raw);
                if (arr != null)
                {
                    resultArray = arr;
                    return true;
                }
            }

            return false;
        }

        private static bool TryInvokeGenericFinder(string declaringTypeName, string methodName, Type genericArg, object[] args, out Array resultArray)
        {
            resultArray = null;
            if (IsNullOrWhiteSpace(declaringTypeName) || IsNullOrWhiteSpace(methodName) || genericArg == null)
                return false;

            var declaringType = FindUnityType(declaringTypeName);
            if (declaringType == null)
                return false;

            var desiredParams = args?.Length ?? 0;
            var methods = declaringType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            for (var i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (m == null)
                    continue;
                if (!m.Name.Equals(methodName, StringComparison.Ordinal))
                    continue;
                if (!m.IsGenericMethodDefinition)
                    continue;

                MethodInfo constructed;
                try { constructed = m.MakeGenericMethod(genericArg); }
                catch { continue; }

                var ps = constructed.GetParameters();
                if (ps == null || ps.Length != desiredParams)
                    continue;

                object raw;
                try { raw = constructed.Invoke(null, args); }
                catch { continue; }

                var arr = CoerceToSystemArray(raw);
                if (arr != null)
                {
                    resultArray = arr;
                    return true;
                }
            }

            return false;
        }

        private static bool TryInvokeGenericFindObjectsByType(Type genericArg, out Array resultArray)
        {
            resultArray = null;
            if (genericArg == null)
                return false;

            var unityObjectType = FindUnityType("UnityEngine.Object");
            if (unityObjectType == null)
                return false;

            var sortModeType = FindUnityType("UnityEngine.FindObjectsSortMode");
            var inactiveType = FindUnityType("UnityEngine.FindObjectsInactive");

            object sortNone = sortModeType != null ? GetEnumValue(sortModeType, "None", 0) : null;
            object inactiveInclude = inactiveType != null ? GetEnumValue(inactiveType, "Include", 1) : null;

            var methods = unityObjectType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            for (var i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (m == null)
                    continue;
                if (!m.Name.Equals("FindObjectsByType", StringComparison.Ordinal))
                    continue;
                if (!m.IsGenericMethodDefinition)
                    continue;

                MethodInfo constructed;
                try { constructed = m.MakeGenericMethod(genericArg); }
                catch { continue; }

                var ps = constructed.GetParameters();
                if (ps == null)
                    continue;

                object[] args = null;
                if (ps.Length == 1 && sortModeType != null && ps[0].ParameterType == sortModeType)
                {
                    args = new[] { sortNone };
                }
                else if (ps.Length == 2 && inactiveType != null && sortModeType != null)
                {
                    if (ps[0].ParameterType == inactiveType && ps[1].ParameterType == sortModeType)
                        args = new[] { inactiveInclude, sortNone };
                    else if (ps[0].ParameterType == sortModeType && ps[1].ParameterType == inactiveType)
                        args = new[] { sortNone, inactiveInclude };
                }

                if (args == null)
                    continue;

                object raw;
                try { raw = constructed.Invoke(null, args); }
                catch { continue; }

                var arr = CoerceToSystemArray(raw);
                if (arr != null)
                {
                    resultArray = arr;
                    return true;
                }
            }

            return false;
        }

        private static object GetEnumValue(Type enumType, string name, int fallbackValue)
        {
            try
            {
                if (enumType == null || !enumType.IsEnum)
                    return null;

                if (!IsNullOrWhiteSpace(name))
                {
                    try
                    {
                        return Enum.Parse(enumType, name, true);
                    }
                    catch
                    {
                    }
                }

                return Enum.ToObject(enumType, fallbackValue);
            }
            catch
            {
                return null;
            }
        }

        private static object TryConvertTypeArgument(Type systemType, Type expectedParamType)
        {
            if (systemType == null || expectedParamType == null)
                return null;

            if (expectedParamType == typeof(Type))
                return systemType;

            if (expectedParamType.FullName == "Il2CppSystem.Type")
            {
                var il2cppTypeType = FindUnityType("Il2CppSystem.Type");
                if (il2cppTypeType == null || !expectedParamType.IsAssignableFrom(il2cppTypeType))
                    return null;

                var getType = il2cppTypeType.GetMethod("GetType", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (getType == null)
                    return null;

                try
                {
                    var a = systemType.AssemblyQualifiedName;
                    if (!IsNullOrWhiteSpace(a))
                    {
                        var v = getType.Invoke(null, new object[] { a });
                        if (v != null)
                            return v;
                    }
                }
                catch
                {
                }

                try
                {
                    var n = systemType.FullName;
                    if (!IsNullOrWhiteSpace(n))
                    {
                        var v = getType.Invoke(null, new object[] { n });
                        if (v != null)
                            return v;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static Array CoerceToSystemArray(object value)
        {
            if (value == null)
                return null;

            var arr = value as Array;
            if (arr != null)
                return arr;

            try
            {
                var coerced = TryCoerceArrayLike(value);
                if (coerced != null)
                    return coerced;
            }
            catch
            {
            }

            var sysEnumerable = value as System.Collections.IEnumerable;
            if (sysEnumerable != null)
            {
                int capacity = 0;
                var sysCollection = value as System.Collections.ICollection;
                if (sysCollection != null)
                    capacity = sysCollection.Count;

                var list = capacity > 0 ? new List<object>(capacity) : new List<object>();
                foreach (var item in sysEnumerable)
                    list.Add(item);
                return list.ToArray();
            }

            try
            {
                var list = new List<object>();
                if (TryEnumerateViaReflection(value, list))
                    return list.ToArray();
            }
            catch
            {
            }

            return null;
        }

        private static Array TryCoerceArrayLike(object value)
        {
            if (value == null)
                return null;

            var t = value.GetType();

            try
            {
                var toArray = GetMethodCached(t, "ToArray", Type.EmptyTypes);
                if (toArray != null)
                {
                    var raw = toArray.Invoke(value, null);
                    var a = raw as Array;
                    if (a != null)
                        return a;

                    var coerced = CoerceToSystemArray(raw);
                    if (coerced != null)
                        return coerced;
                }
            }
            catch
            {
            }

            int length = 0;
            try
            {
                var lengthProp = GetPropertyCached(t, "Length");
                if (lengthProp != null)
                {
                    var v = lengthProp.GetValueCompat(value);
                    if (v != null)
                        length = Convert.ToInt32(v);
                }
            }
            catch
            {
            }

            if (length <= 0)
            {
                try
                {
                    var countProp = GetPropertyCached(t, "Count");
                    if (countProp != null)
                    {
                        var v = countProp.GetValueCompat(value);
                        if (v != null)
                            length = Convert.ToInt32(v);
                    }
                }
                catch
                {
                }
            }

            if (length <= 0)
                return null;

            if (length > 200000)
                length = 200000;

            MethodInfo getter = null;
            try { getter = GetMethodCached(t, "get_Item", new[] { typeof(int) }); }
            catch { }
            if (getter == null)
            {
                try { getter = GetMethodCached(t, "GetValue", new[] { typeof(int) }); }
                catch { }
            }

            if (getter == null)
                return null;

            var output = new object[length];
            for (var i = 0; i < length; i++)
            {
                object item = null;
                try { item = getter.Invoke(value, new object[] { i }); }
                catch { }
                output[i] = item;
            }

            return output;
        }

        private static bool TryEnumerateViaReflection(object value, List<object> output)
        {
            if (value == null || output == null)
                return false;

            try
            {
                var t = value.GetType();
                var getEnumerator = GetMethodCached(t, "GetEnumerator", Type.EmptyTypes);
                if (getEnumerator == null)
                    return false;

                var enumerator = getEnumerator.Invoke(value, null);
                if (enumerator == null)
                    return false;

                var et = enumerator.GetType();
                var moveNext = GetMethodCached(et, "MoveNext", Type.EmptyTypes);
                if (moveNext == null)
                    return false;

                var currentProp = GetPropertyCached(et, "Current");
                var getCurrent = currentProp != null ? null : GetMethodCached(et, "get_Current", Type.EmptyTypes);

                while (true)
                {
                    var ok = moveNext.Invoke(enumerator, null);
                    if (!(ok is bool) || !(bool)ok)
                        break;

                    object current = null;
                    if (currentProp != null)
                        current = currentProp.GetValueCompat(enumerator);
                    else if (getCurrent != null)
                        current = getCurrent.Invoke(enumerator, null);

                    output.Add(current);
                    if (output.Count > 200000)
                        break;
                }

                return output.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetActiveInHierarchy(object obj, out bool active)
        {
            active = false;
            if (obj == null)
                return false;

            try
            {
                var goType = FindUnityType("UnityEngine.GameObject");
                var componentType = FindUnityType("UnityEngine.Component");
                if (goType != null && goType.IsInstanceOfType(obj))
                {
                    var prop = goType.GetProperty("activeInHierarchy", BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null)
                    {
                        active = Convert.ToBoolean(prop.GetValueCompat(obj));
                        return true;
                    }
                }

                if (componentType != null && componentType.IsInstanceOfType(obj))
                {
                    var goProp = componentType.GetProperty("gameObject", BindingFlags.Public | BindingFlags.Instance);
                    var go = goProp?.GetValueCompat(obj);
                    if (go != null && goType != null)
                    {
                        var prop = goType.GetProperty("activeInHierarchy", BindingFlags.Public | BindingFlags.Instance);
                        if (prop != null)
                        {
                            active = Convert.ToBoolean(prop.GetValueCompat(go));
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryGetWorldPosition(object obj, out string position)
        {
            position = null;
            if (obj == null)
                return false;
            try
            {
                object transform = null;
                var goType = FindUnityType("UnityEngine.GameObject");
                var componentType = FindUnityType("UnityEngine.Component");
                if (goType != null && goType.IsInstanceOfType(obj))
                {
                    var prop = goType.GetProperty("transform", BindingFlags.Public | BindingFlags.Instance);
                    transform = prop?.GetValueCompat(obj);
                }
                else if (componentType != null && componentType.IsInstanceOfType(obj))
                {
                    var prop = componentType.GetProperty("transform", BindingFlags.Public | BindingFlags.Instance);
                    transform = prop?.GetValueCompat(obj);
                }

                if (transform == null)
                    return false;

                var t = transform.GetType();
                var posProp = t.GetProperty("position", BindingFlags.Public | BindingFlags.Instance);
                var pos = posProp?.GetValueCompat(transform);
                if (pos == null)
                    return false;

                var p = ReadVector3(pos);
                position = $"{p.X:F2},{p.Y:F2},{p.Z:F2}";
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<string> TryGetComponentTypeNames(object obj)
        {
            try
            {
                var goType = FindUnityType("UnityEngine.GameObject");
                if (goType == null || !goType.IsInstanceOfType(obj))
                    return null;

                var componentType = FindUnityType("UnityEngine.Component");
                if (componentType == null)
                    return null;

                var getComponents = goType.GetMethod("GetComponents", new[] { typeof(Type) });
                if (getComponents == null)
                    return null;

                var components = CoerceToSystemArray(getComponents.Invoke(obj, new object[] { componentType }));
                if (components == null)
                    return null;

                var list = new List<string>();
                foreach (var c in components)
                {
                    if (c == null)
                        continue;
                    list.Add(c.GetType().FullName ?? c.GetType().Name);
                }
                return list;
            }
            catch
            {
                return null;
            }
        }

        private static MethodInfo BuildPrefixPatch(string mode)
        {
            if (mode.Equals("DISABLE", StringComparison.OrdinalIgnoreCase))
            {
                var dm = new DynamicMethod("URT_Prefix_Disable", typeof(bool), Type.EmptyTypes, typeof(UniversalMelonMod), true);
                var il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ret);
                return dm;
            }

            if (mode.Equals("LOG", StringComparison.OrdinalIgnoreCase))
            {
                var dm = new DynamicMethod("URT_Prefix_Log", typeof(bool), new[] { typeof(MethodBase) }, typeof(UniversalMelonMod), true);
                var il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldstr, "[URT] Enter: ");
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("ToString"));
                il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) }));
                il.Emit(OpCodes.Call, typeof(MelonLoader.MelonLogger).GetMethod("Msg", new[] { typeof(string) }));
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ret);
                return dm;
            }

            return null;
        }

        private static MethodInfo BuildPostfixPatch(string mode)
        {
            if (mode.Equals("LOG", StringComparison.OrdinalIgnoreCase))
            {
                var dm = new DynamicMethod("URT_Postfix_Log", typeof(void), new[] { typeof(MethodBase) }, typeof(UniversalMelonMod), true);
                var il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldstr, "[URT] Exit: ");
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("ToString"));
                il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) }));
                il.Emit(OpCodes.Call, typeof(MelonLoader.MelonLogger).GetMethod("Msg", new[] { typeof(string) }));
                il.Emit(OpCodes.Ret);
                return dm;
            }

            return null;
        }
        
        private static string GetInstanceFieldValue(string typeName, string instanceId, string fieldName)
        {
            try
            {
                var instance = ResolveTrackedObject(instanceId);
                if (instance == null)
                    return $"ERROR|Instance not found: {instanceId}";

                if (!IsNullOrWhiteSpace(typeName))
                {
                    var expected = ResolveTypeByName(typeName);
                    if (expected != null && !expected.IsInstanceOfType(instance))
                        return $"ERROR|Instance type mismatch: expected {typeName}, got {instance.GetType().FullName}";
                }

                if (IsNullOrWhiteSpace(fieldName))
                    return "ERROR|Missing field name";

                var t = instance.GetType();
                var field = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null)
                    return $"ERROR|Field not found: {fieldName}";

                var value = field.GetValue(instance);
                return $"SUCCESS|{FormatValue(value)}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static string SetInstanceFieldValue(string typeName, string instanceId, string fieldName, string value)
        {
            try
            {
                var instance = ResolveTrackedObject(instanceId);
                if (instance == null)
                    return $"ERROR|Instance not found: {instanceId}";

                if (!IsNullOrWhiteSpace(typeName))
                {
                    var expected = ResolveTypeByName(typeName);
                    if (expected != null && !expected.IsInstanceOfType(instance))
                        return $"ERROR|Instance type mismatch: expected {typeName}, got {instance.GetType().FullName}";
                }

                if (IsNullOrWhiteSpace(fieldName))
                    return "ERROR|Missing field name";

                var t = instance.GetType();
                var field = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null)
                    return $"ERROR|Field not found: {fieldName}";

                var converted = ConvertStringToType(value, field.FieldType);
                field.SetValue(instance, converted);
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string GetInstancePropertyValue(string typeName, string instanceId, string propertyName)
        {
            try
            {
                var instance = ResolveTrackedObject(instanceId);
                if (instance == null)
                    return $"ERROR|Instance not found: {instanceId}";

                if (!IsNullOrWhiteSpace(typeName))
                {
                    var expected = ResolveTypeByName(typeName);
                    if (expected != null && !expected.IsInstanceOfType(instance))
                        return $"ERROR|Instance type mismatch: expected {typeName}, got {instance.GetType().FullName}";
                }

                if (IsNullOrWhiteSpace(propertyName))
                    return "ERROR|Missing property name";

                var t = instance.GetType();
                var prop = t.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop == null)
                    return $"ERROR|Property not found: {propertyName}";

                if (prop.GetIndexParameters().Length != 0)
                    return "ERROR|Indexed properties are not supported";

                if (!prop.CanRead)
                    return "ERROR|Property is not readable";

                var value = prop.GetValueCompat(instance);
                return $"SUCCESS|{FormatValue(value)}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetInstancePropertyValue(string typeName, string instanceId, string propertyName, string value)
        {
            try
            {
                var instance = ResolveTrackedObject(instanceId);
                if (instance == null)
                    return $"ERROR|Instance not found: {instanceId}";

                if (!IsNullOrWhiteSpace(typeName))
                {
                    var expected = ResolveTypeByName(typeName);
                    if (expected != null && !expected.IsInstanceOfType(instance))
                        return $"ERROR|Instance type mismatch: expected {typeName}, got {instance.GetType().FullName}";
                }

                if (IsNullOrWhiteSpace(propertyName))
                    return "ERROR|Missing property name";

                var t = instance.GetType();
                var prop = t.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop == null)
                    return $"ERROR|Property not found: {propertyName}";

                if (prop.GetIndexParameters().Length != 0)
                    return "ERROR|Indexed properties are not supported";

                if (!prop.CanWrite)
                    return "ERROR|Property is not writable";

                var converted = ConvertStringToType(value, prop.PropertyType);
                prop.SetValueCompat(instance, converted);
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static string CreateInstance(string typeName, string constructorArgs)
        {
            try
            {
                var type = ResolveTypeByName(typeName);
                
                if (type == null)
                    return $"ERROR|Type not found: {typeName}";
                
                object instance;
                if (IsNullOrWhiteSpace(constructorArgs) || constructorArgs.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    instance = Activator.CreateInstance(type);
                }
                else
                {
                    var tokens = constructorArgs.Split(new[] { ',' }, StringSplitOptions.None)
                        .Select(t => t.Trim())
                        .ToArray();

                    var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    ConstructorInfo best = null;
                    object[] bestArgs = null;

                    foreach (var ctor in ctors)
                    {
                        var parameters = ctor.GetParameters();
                        if (parameters.Length != tokens.Length)
                            continue;

                        var args = new object[parameters.Length];
                        var ok = true;
                        for (var i = 0; i < parameters.Length; i++)
                        {
                            try
                            {
                                args[i] = ConvertStringToType(tokens[i], parameters[i].ParameterType);
                            }
                            catch
                            {
                                ok = false;
                                break;
                            }
                        }

                        if (!ok)
                            continue;

                        best = ctor;
                        bestArgs = args;
                        break;
                    }

                    if (best == null)
                        return "ERROR|No matching constructor found";

                    instance = best.Invoke(bestArgs);
                }

                var id = TrackObject(instance);
                return $"SUCCESS|{id}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static string GetInstanceFields(string typeName, string instanceId)
        {
            try
            {
                var instance = ResolveTrackedObject(instanceId);
                if (instance == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var t = instance.GetType();
                var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var sb = new StringBuilder();
                sb.Append("FIELDS");
                foreach (var f in fields.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append('|');
                    sb.Append(f.FieldType.FullName ?? f.FieldType.Name);
                    sb.Append(' ');
                    sb.Append(f.Name);
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static string GetInstanceProperties(string typeName, string instanceId)
        {
            try
            {
                var instance = ResolveTrackedObject(instanceId);
                if (instance == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var t = instance.GetType();
                var props = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var sb = new StringBuilder();
                sb.Append("PROPERTIES");
                foreach (var p in props.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append('|');
                    sb.Append(p.PropertyType.FullName ?? p.PropertyType.Name);
                    sb.Append(' ');
                    sb.Append(p.Name);
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static string GetInstanceMethods(string typeName, string instanceId)
        {
            try
            {
                var instance = ResolveTrackedObject(instanceId);
                if (instance == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var t = instance.GetType();
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var sb = new StringBuilder();
                sb.Append("METHODS");
                foreach (var m in methods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append('|');
                    sb.Append(m.ReturnType.FullName ?? m.ReturnType.Name);
                    sb.Append(' ');
                    sb.Append(m.Name);
                    sb.Append('(');
                    var ps = m.GetParameters();
                    for (var i = 0; i < ps.Length; i++)
                    {
                        if (i > 0)
                            sb.Append(", ");
                        sb.Append(ps[i].ParameterType.FullName ?? ps[i].ParameterType.Name);
                    }
                    sb.Append(')');
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static string EnumerateObjects(string typeName)
        {
            try
            {
                var type = ResolveTypeByName(typeName);
                if (type == null)
                    return $"ERROR|Type not found: {typeName}";

                var objects = FindUnityObjectsOfType(type);
                if (objects == null)
                    return "ERROR|Unity object enumeration unavailable";

                var sb = new StringBuilder();
                sb.Append("OBJECTS");
                sb.Append("|type=");
                sb.Append(type.FullName ?? type.Name);
                sb.Append("|count=");
                sb.Append(objects.Length);

                var limit = Math.Min(objects.Length, 500);
                for (var i = 0; i < limit; i++)
                {
                    var obj = objects.GetValue(i);
                    if (obj == null)
                        continue;

                    var id = TrackObject(obj);
                    var name = GetUnityObjectName(obj) ?? "";
                    var active = TryGetActiveInHierarchy(obj, out var isActive) ? (isActive ? "1" : "0") : "";

                    sb.Append("|id=");
                    sb.Append(id);
                    sb.Append(";name=");
                    sb.Append(name.Replace("|", " ").Replace(";", " "));
                    if (!string.IsNullOrEmpty(active))
                    {
                        sb.Append(";active=");
                        sb.Append(active);
                    }
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static string FindObjects(string typeName, string filter)
        {
            try
            {
                var type = ResolveTypeByName(typeName);
                if (type == null)
                    return $"ERROR|Type not found: {typeName}";

                var objects = FindUnityObjectsOfType(type);
                if (objects == null)
                    return "ERROR|Unity object enumeration unavailable";

                var needle = filter ?? "";
                var sb = new StringBuilder();
                sb.Append("OBJECTS");
                sb.Append("|type=");
                sb.Append(type.FullName ?? type.Name);

                var matched = 0;
                var emitted = 0;
                for (var i = 0; i < objects.Length; i++)
                {
                    var obj = objects.GetValue(i);
                    if (obj == null)
                        continue;

                    if (!IsObjectInLoadedScene(obj))
                        continue;

                    var name = GetUnityObjectName(obj) ?? "";
                    var id = TryGetUnityInstanceId(obj, out var uid) ? uid : 0;

                    var ok = IsNullOrWhiteSpace(needle)
                             || name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                             || (int.TryParse(needle, out var idNeedle) && idNeedle != 0 && idNeedle == id);

                    if (!ok)
                        continue;

                    matched++;
                    if (emitted >= 500)
                        continue;

                    var trackedId = TrackObject(obj);
                    var active = TryGetActiveInHierarchy(obj, out var isActive) ? (isActive ? "1" : "0") : "";

                    sb.Append("|id=");
                    sb.Append(trackedId);
                    sb.Append(";name=");
                    sb.Append(name.Replace("|", " ").Replace(";", " "));
                    if (!string.IsNullOrEmpty(active))
                    {
                        sb.Append(";active=");
                        sb.Append(active);
                    }

                    emitted++;
                }

                sb.Append("|count=");
                sb.Append(matched);

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static bool IsObjectInLoadedScene(object obj)
        {
            try
            {
                var go = TryGetGameObject(obj);
                if (go == null)
                    return false;

                var goType = go.GetType();
                var sceneProp = GetPropertyCached(goType, "scene");
                if (sceneProp == null)
                    return false;

                var scene = sceneProp.GetValueCompat(go);
                if (scene == null)
                    return false;

                var sceneType = scene.GetType();

                var isLoadedProp = GetPropertyCached(sceneType, "isLoaded");
                if (isLoadedProp != null)
                {
                    try
                    {
                        var loadedVal = isLoadedProp.GetValueCompat(scene);
                        if (loadedVal is bool b)
                            return b;
                        if (loadedVal != null)
                            return Convert.ToBoolean(loadedVal);
                    }
                    catch
                    {
                    }
                }

                var isValidMethod = GetMethodCached(sceneType, "IsValid", Type.EmptyTypes);
                if (isValidMethod != null && isValidMethod.ReturnType == typeof(bool))
                {
                    try
                    {
                        var validVal = isValidMethod.Invoke(scene, null);
                        if (validVal is bool vb)
                            return vb;
                        if (validVal != null)
                            return Convert.ToBoolean(validVal);
                    }
                    catch
                    {
                    }
                }

                var nameProp = GetPropertyCached(sceneType, "name");
                if (nameProp != null)
                {
                    try
                    {
                        var n = nameProp.GetValueCompat(scene)?.ToString();
                        return !IsNullOrWhiteSpace(n);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return false;
        }
        
        private static string GetObjectInfo(string typeName, string instanceId)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                if (!IsNullOrWhiteSpace(typeName))
                {
                    var expected = ResolveTypeByName(typeName);
                    if (expected != null && !expected.IsInstanceOfType(obj))
                        return $"ERROR|Instance type mismatch: expected {typeName}, got {obj.GetType().FullName}";
                }

                var sb = new StringBuilder();
                sb.Append("OBJECT_INFO");
                sb.Append("|id=");
                sb.Append(instanceId);
                sb.Append("|type=");
                sb.Append(obj.GetType().FullName ?? obj.GetType().Name);
                sb.Append("|name=");
                sb.Append((GetUnityObjectName(obj) ?? "").Replace("|", " "));

                var go = TryGetGameObject(obj);
                if (go != null)
                {
                    var goType = go.GetType();
                    var tagProp = GetPropertyCached(goType, "tag");
                    var layerProp = GetPropertyCached(goType, "layer");
                    var tag = tagProp?.GetValueCompat(go)?.ToString();
                    if (!IsNullOrWhiteSpace(tag))
                    {
                        sb.Append("|tag=");
                        sb.Append(tag.Replace("|", " "));
                    }
                    if (layerProp != null)
                    {
                        try
                        {
                            var layerVal = layerProp.GetValueCompat(go);
                            if (layerVal != null)
                            {
                                sb.Append("|layer=");
                                sb.Append(Convert.ToInt32(layerVal));
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                if (TryGetActiveInHierarchy(obj, out var active))
                {
                    sb.Append("|active=");
                    sb.Append(active ? "1" : "0");
                }

                if (TryGetWorldPosition(obj, out var pos))
                {
                    sb.Append("|pos=");
                    sb.Append(pos);
                }

                var componentNames = TryGetComponentTypeNames(obj);
                if (componentNames != null && componentNames.Count > 0)
                {
                    sb.Append("|components=");
                    sb.Append(string.Join(",", componentNames.Take(50).ToArray()));
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string StartSceneSync(string[] parts)
        {
            _sceneSyncSessionId = _sceneSyncSessionId <= 0 ? 1 : unchecked(_sceneSyncSessionId + 1);
            _sceneSyncActive = true;
            _sceneSyncCleanupRequested = false;
            _sceneSyncLastScanTicks = 0;
            _sceneSyncLastSceneSignatureCheckTicks = 0;
            _sceneSyncNextScanStartTicks = 0;
            _sceneSyncScanInProgress = false;
            _sceneSyncScanForceCreate = true;
            _sceneSyncScanStack.Clear();
            _sceneSyncScanVisited.Clear();
            _sceneSyncScanCurrent.Clear();
            _sceneSyncObjects.Clear();
            lock (_sceneSyncEventsLock)
            {
                _sceneSyncEvents.Clear();
                _sceneSyncEvents.Enqueue("e=RESET;id=0");
            }
            _sceneSyncPendingDestroy.Clear();
            _sceneSyncAwaitingDestroyDrain = false;

            _sceneSyncLastSceneSignature = GetSceneSignatureFast();
            BeginSceneSyncScan(forceCreate: true);

            var activeScene = "";
            try
            {
                var sceneManager = FindUnityType("UnityEngine.SceneManagement.SceneManager");
                if (sceneManager != null)
                {
                    var getActive = sceneManager.GetMethod("GetActiveScene", BindingFlags.Public | BindingFlags.Static);
                    if (getActive != null)
                    {
                        var scene = getActive.Invoke(null, null);
                        if (scene != null)
                        {
                            var nameProp = scene.GetType().GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
                            activeScene = nameProp?.GetValueCompat(scene)?.ToString() ?? "";
                        }
                    }
                }
            }
            catch
            {
            }

            return $"SCENE_SYNC_STARTED|sid={_sceneSyncSessionId}|active={SanitizePipeValue(activeScene)}";
        }

        private static string StopSceneSync(string[] parts)
        {
            var sid = _sceneSyncSessionId;
            _sceneSyncActive = false;
            _sceneSyncCleanupRequested = true;
            lock (_sceneSyncEventsLock)
            {
                _sceneSyncEvents.Clear();
            }
            return $"SCENE_SYNC_STOPPED|sid={sid}";
        }

        private static string PollSceneSync(string[] parts)
        {
            if (!_sceneSyncActive)
                return "ERROR|Scene sync not active";

            var maxEvents = 64;
            if (parts != null && parts.Length > 1)
            {
                if (int.TryParse(parts[1], out var parsed) && parsed > 0)
                    maxEvents = Math.Min(parsed, 256);
            }

            var sb = new StringBuilder();
            sb.Append("SCENE_SYNC|sid=");
            sb.Append(_sceneSyncSessionId);

            var batch = new List<string>(maxEvents);
            lock (_sceneSyncEventsLock)
            {
                var take = 0;
                while (take < maxEvents && _sceneSyncEvents.Count > 0)
                {
                    var next = _sceneSyncEvents.Dequeue();
                    if (IsNullOrWhiteSpace(next))
                        continue;
                    batch.Add(next);
                    take++;
                }
            }

            var emitted = 0;
            while (emitted < batch.Count)
            {
                var next = batch[emitted];
                if (IsNullOrWhiteSpace(next))
                    continue;

                if (sb.Length + next.Length + 2 > 60000)
                    break;

                sb.Append('|');
                sb.Append(next);
                emitted++;
            }

            sb.Append("|count=");
            sb.Append(emitted);
            return sb.ToString();
        }

        private static void EnqueueSceneSyncEvent(string evt)
        {
            if (IsNullOrWhiteSpace(evt))
                return;

            lock (_sceneSyncEventsLock)
            {
                if (_sceneSyncEvents.Count > 12000)
                    _sceneSyncEvents.Clear();
                _sceneSyncEvents.Enqueue(evt);
            }
        }

        private static void UpdateSceneSync(bool force = false)
        {
            if (!_sceneSyncActive)
            {
                if (_sceneSyncCleanupRequested)
                {
                    _sceneSyncCleanupRequested = false;
                    _sceneSyncLastScanTicks = 0;
                    _sceneSyncLastSceneSignatureCheckTicks = 0;
                    _sceneSyncNextScanStartTicks = 0;
                    _sceneSyncScanInProgress = false;
                    _sceneSyncScanForceCreate = false;
                    _sceneSyncScanStack.Clear();
                    _sceneSyncScanVisited.Clear();
                    _sceneSyncScanCurrent.Clear();
                    _sceneSyncObjects.Clear();
                    _sceneSyncPendingDestroy.Clear();
                    _sceneSyncAwaitingDestroyDrain = false;
                    _sceneSyncLastSceneSignature = "";
                }
                return;
            }

            var now = Stopwatch.GetTimestamp();
            if (!force)
            {
                var minTicks = Stopwatch.Frequency / 6;
                if (_fps > 0)
                {
                    if (_fps < 10)
                        minTicks = Stopwatch.Frequency;
                    else if (_fps < 20)
                        minTicks = Stopwatch.Frequency / 2;
                    else if (_fps < 35)
                        minTicks = Stopwatch.Frequency / 3;
                }
                if (now - _sceneSyncLastScanTicks < minTicks)
                    return;
            }
            _sceneSyncLastScanTicks = now;

            try
            {
                var goType = FindUnityType("UnityEngine.GameObject");
                if (goType == null)
                    return;

                if (_sceneSyncLastSceneSignatureCheckTicks == 0 || (now - _sceneSyncLastSceneSignatureCheckTicks) > (Stopwatch.Frequency / 2))
                {
                    _sceneSyncLastSceneSignatureCheckTicks = now;
                    var sig = GetSceneSignatureFast();
                    if (!string.Equals(sig, _sceneSyncLastSceneSignature, StringComparison.Ordinal))
                    {
                        _sceneSyncLastSceneSignature = sig;
                        lock (_sceneSyncEventsLock)
                        {
                            _sceneSyncEvents.Clear();
                            _sceneSyncEvents.Enqueue("e=RESET;id=0");
                        }
                        if (_sceneSyncObjects.Count > 0)
                        {
                            foreach (var id in _sceneSyncObjects.Keys)
                                _sceneSyncPendingDestroy.Enqueue(id);
                        }
                        _sceneSyncObjects.Clear();
                        _sceneSyncAwaitingDestroyDrain = _sceneSyncPendingDestroy.Count > 0;
                        _sceneSyncScanInProgress = false;
                        _sceneSyncScanStack.Clear();
                        _sceneSyncScanVisited.Clear();
                        _sceneSyncScanCurrent.Clear();
                        _sceneSyncNextScanStartTicks = 0;
                    }
                }

                var destroyBurst = 0;
                while (destroyBurst < 64 && _sceneSyncPendingDestroy.Count > 0)
                {
                    if (!_sceneSyncActive)
                        return;
                    var id = _sceneSyncPendingDestroy.Dequeue();
                    EnqueueSceneSyncEvent("e=DESTROY;id=" + id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    destroyBurst++;
                }

                if (_sceneSyncAwaitingDestroyDrain)
                {
                    if (_sceneSyncPendingDestroy.Count > 0)
                        return;
                    _sceneSyncAwaitingDestroyDrain = false;
                    BeginSceneSyncScan(forceCreate: true);
                    return;
                }

                if (!_sceneSyncScanInProgress)
                {
                    if (force || _sceneSyncNextScanStartTicks == 0 || now >= _sceneSyncNextScanStartTicks)
                        BeginSceneSyncScan(forceCreate: false);
                    return;
                }

                var maxPerTick = 260;
                if (_fps > 0)
                {
                    if (_fps < 8)
                        maxPerTick = 20;
                    else if (_fps < 15)
                        maxPerTick = 60;
                    else if (_fps < 25)
                        maxPerTick = 120;
                    else if (_fps < 40)
                        maxPerTick = 220;
                    else
                        maxPerTick = 360;
                }

                var startWork = Stopwatch.GetTimestamp();
                var processed = 0;
                while (processed < maxPerTick && _sceneSyncScanStack.Count > 0)
                {
                    if (!_sceneSyncActive)
                        return;
                    if ((Stopwatch.GetTimestamp() - startWork) > (Stopwatch.Frequency / 220))
                        break;

                    var go = _sceneSyncScanStack.Pop();
                    if (go == null)
                        continue;

                    if (TryGetUnityInstanceId(go, out var unityId) && unityId != 0)
                    {
                        if (!_sceneSyncScanVisited.Add(unityId))
                            continue;
                    }

                    var id = TrackObject(go);
                    if (id == 0)
                        continue;

                    _sceneSyncScanCurrent.Add(id);

                    var name = GetUnityObjectName(go) ?? "";
                    var active = TryGetActiveInHierarchy(go, out var act) && act;

                    var parentId = 0;
                    string localPos = "";
                    string localRot = "";
                    string localScale = "";
                    if (TryGetTransform(go, out var tr) && tr != null)
                    {
                        parentId = TryGetTransformParentId(tr);

                        var t = tr.GetType();
                        localPos = FormatVector3(GetPropertyCached(t, "localPosition")?.GetValueCompat(tr));
                        localRot = FormatVector3(GetPropertyCached(t, "localEulerAngles")?.GetValueCompat(tr));
                        localScale = FormatVector3(GetPropertyCached(t, "localScale")?.GetValueCompat(tr));
                    }

                    var isNew = !_sceneSyncObjects.TryGetValue(id, out var prev);
                    var wantFull = isNew || (prev.LastFullTicks == 0 || (now - prev.LastFullTicks) > (Stopwatch.Frequency * 2));
                    if (_fps > 0 && _fps < 12)
                        wantFull = isNew;

                    var boundsCenter = isNew ? "" : prev.BoundsCenter;
                    var boundsSize = isNew ? "" : prev.BoundsSize;
                    var colliderCenter = isNew ? "" : prev.ColliderCenter;
                    var colliderSize = isNew ? "" : prev.ColliderSize;
                    var comps = isNew ? "" : prev.Components;
                    if (wantFull)
                    {
                        TryGetRenderableBounds(go, out boundsCenter, out boundsSize);
                        TryGetColliderBounds(go, out colliderCenter, out colliderSize);

                        var compNames = TryGetComponentTypeNames(go);
                        comps = compNames != null && compNames.Count > 0
                            ? string.Join(",", compNames.Take(40).Select(SanitizePipeValue).ToArray())
                            : "";
                    }

                    var entry = new SceneSyncEntry
                    {
                        ParentId = parentId,
                        Name = name,
                        Active = active,
                        LocalPos = localPos,
                        LocalRot = localRot,
                        LocalScale = localScale,
                        BoundsCenter = boundsCenter,
                        BoundsSize = boundsSize,
                        ColliderCenter = colliderCenter,
                        ColliderSize = colliderSize,
                        Components = comps,
                        LastFullTicks = wantFull ? now : prev.LastFullTicks
                    };

                    if (isNew || _sceneSyncScanForceCreate)
                    {
                        _sceneSyncObjects[id] = entry;
                        EnqueueSceneSyncEvent(BuildSceneSyncUpsertEvent("CREATE", id, entry));
                    }
                    else
                    {
                        var changed = prev.ParentId != entry.ParentId ||
                                      !string.Equals(prev.Name, entry.Name, StringComparison.Ordinal) ||
                                      prev.Active != entry.Active ||
                                      !string.Equals(prev.LocalPos, entry.LocalPos, StringComparison.Ordinal) ||
                                      !string.Equals(prev.LocalRot, entry.LocalRot, StringComparison.Ordinal) ||
                                      !string.Equals(prev.LocalScale, entry.LocalScale, StringComparison.Ordinal) ||
                                      !string.Equals(prev.BoundsCenter, entry.BoundsCenter, StringComparison.Ordinal) ||
                                      !string.Equals(prev.BoundsSize, entry.BoundsSize, StringComparison.Ordinal) ||
                                      !string.Equals(prev.ColliderCenter, entry.ColliderCenter, StringComparison.Ordinal) ||
                                      !string.Equals(prev.ColliderSize, entry.ColliderSize, StringComparison.Ordinal) ||
                                      !string.Equals(prev.Components, entry.Components, StringComparison.Ordinal);

                        if (changed)
                        {
                            _sceneSyncObjects[id] = entry;
                            EnqueueSceneSyncEvent(BuildSceneSyncUpsertEvent("UPDATE", id, entry));
                        }
                        else
                        {
                            _sceneSyncObjects[id] = entry;
                        }
                    }

                    PushGameObjectChildren(go, _sceneSyncScanStack);
                    processed++;
                    _sceneSyncScanProcessed++;
                    if (_sceneSyncScanProcessed >= _sceneSyncScanMaxObjects)
                        _sceneSyncScanStack.Clear();
                }

                if (_sceneSyncScanStack.Count == 0)
                {
                    if (_sceneSyncObjects.Count > 0)
                    {
                        var removed = new List<int>();
                        foreach (var kv in _sceneSyncObjects)
                        {
                            if (!_sceneSyncScanCurrent.Contains(kv.Key))
                                removed.Add(kv.Key);
                        }
                        for (var i = 0; i < removed.Count; i++)
                        {
                            var id = removed[i];
                            _sceneSyncObjects.Remove(id);
                            _sceneSyncPendingDestroy.Enqueue(id);
                        }
                    }

                    _sceneSyncScanInProgress = false;
                    _sceneSyncScanForceCreate = false;
                    _sceneSyncScanVisited.Clear();
                    _sceneSyncScanCurrent.Clear();

                    var next = Stopwatch.Frequency / 2;
                    if (_fps > 0)
                    {
                        if (_fps < 10)
                            next = Stopwatch.Frequency * 2;
                        else if (_fps < 20)
                            next = Stopwatch.Frequency;
                        else if (_fps < 35)
                            next = Stopwatch.Frequency * 3 / 4;
                    }
                    _sceneSyncNextScanStartTicks = Stopwatch.GetTimestamp() + next;
                }
            }
            catch
            {
            }
        }

        private static void BeginSceneSyncScan(bool forceCreate)
        {
            if (!_sceneSyncActive)
                return;

            try
            {
                var goType = FindUnityType("UnityEngine.GameObject");
                if (goType == null)
                    return;

                var roots = GetLoadedSceneRootGameObjects(goType, _sceneSyncScanMaxObjects);
                if (roots == null || roots.Count == 0)
                    roots = FindSceneGameObjects(goType, _sceneSyncScanMaxObjects);
                if (roots == null || roots.Count == 0)
                    return;

                _sceneSyncScanStack.Clear();
                _sceneSyncScanVisited.Clear();
                _sceneSyncScanCurrent.Clear();
                _sceneSyncScanProcessed = 0;
                _sceneSyncScanForceCreate = forceCreate;
                _sceneSyncScanInProgress = true;

                for (var i = 0; i < roots.Count; i++)
                {
                    var r = roots[i];
                    if (r != null)
                        _sceneSyncScanStack.Push(r);
                }
            }
            catch
            {
            }
        }

        private static string GetSceneSignatureFast()
        {
            try
            {
                var sceneManager = FindUnityType("UnityEngine.SceneManagement.SceneManager");
                if (sceneManager == null)
                    return "";

                var countProp = sceneManager.GetProperty("sceneCount", BindingFlags.Public | BindingFlags.Static);
                var sceneCount = 0;
                try { sceneCount = countProp != null ? Convert.ToInt32(countProp.GetValueCompat(null), System.Globalization.CultureInfo.InvariantCulture) : 0; }
                catch { sceneCount = 0; }

                var getActive = sceneManager.GetMethod("GetActiveScene", BindingFlags.Public | BindingFlags.Static);
                if (getActive == null)
                    return sceneCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

                var scene = getActive.Invoke(null, null);
                if (scene == null)
                    return sceneCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

                var st = scene.GetType();
                var nameProp = st.GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
                var buildIndexProp = st.GetProperty("buildIndex", BindingFlags.Public | BindingFlags.Instance);

                var name = "";
                try { name = nameProp?.GetValueCompat(scene)?.ToString() ?? ""; } catch { name = ""; }
                var build = -1;
                try { build = buildIndexProp != null ? Convert.ToInt32(buildIndexProp.GetValueCompat(scene), System.Globalization.CultureInfo.InvariantCulture) : -1; }
                catch { build = -1; }

                return name + "|" + build.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + sceneCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return "";
            }
        }

        private static string BuildSceneSyncUpsertEvent(string kind, int id, SceneSyncEntry entry)
        {
            var evt = new StringBuilder(256);
            evt.Append("e=");
            evt.Append(kind);
            evt.Append(";id=");
            evt.Append(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            evt.Append(";parent=");
            evt.Append(entry.ParentId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            evt.Append(";name=");
            evt.Append(SanitizePipeValue(entry.Name));
            evt.Append(";active=");
            evt.Append(entry.Active ? "1" : "0");
            if (!IsNullOrWhiteSpace(entry.LocalPos))
            {
                evt.Append(";lp=");
                evt.Append(entry.LocalPos);
            }
            if (!IsNullOrWhiteSpace(entry.LocalRot))
            {
                evt.Append(";lr=");
                evt.Append(entry.LocalRot);
            }
            if (!IsNullOrWhiteSpace(entry.LocalScale))
            {
                evt.Append(";ls=");
                evt.Append(entry.LocalScale);
            }
            if (!IsNullOrWhiteSpace(entry.BoundsCenter))
            {
                evt.Append(";bc=");
                evt.Append(entry.BoundsCenter);
            }
            if (!IsNullOrWhiteSpace(entry.BoundsSize))
            {
                evt.Append(";bs=");
                evt.Append(entry.BoundsSize);
            }
            if (!IsNullOrWhiteSpace(entry.ColliderCenter))
            {
                evt.Append(";cc=");
                evt.Append(entry.ColliderCenter);
            }
            if (!IsNullOrWhiteSpace(entry.ColliderSize))
            {
                evt.Append(";cs=");
                evt.Append(entry.ColliderSize);
            }
            if (!IsNullOrWhiteSpace(entry.Components))
            {
                evt.Append(";comps=");
                evt.Append(entry.Components);
            }
            return evt.ToString();
        }

        private static int TryGetTransformParentId(object transform)
        {
            if (transform == null)
                return 0;
            try
            {
                var t = transform.GetType();
                var parentProp = GetPropertyCached(t, "parent");
                var parent = parentProp?.GetValueCompat(transform);
                if (parent == null)
                    return 0;
                var parentGo = TryGetTransformGameObject(parent);
                if (parentGo == null)
                    return 0;
                return TrackObject(parentGo);
            }
            catch
            {
                return 0;
            }
        }

        private static bool TryGetRenderableBounds(object go, out string center, out string size)
        {
            center = "";
            size = "";
            if (go == null)
                return false;
            try
            {
                var rendererType = FindUnityType("UnityEngine.Renderer");
                if (rendererType == null)
                    return false;

                var getComp = GetMethodCached(go.GetType(), "GetComponent", new[] { typeof(Type) });
                if (getComp == null)
                    return false;

                var renderer = getComp.Invoke(go, new object[] { rendererType });
                if (renderer == null)
                    return false;

                var boundsProp = GetPropertyCached(renderer.GetType(), "bounds");
                var bounds = boundsProp?.GetValueCompat(renderer);
                if (bounds == null)
                    return false;

                var bt = bounds.GetType();
                var centerProp = GetPropertyCached(bt, "center");
                var sizeProp = GetPropertyCached(bt, "size");
                center = FormatVector3(centerProp?.GetValueCompat(bounds));
                size = FormatVector3(sizeProp?.GetValueCompat(bounds));
                return !IsNullOrWhiteSpace(center) && !IsNullOrWhiteSpace(size);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetColliderBounds(object go, out string center, out string size)
        {
            center = "";
            size = "";
            if (go == null)
                return false;
            try
            {
                var colliderType = FindUnityType("UnityEngine.Collider");
                if (colliderType == null)
                    return false;

                var getComp = GetMethodCached(go.GetType(), "GetComponent", new[] { typeof(Type) });
                if (getComp == null)
                    return false;

                var collider = getComp.Invoke(go, new object[] { colliderType });
                if (collider == null)
                    return false;

                var boundsProp = GetPropertyCached(collider.GetType(), "bounds");
                var bounds = boundsProp?.GetValueCompat(collider);
                if (bounds == null)
                    return false;

                var bt = bounds.GetType();
                var centerProp = GetPropertyCached(bt, "center");
                var sizeProp = GetPropertyCached(bt, "size");
                center = FormatVector3(centerProp?.GetValueCompat(bounds));
                size = FormatVector3(sizeProp?.GetValueCompat(bounds));
                return !IsNullOrWhiteSpace(center) && !IsNullOrWhiteSpace(size);
            }
            catch
            {
                return false;
            }
        }

        private static string GetMeshInfo(string[] parts)
        {
            try
            {
                object bakedMesh = null;
                try
                {
                    var idText = parts[1];
                    var maxVerts = 2000;
                    var maxIndices = 12000;
                    if (parts.Length > 2 && int.TryParse(parts[2], out var mv) && mv > 0)
                        maxVerts = Math.Min(mv, 20000);
                    if (parts.Length > 3 && int.TryParse(parts[3], out var mi) && mi > 0)
                        maxIndices = Math.Min(mi, 120000);

                    var obj = ResolveTrackedObject(idText);
                    if (obj == null)
                        return $"ERROR|Instance not found: {idText}";

                    var go = TryGetGameObject(obj) ?? obj;
                    var meshFilterType = FindUnityType("UnityEngine.MeshFilter");
                    var skinnedType = FindUnityType("UnityEngine.SkinnedMeshRenderer");
                    if (meshFilterType == null && skinnedType == null)
                        return "ERROR|No mesh component types found";

                    var getComp = GetMethodCached(go.GetType(), "GetComponent", new[] { typeof(Type) });
                    if (getComp == null)
                        return "ERROR|GetComponent(Type) not found";

                    object meshOwner = null;
                    if (meshFilterType != null)
                    {
                        meshOwner = getComp.Invoke(go, new object[] { meshFilterType });
                    }
                    if (meshOwner == null && skinnedType != null)
                    {
                        meshOwner = getComp.Invoke(go, new object[] { skinnedType });
                    }
                    if (meshOwner == null)
                        return $"MESH|id={idText}|vcount=0|icount=0";

                    var ownerType = meshOwner.GetType();
                    var meshProp = GetPropertyCached(ownerType, "sharedMesh") ?? GetPropertyCached(ownerType, "mesh");
                    var mesh = meshProp?.GetValueCompat(meshOwner);
                    if (mesh == null)
                        return $"MESH|id={idText}|vcount=0|icount=0";

                    var isSkinned = skinnedType != null && (skinnedType.IsAssignableFrom(ownerType) ||
                                                            ownerType.FullName.IndexOf("SkinnedMeshRenderer", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (isSkinned)
                    {
                        var meshType = FindUnityType("UnityEngine.Mesh");
                        if (meshType != null)
                        {
                            object temp = null;
                            try { temp = Activator.CreateInstance(meshType); } catch { temp = null; }
                            if (temp != null)
                            {
                                var methods = ownerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                for (var i = 0; i < methods.Length; i++)
                                {
                                    var m = methods[i];
                                    if (m == null || !string.Equals(m.Name, "BakeMesh", StringComparison.Ordinal))
                                        continue;

                                    var ps = m.GetParameters();
                                    if (ps == null || ps.Length < 1 || ps.Length > 2)
                                        continue;
                                    if (!ps[0].ParameterType.IsAssignableFrom(meshType) && ps[0].ParameterType != meshType)
                                        continue;

                                    try
                                    {
                                        if (ps.Length == 1)
                                        {
                                            m.Invoke(meshOwner, new[] { temp });
                                            bakedMesh = temp;
                                            mesh = temp;
                                            break;
                                        }

                                        if (ps.Length == 2 && ps[1].ParameterType == typeof(bool))
                                        {
                                            m.Invoke(meshOwner, new object[] { temp, true });
                                            bakedMesh = temp;
                                            mesh = temp;
                                            break;
                                        }
                                    }
                                    catch
                                    {
                                    }
                                }
                            }
                        }
                    }

                    var mt = mesh.GetType();
                    var verticesProp = GetPropertyCached(mt, "vertices");
                    var trisProp = GetPropertyCached(mt, "triangles");
                    if (verticesProp == null || trisProp == null)
                        return $"ERROR|Mesh data unavailable";

                    var verticesArr = CoerceToSystemArray(verticesProp.GetValueCompat(mesh)) ?? Array.CreateInstance(typeof(object), 0);
                    var indicesArr = CoerceToSystemArray(trisProp.GetValueCompat(mesh)) ?? Array.CreateInstance(typeof(object), 0);

                    var vcount = verticesArr.Length;
                    var icount = indicesArr.Length;
                    if (vcount > maxVerts || icount > maxIndices)
                    {
                        return $"MESH|id={idText}|tooLarge=1|vcount={vcount}|icount={icount}";
                    }

                    var verts = new float[vcount * 3];
                    for (var i = 0; i < vcount; i++)
                    {
                        var v = verticesArr.GetValue(i);
                        if (!TryReadVector3(v, out var x, out var y, out var z))
                            continue;
                        var o = i * 3;
                        verts[o] = x;
                        verts[o + 1] = y;
                        verts[o + 2] = z;
                    }

                    var indices = new int[icount];
                    for (var i = 0; i < icount; i++)
                    {
                        var raw = indicesArr.GetValue(i);
                        if (raw == null)
                            continue;
                        indices[i] = Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
                    }

                    var vb = new byte[verts.Length * 4];
                    Buffer.BlockCopy(verts, 0, vb, 0, vb.Length);
                    var ib = new byte[indices.Length * 4];
                    Buffer.BlockCopy(indices, 0, ib, 0, ib.Length);

                    var vb64 = Convert.ToBase64String(vb);
                    var ib64 = Convert.ToBase64String(ib);

                    return $"MESH|id={idText}|vcount={vcount}|icount={icount}|vb64={vb64}|ib64={ib64}";
                }
                finally
                {
                    if (bakedMesh != null)
                    {
                        try
                        {
                            var unityObjectType = FindUnityType("UnityEngine.Object");
                            var destroy = unityObjectType?.GetMethod("Destroy", BindingFlags.Public | BindingFlags.Static, null, new[] { unityObjectType }, null);
                            destroy?.Invoke(null, new[] { bakedMesh });
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static string GetSceneInfo()
        {
            try
            {
                var sceneManager = FindUnityType("UnityEngine.SceneManagement.SceneManager");
                if (sceneManager == null)
                    return "ERROR|SceneManager not found";

                var getActive = sceneManager.GetMethod("GetActiveScene", BindingFlags.Public | BindingFlags.Static);
                if (getActive == null)
                    return "ERROR|GetActiveScene not found";

                var scene = getActive.Invoke(null, null);
                if (scene == null)
                    return "ERROR|Active scene unavailable";

                var sceneType = scene.GetType();
                var nameProp = sceneType.GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
                var buildIndexProp = sceneType.GetProperty("buildIndex", BindingFlags.Public | BindingFlags.Instance);
                var isLoadedProp = sceneType.GetProperty("isLoaded", BindingFlags.Public | BindingFlags.Instance);

                var activeName = nameProp?.GetValueCompat(scene)?.ToString() ?? "Unknown";
                var activeBuild = buildIndexProp != null ? (int)buildIndexProp.GetValueCompat(scene) : -1;
                var activeLoaded = isLoadedProp != null ? (bool)isLoadedProp.GetValueCompat(scene) : true;

                var countProp = sceneManager.GetProperty("sceneCount", BindingFlags.Public | BindingFlags.Static);
                var sceneCount = countProp != null ? (int)countProp.GetValueCompat(null) : 1;

                var getSceneAt = sceneManager.GetMethod("GetSceneAt", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);

                var sb = new StringBuilder();
                sb.Append("SCENE");
                sb.Append("|active=");
                sb.Append(activeName.Replace("|", " "));
                sb.Append("|activeBuildIndex=");
                sb.Append(activeBuild);
                sb.Append("|activeLoaded=");
                sb.Append(activeLoaded ? "1" : "0");
                sb.Append("|sceneCount=");
                sb.Append(sceneCount);

                if (getSceneAt != null)
                {
                    var names = new List<string>();
                    for (var i = 0; i < sceneCount; i++)
                    {
                        try
                        {
                            var s = getSceneAt.Invoke(null, new object[] { i });
                            if (s == null)
                                continue;
                            var n = nameProp?.GetValueCompat(s)?.ToString() ?? $"Scene{i}";
                            names.Add(n);
                        }
                        catch
                        {
                        }
                    }
                    if (names.Count > 0)
                    {
                        sb.Append("|scenes=");
                        sb.Append(string.Join(",", names.Take(20).Select(n => n.Replace("|", " ")).ToArray()));
                    }
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static string GetActiveObjectsInfo()
        {
            try
            {
                var gameObjectType = FindUnityType("UnityEngine.GameObject");
                if (gameObjectType == null)
                    return "ERROR|GameObject not found";

                var all = FindUnityObjectsOfType(gameObjectType);
                if (all == null)
                    return "ERROR|Unity object enumeration unavailable";

                var activeCount = 0;
                for (var i = 0; i < all.Length; i++)
                {
                    var go = all.GetValue(i);
                    if (go == null)
                        continue;
                    if (TryGetActiveInHierarchy(go, out var active) && active)
                        activeCount++;
                }

                return $"ACTIVE_OBJECTS|total={all.Length}|active={activeCount}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string GetSceneRootsInfo(string sceneName)
        {
            try
            {
                var sceneManager = FindUnityType("UnityEngine.SceneManagement.SceneManager");
                if (sceneManager == null)
                    return "ERROR|SceneManager not found";

                object scene;
                if (IsNullOrWhiteSpace(sceneName))
                {
                    var getActive = sceneManager.GetMethod("GetActiveScene", BindingFlags.Public | BindingFlags.Static);
                    if (getActive == null)
                        return "ERROR|GetActiveScene not found";
                    scene = getActive.Invoke(null, null);
                }
                else
                {
                    var getByName = sceneManager.GetMethod("GetSceneByName", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                    if (getByName == null)
                        return "ERROR|GetSceneByName not found";
                    scene = getByName.Invoke(null, new object[] { sceneName });
                }

                if (scene == null)
                    return "ERROR|Scene unavailable";

                var sceneType = scene.GetType();
                var nameProp = sceneType.GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
                var isLoadedProp = sceneType.GetProperty("isLoaded", BindingFlags.Public | BindingFlags.Instance);
                var name = nameProp?.GetValueCompat(scene)?.ToString() ?? (sceneName ?? "Unknown");
                var loaded = isLoadedProp != null ? (bool)isLoadedProp.GetValueCompat(scene) : true;
                if (!loaded)
                    return $"ROOTS|scene={name.Replace("|", " ")}|count=0";

                Array roots = null;
                var candidates = sceneType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => string.Equals(m.Name, "GetRootGameObjects", StringComparison.Ordinal))
                    .ToArray();

                for (var i = 0; i < candidates.Length && roots == null; i++)
                {
                    var m = candidates[i];
                    object result = null;
                    try
                    {
                        var ps = m.GetParameters();
                        if (ps.Length == 0)
                        {
                            result = m.Invoke(scene, null);
                            roots = CoerceToSystemArray(result);
                            continue;
                        }

                        if (ps.Length == 1)
                        {
                            var listType = ps[0].ParameterType;
                            object listInstance = null;
                            try { listInstance = Activator.CreateInstance(listType); } catch { }
                            if (listInstance == null)
                                continue;

                            result = m.Invoke(scene, new[] { listInstance });

                            roots = CoerceToSystemArray(result);
                            if (roots != null)
                                continue;

                            var toArray = GetMethodCached(listType, "ToArray", Type.EmptyTypes);
                            if (toArray != null)
                                roots = CoerceToSystemArray(toArray.Invoke(listInstance, null));

                            if (roots == null)
                                roots = CoerceToSystemArray(listInstance);
                        }
                    }
                    catch
                    {
                    }
                }

                if (roots == null)
                    roots = Array.CreateInstance(typeof(object), 0);

                var sb = new StringBuilder();
                sb.Append("ROOTS|scene=");
                sb.Append(name.Replace("|", " "));
                sb.Append("|count=");
                sb.Append(roots.Length);

                var limit = Math.Min(roots.Length, 500);
                for (var i = 0; i < limit; i++)
                {
                    var go = roots.GetValue(i);
                    if (go == null)
                        continue;

                    var trackedId = TrackObject(go);
                    var goName = GetUnityObjectName(go) ?? "";
                    var active = TryGetActiveInHierarchy(go, out var isActive) ? (isActive ? "1" : "0") : "";
                    var childCount = TryGetTransformChildCount(go, out var cc) ? cc.ToString() : "";

                    sb.Append("|id=");
                    sb.Append(trackedId);
                    sb.Append(";name=");
                    sb.Append(goName.Replace("|", " ").Replace(";", " "));
                    if (!IsNullOrWhiteSpace(active))
                    {
                        sb.Append(";active=");
                        sb.Append(active);
                    }
                    if (!IsNullOrWhiteSpace(childCount))
                    {
                        sb.Append(";children=");
                        sb.Append(childCount);
                    }
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string GetChildrenInfo(string instanceId)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                if (!TryGetTransform(obj, out var transform))
                    return "ERROR|Transform not available";

                if (!TryGetTransformChildCount(transform, out var childCount))
                    childCount = 0;

                var sb = new StringBuilder();
                sb.Append("CHILDREN|id=");
                sb.Append(instanceId);
                sb.Append("|count=");
                sb.Append(childCount);

                var limit = Math.Min(childCount, 500);
                for (var i = 0; i < limit; i++)
                {
                    var child = TryGetTransformChild(transform, i);
                    if (child == null)
                        continue;

                    var childGo = TryGetTransformGameObject(child) ?? child;
                    var trackedId = TrackObject(childGo);
                    var name = GetUnityObjectName(childGo) ?? "";
                    var active = TryGetActiveInHierarchy(childGo, out var isActive) ? (isActive ? "1" : "0") : "";
                    var grandChildren = TryGetTransformChildCount(childGo, out var gc) ? gc.ToString() : "";

                    sb.Append("|id=");
                    sb.Append(trackedId);
                    sb.Append(";name=");
                    sb.Append(name.Replace("|", " ").Replace(";", " "));
                    if (!IsNullOrWhiteSpace(active))
                    {
                        sb.Append(";active=");
                        sb.Append(active);
                    }
                    if (!IsNullOrWhiteSpace(grandChildren))
                    {
                        sb.Append(";children=");
                        sb.Append(grandChildren);
                    }
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string GetTransformInfo(string instanceId)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                if (!TryGetTransform(obj, out var transform) || transform == null)
                    return "ERROR|Transform not available";

                var t = transform.GetType();
                var localPos = GetPropertyCached(t, "localPosition")?.GetValueCompat(transform);
                var localRot = GetPropertyCached(t, "localEulerAngles")?.GetValueCompat(transform);
                var localScale = GetPropertyCached(t, "localScale")?.GetValueCompat(transform);
                var worldPos = GetPropertyCached(t, "position")?.GetValueCompat(transform);
                var worldRot = GetPropertyCached(t, "eulerAngles")?.GetValueCompat(transform);

                var sb = new StringBuilder();
                sb.Append("TRANSFORM|id=");
                sb.Append(instanceId);
                sb.Append("|pos=");
                sb.Append(FormatVector3(worldPos));
                sb.Append("|rot=");
                sb.Append(FormatVector3(worldRot));
                sb.Append("|localPos=");
                sb.Append(FormatVector3(localPos));
                sb.Append("|localRot=");
                sb.Append(FormatVector3(localRot));
                sb.Append("|localScale=");
                sb.Append(FormatVector3(localScale));
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetTransformLocal(string instanceId, string localPos, string localRot, string localScale)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                if (!TryGetTransform(obj, out var transform) || transform == null)
                    return "ERROR|Transform not available";

                var vector3Type = FindUnityType("UnityEngine.Vector3");
                if (vector3Type == null)
                    return "ERROR|Vector3 not found";

                var t = transform.GetType();

                if (!IsNullOrWhiteSpace(localPos) && !localPos.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    var prop = GetPropertyCached(t, "localPosition");
                    if (prop != null && prop.CanWrite)
                    {
                        var v = ConvertStringToType(localPos, vector3Type);
                        prop.SetValueCompat(transform, v);
                    }
                }

                if (!IsNullOrWhiteSpace(localRot) && !localRot.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    var prop = GetPropertyCached(t, "localEulerAngles");
                    if (prop != null && prop.CanWrite)
                    {
                        var v = ConvertStringToType(localRot, vector3Type);
                        prop.SetValueCompat(transform, v);
                    }
                }

                if (!IsNullOrWhiteSpace(localScale) && !localScale.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    var prop = GetPropertyCached(t, "localScale");
                    if (prop != null && prop.CanWrite)
                    {
                        var v = ConvertStringToType(localScale, vector3Type);
                        prop.SetValueCompat(transform, v);
                    }
                }

                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetTransformWorld(string instanceId, string worldPos, string worldRot, string localScale)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                if (!TryGetTransform(obj, out var transform) || transform == null)
                    return "ERROR|Transform not available";

                var vector3Type = FindUnityType("UnityEngine.Vector3");
                if (vector3Type == null)
                    return "ERROR|Vector3 not found";

                var t = transform.GetType();

                if (!IsNullOrWhiteSpace(worldPos) && !worldPos.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    var prop = GetPropertyCached(t, "position");
                    if (prop != null && prop.CanWrite)
                    {
                        var v = ConvertStringToType(worldPos, vector3Type);
                        prop.SetValueCompat(transform, v);
                    }
                }

                if (!IsNullOrWhiteSpace(worldRot) && !worldRot.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    var prop = GetPropertyCached(t, "eulerAngles");
                    if (prop != null && prop.CanWrite)
                    {
                        var v = ConvertStringToType(worldRot, vector3Type);
                        prop.SetValueCompat(transform, v);
                    }
                }

                if (!IsNullOrWhiteSpace(localScale) && !localScale.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    var prop = GetPropertyCached(t, "localScale");
                    if (prop != null && prop.CanWrite)
                    {
                        var v = ConvertStringToType(localScale, vector3Type);
                        prop.SetValueCompat(transform, v);
                    }
                }

                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetObjectActive(string instanceId, string active)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var go = TryGetGameObject(obj) ?? obj;
                var goType = go.GetType();

                var setActive = GetMethodCached(goType, "SetActive", new[] { typeof(bool) });
                if (setActive == null)
                    return "ERROR|SetActive not found";

                var value = active == "1" || active.Equals("true", StringComparison.OrdinalIgnoreCase);
                setActive.Invoke(go, new object[] { value });
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetObjectName(string instanceId, string name)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var go = TryGetGameObject(obj) ?? obj;
                var prop = GetPropertyCached(go.GetType(), "name");
                if (prop == null || !prop.CanWrite)
                    return "ERROR|Name not writable";

                prop.SetValueCompat(go, name ?? string.Empty);
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetObjectTag(string instanceId, string tag)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var go = TryGetGameObject(obj);
                if (go == null)
                    return "ERROR|GameObject not found";

                var prop = GetPropertyCached(go.GetType(), "tag");
                if (prop == null || !prop.CanWrite)
                    return "ERROR|Tag not writable";

                prop.SetValueCompat(go, tag ?? string.Empty);
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetObjectLayer(string instanceId, string layer)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var go = TryGetGameObject(obj);
                if (go == null)
                    return "ERROR|GameObject not found";

                if (!int.TryParse(layer ?? "", out var layerValue))
                    return "ERROR|Layer must be an integer";

                var prop = GetPropertyCached(go.GetType(), "layer");
                if (prop == null || !prop.CanWrite)
                    return "ERROR|Layer not writable";

                prop.SetValueCompat(go, layerValue);
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string GetEnabledInfo(string instanceId)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var prop = GetPropertyCached(obj.GetType(), "enabled");
                if (prop == null || !prop.CanRead)
                    return "ERROR|Enabled not readable";

                var v = prop.GetValueCompat(obj);
                var enabled = false;
                if (v != null)
                {
                    try
                    {
                        enabled = Convert.ToBoolean(v, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        enabled = false;
                    }
                }

                return $"ENABLED|id={instanceId}|value={(enabled ? "1" : "0")}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetEnabled(string instanceId, string enabledValue)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var prop = GetPropertyCached(obj.GetType(), "enabled");
                if (prop == null || !prop.CanWrite)
                    return "ERROR|Enabled not writable";

                var enabled = enabledValue == "1" || enabledValue.Equals("true", StringComparison.OrdinalIgnoreCase);
                prop.SetValueCompat(obj, enabled);
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DestroyObject(string instanceId)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var unityObjectType = FindUnityType("UnityEngine.Object");
                if (unityObjectType == null)
                    return "ERROR|UnityEngine.Object not found";

                if (!unityObjectType.IsInstanceOfType(obj))
                {
                    var go = TryGetGameObject(obj);
                    if (go != null && unityObjectType.IsInstanceOfType(go))
                        obj = go;
                }

                if (!unityObjectType.IsInstanceOfType(obj))
                    return "ERROR|Not a UnityEngine.Object";

                var destroy = GetMethodCached(unityObjectType, "Destroy", new[] { unityObjectType });
                if (destroy == null)
                    return "ERROR|Destroy not found";

                destroy.Invoke(null, new[] { obj });
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DespawnObject(string instanceId)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var go = TryGetGameObject(obj);
                var unityObj = go ?? obj;

                var addressables = FindUnityType("UnityEngine.AddressableAssets.Addressables");
                if (addressables != null && go != null)
                {
                    try
                    {
                        var release = addressables.GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .FirstOrDefault(m => m.Name == "ReleaseInstance" && m.GetParameters().Length == 1);
                        if (release != null)
                        {
                            var pt = release.GetParameters()[0].ParameterType;
                            if (pt.IsInstanceOfType(go))
                            {
                                release.Invoke(null, new[] { go });
                                return "SUCCESS|RELEASE_INSTANCE";
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                var leanPool = FindUnityType("Lean.Pool.LeanPool");
                if (leanPool != null)
                {
                    try
                    {
                        if (go != null)
                        {
                            var m = leanPool.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                .FirstOrDefault(mi => mi.Name == "Despawn" && mi.GetParameters().Length == 1 && mi.GetParameters()[0].ParameterType.IsInstanceOfType(go));
                            if (m != null)
                            {
                                m.Invoke(null, new[] { go });
                                return "SUCCESS|LEAN_DESPAWN";
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                try
                {
                    var t = unityObj.GetType();
                    var m = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .FirstOrDefault(mi => (string.Equals(mi.Name, "Despawn", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(mi.Name, "ReturnToPool", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(mi.Name, "Release", StringComparison.OrdinalIgnoreCase)) &&
                                             mi.GetParameters().Length == 0);
                    if (m != null)
                    {
                        m.Invoke(unityObj, null);
                        return "SUCCESS|OK";
                    }
                }
                catch
                {
                }

                return DestroyObject(instanceId);
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DuplicateObject(string instanceId)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var go = TryGetGameObject(obj);
                if (go == null)
                    return "ERROR|GameObject not found";

                var unityObjectType = FindUnityType("UnityEngine.Object");
                if (unityObjectType == null)
                    return "ERROR|UnityEngine.Object not found";

                if (!unityObjectType.IsInstanceOfType(go))
                    return "ERROR|Not a UnityEngine.Object";

                var instantiate = GetMethodCached(unityObjectType, "Instantiate", new[] { unityObjectType });
                if (instantiate == null)
                    return "ERROR|Instantiate not found";

                var clone = instantiate.Invoke(null, new[] { go });
                if (clone == null)
                    return "ERROR|Instantiate returned null";

                var id = TrackObject(clone);
                return $"SUCCESS|id={id}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string AddComponentToObject(string instanceId, string componentTypeName)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var go = TryGetGameObject(obj);
                if (go == null)
                    return "ERROR|GameObject not found";

                var componentBase = FindUnityType("UnityEngine.Component");
                if (componentBase == null)
                    return "ERROR|Component base type not found";

                var componentType = ResolveTypeByName(componentTypeName);
                if (componentType == null)
                    return $"ERROR|Type not found: {componentTypeName}";

                if (!componentBase.IsAssignableFrom(componentType))
                    return "ERROR|Type is not a Component";

                var add = GetMethodCached(go.GetType(), "AddComponent", new[] { typeof(Type) });
                if (add == null)
                    return "ERROR|AddComponent(Type) not found";

                var comp = add.Invoke(go, new object[] { componentType });
                if (comp == null)
                    return "ERROR|AddComponent returned null";

                var id = TrackObject(comp);
                return $"SUCCESS|id={id}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string GetHierarchyPath(string instanceId)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var go = TryGetGameObject(obj) ?? obj;
                if (go == null)
                    return "ERROR|GameObject not found";

                if (!TryGetTransform(go, out var transform) || transform == null)
                    return "ERROR|Transform not available";

                var transformType = transform.GetType();
                var parentProp = GetPropertyCached(transformType, "parent");
                var goProp = GetPropertyCached(transformType, "gameObject");

                var ids = new List<string>();
                var names = new List<string>();

                object current = transform;
                while (current != null)
                {
                    object currentGo = null;
                    try
                    {
                        currentGo = goProp?.GetValueCompat(current) ?? TryGetTransformGameObject(current);
                    }
                    catch
                    {
                    }

                    if (currentGo != null)
                    {
                        var id = TrackObject(currentGo).ToString();
                        var n = (GetUnityObjectName(currentGo) ?? "").Replace("|", " ").Replace(";", " ").Replace(",", " ").Replace("/", " ");
                        ids.Add(id);
                        names.Add(n);
                    }

                    try
                    {
                        current = parentProp?.GetValueCompat(current);
                    }
                    catch
                    {
                        current = null;
                    }
                }

                ids.Reverse();
                names.Reverse();

                return $"PATH|ids={string.Join(",", ids.ToArray())}|names={string.Join("/", names.ToArray())}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string GetNodeInfo(string instanceId)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var go = TryGetGameObject(obj) ?? obj;
                if (!TryGetTransform(go, out var transform) || transform == null)
                    return "ERROR|Transform not available";

                var t = transform.GetType();
                var parentProp = GetPropertyCached(t, "parent");
                var siblingIndexMethod = GetMethodCached(t, "GetSiblingIndex", Type.EmptyTypes);
                var childCountProp = GetPropertyCached(t, "childCount");

                string parentId = "";
                try
                {
                    var parent = parentProp?.GetValueCompat(transform);
                    var parentGo = parent != null ? TryGetTransformGameObject(parent) : null;
                    if (parentGo != null)
                        parentId = TrackObject(parentGo).ToString();
                }
                catch
                {
                }

                var siblingIndex = -1;
                try
                {
                    var v = siblingIndexMethod?.Invoke(transform, null);
                    if (v != null)
                        siblingIndex = Convert.ToInt32(v);
                }
                catch
                {
                }

                var childCount = 0;
                try
                {
                    var v = childCountProp?.GetValueCompat(transform);
                    if (v != null)
                        childCount = Convert.ToInt32(v);
                }
                catch
                {
                }

                return $"NODE|id={instanceId}|parentId={parentId}|siblingIndex={siblingIndex}|childCount={childCount}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetParent(string childId, string parentId)
        {
            try
            {
                var childObj = ResolveTrackedObject(childId);
                if (childObj == null)
                    return $"ERROR|Instance not found: {childId}";

                var childGo = TryGetGameObject(childObj) ?? childObj;
                if (!TryGetTransform(childGo, out var childTransform) || childTransform == null)
                    return "ERROR|Child transform not available";

                object parentTransform = null;
                if (!IsNullOrWhiteSpace(parentId) && !parentId.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    var parentObj = ResolveTrackedObject(parentId);
                    if (parentObj == null)
                        return $"ERROR|Parent instance not found: {parentId}";

                    var parentGo = TryGetGameObject(parentObj) ?? parentObj;
                    if (!TryGetTransform(parentGo, out parentTransform) || parentTransform == null)
                        return "ERROR|Parent transform not available";
                }

                var t = childTransform.GetType();
                var setParent = GetMethodCached(t, "SetParent", new[] { t, typeof(bool) });
                if (setParent != null)
                {
                    setParent.Invoke(childTransform, new object[] { parentTransform, true });
                    return "SUCCESS|OK";
                }

                var setParentSimple = GetMethodCached(t, "SetParent", new[] { t });
                if (setParentSimple != null)
                {
                    setParentSimple.Invoke(childTransform, new object[] { parentTransform });
                    return "SUCCESS|OK";
                }

                return "ERROR|SetParent not found";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetSiblingIndex(string instanceId, string indexText)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var go = TryGetGameObject(obj) ?? obj;
                if (!TryGetTransform(go, out var transform) || transform == null)
                    return "ERROR|Transform not available";

                if (!int.TryParse(indexText ?? "", out var index))
                    return "ERROR|Index must be an integer";

                var t = transform.GetType();
                var method = GetMethodCached(t, "SetSiblingIndex", new[] { typeof(int) });
                if (method == null)
                    return "ERROR|SetSiblingIndex not found";

                method.Invoke(transform, new object[] { index });
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string CreateGameObject(string name, string parentId)
        {
            try
            {
                var gameObjectType = FindUnityType("UnityEngine.GameObject");
                if (gameObjectType == null)
                    return "ERROR|GameObject type not found";

                object go = null;
                try
                {
                    var ctor = gameObjectType.GetConstructor(new[] { typeof(string) });
                    if (ctor != null)
                        go = ctor.Invoke(new object[] { name ?? "New GameObject" });
                }
                catch
                {
                }

                if (go == null)
                {
                    try
                    {
                        go = Activator.CreateInstance(gameObjectType);
                        var prop = GetPropertyCached(gameObjectType, "name");
                        if (prop != null && prop.CanWrite)
                            prop.SetValueCompat(go, name ?? "New GameObject");
                    }
                    catch
                    {
                    }
                }

                if (go == null)
                    return "ERROR|Failed to create GameObject";

                if (!IsNullOrWhiteSpace(parentId) && !parentId.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    var parentObj = ResolveTrackedObject(parentId);
                    if (parentObj != null)
                    {
                        var parentGo = TryGetGameObject(parentObj) ?? parentObj;
                        if (TryGetTransform(parentGo, out var parentTransform) && parentTransform != null)
                        {
                            if (TryGetTransform(go, out var childTransform) && childTransform != null)
                            {
                                var t = childTransform.GetType();
                                var setParent = GetMethodCached(t, "SetParent", new[] { t, typeof(bool) });
                                if (setParent != null)
                                {
                                    setParent.Invoke(childTransform, new object[] { parentTransform, false });
                                }
                            }
                        }
                    }
                }

                var id = TrackObject(go);
                return $"SUCCESS|id={id}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string FormatVector3(object value)
        {
            if (value == null)
                return "";
            try
            {
                if (TryReadVector3(value, out var x, out var y, out var z))
                    return x.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                           y.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                           z.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
            }

            return "";
        }

        private static bool TryReadVector3(object value, out float x, out float y, out float z)
        {
            x = y = z = 0f;
            if (value == null)
                return false;
            try
            {
                var t = value.GetType();
                var px = GetPropertyCached(t, "x");
                var py = GetPropertyCached(t, "y");
                var pz = GetPropertyCached(t, "z");
                if (px == null || py == null || pz == null)
                    return false;
                var vx = px.GetValueCompat(value);
                var vy = py.GetValueCompat(value);
                var vz = pz.GetValueCompat(value);
                if (vx == null || vy == null || vz == null)
                    return false;
                x = Convert.ToSingle(vx, System.Globalization.CultureInfo.InvariantCulture);
                y = Convert.ToSingle(vy, System.Globalization.CultureInfo.InvariantCulture);
                z = Convert.ToSingle(vz, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object TryGetGameObject(object obj)
        {
            if (obj == null)
                return null;
            try
            {
                var t = obj.GetType();
                if (t.FullName == "UnityEngine.GameObject")
                    return obj;

                var gameObjectProp = GetPropertyCached(t, "gameObject");
                return gameObjectProp?.GetValueCompat(obj);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetTransform(object obj, out object transform)
        {
            transform = null;
            if (obj == null)
                return false;
            try
            {
                var t = obj.GetType();
                var prop = GetPropertyCached(t, "transform");
                if (prop != null)
                {
                    transform = prop.GetValueCompat(obj);
                    return transform != null;
                }

                var go = TryGetGameObject(obj);
                if (go != null && !ReferenceEquals(go, obj))
                    return TryGetTransform(go, out transform);
            }
            catch
            {
            }
            return false;
        }

        private static bool TryGetTransformChildCount(object objOrGo, out int count)
        {
            count = 0;
            if (objOrGo == null)
                return false;
            try
            {
                if (!TryGetTransform(objOrGo, out var transform))
                    transform = objOrGo;

                var t = transform.GetType();
                var prop = GetPropertyCached(t, "childCount");
                if (prop == null)
                    return false;
                var v = prop.GetValueCompat(transform);
                if (v == null)
                    return false;
                count = Convert.ToInt32(v);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object TryGetTransformChild(object transform, int index)
        {
            if (transform == null)
                return null;
            try
            {
                var t = transform.GetType();
                var mi = GetMethodCached(t, "GetChild", new[] { typeof(int) });
                if (mi == null)
                    return null;
                return mi.Invoke(transform, new object[] { index });
            }
            catch
            {
                return null;
            }
        }

        private static object TryGetTransformGameObject(object transform)
        {
            if (transform == null)
                return null;
            try
            {
                var t = transform.GetType();
                var prop = GetPropertyCached(t, "gameObject");
                return prop?.GetValueCompat(transform);
            }
            catch
            {
                return null;
            }
        }

        private static string GetCapabilitiesInfo()
        {
            var caps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Add(string cmd, bool ok, string reasonIfNo)
            {
                caps[cmd] = ok ? "1" : $"0:{reasonIfNo}";
            }

            var hasSceneManager = FindUnityType("UnityEngine.SceneManagement.SceneManager") != null;
            var hasCamera = GetMainCamera() != null;
            var hasPlayer = GetCachedPlayerGameObject() != null;
            var hasInput = FindUnityType("UnityEngine.Input") != null;
            var hasColliderType = FindUnityType("UnityEngine.Collider") != null;
            var hasObjectFind =
                (FindUnityType("UnityEngine.Object")?.GetMethod("FindObjectsOfType", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Type) }, null) != null) ||
                (FindUnityType("UnityEngine.Resources")?.GetMethod("FindObjectsOfTypeAll", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Type) }, null) != null);
            var playerLikelyDiscoverable = hasPlayer || hasObjectFind;
            var wireframeSupported = FindUnityType("UnityEngine.GL")?.GetProperty("wireframe", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.CanWrite == true;

            Add("PAUSE_GAME", true, "");
            Add("RESUME_GAME", true, "");
            Add("SET_TIMESCALE", FindUnityType("UnityEngine.Time") != null, "NoUnityTime");
            Add("FREEZE_TIME", FindUnityType("UnityEngine.Time") != null, "NoUnityTime");
            Add("SET_DAYTIME", FindUnityType("UnityEngine.Light") != null, "NoUnityLight");
            Add("SET_NIGHTTIME", FindUnityType("UnityEngine.Light") != null, "NoUnityLight");
            Add("TOGGLE_FPS_DISPLAY", true, "");
            Add("TOGGLE_COLLIDERS", hasColliderType, "NoColliderType");
            Add("TOGGLE_WIREFRAME", wireframeSupported, "WireframeUnsupported");
            Add("RELOAD_SCENE", hasSceneManager, "NoSceneManager");
            Add("CRASH_GAME", true, "");
            Add("OPEN_DEBUG_CONSOLE", false, "NotSupported");

            Add("SKIP_LEVEL", hasSceneManager, "NoSceneManager");
            Add("RESTART_LEVEL", hasSceneManager, "NoSceneManager");

            Add("FREE_CAMERA", hasCamera && hasInput, hasCamera ? "NoInput" : "NoCamera");
            Add("TOGGLE_NOCLIP", playerLikelyDiscoverable, "PlayerNotFound");
            Add("ZOOM_OUT", hasCamera, "NoCamera");
            Add("FIRST_PERSON", hasCamera && playerLikelyDiscoverable, hasCamera ? "PlayerNotFound" : "NoCamera");
            Add("THIRD_PERSON", hasCamera && playerLikelyDiscoverable, hasCamera ? "PlayerNotFound" : "NoCamera");
            Add("RESET_CAMERA", hasCamera, "NoCamera");

            Add("TOGGLE_GODMODE", _healthMember != null && _healthMember.IsValid, "HealthNotFound");
            Add("TOGGLE_INFINITE_HEALTH", _healthMember != null && _healthMember.IsValid, "HealthNotFound");
            Add("ADD_HEALTH", _healthMember != null && _healthMember.IsValid, "HealthNotFound");

            Add("TOGGLE_INFINITE_AMMO", _ammoMember != null && _ammoMember.IsValid, "AmmoNotFound");
            Add("TOGGLE_INFINITE_STAMINA", _staminaMember != null && _staminaMember.IsValid, "StaminaNotFound");

            Add("ADD_MONEY", _moneyMember != null && _moneyMember.IsValid, "MoneyNotFound");
            Add("ADD_XP", _xpMember != null && _xpMember.IsValid, "XpNotFound");
            Add("ADD_STAMINA", _staminaMember != null && _staminaMember.IsValid, "StaminaNotFound");

            Add("UNLOCK_ALL", false, "NotImplemented");
            Add("SPAWN_ENTITY", FindUnityType("UnityEngine.GameObject") != null, "NoGameObjectType");

            var sb = new StringBuilder();
            sb.Append("CAPS");
            foreach (var kvp in caps.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append('|');
                sb.Append(kvp.Key);
                sb.Append('=');
                sb.Append(kvp.Value);
            }

            return sb.ToString();
        }

        private static string GetBindingsInfo()
        {
            var player = GetCachedPlayerGameObject();
            var camera = GetMainCamera();

            var sb = new StringBuilder();
            sb.Append("BINDINGS");
            sb.Append("|player=");
            sb.Append(GetUnityObjectName(player) ?? "Unknown");
            sb.Append("|camera=");
            sb.Append(GetUnityObjectName(camera) ?? "Unknown");
            sb.Append("|health=");
            sb.Append(DescribeBinding(_healthMember));
            sb.Append("|ammo=");
            sb.Append(DescribeBinding(_ammoMember));
            sb.Append("|money=");
            sb.Append(DescribeBinding(_moneyMember));
            sb.Append("|xp=");
            sb.Append(DescribeBinding(_xpMember));
            sb.Append("|stamina=");
            sb.Append(DescribeBinding(_staminaMember));
            return sb.ToString();
        }

        private static string DescribeBinding(BoundNumericMember bound)
        {
            if (bound == null || !bound.IsValid)
                return "Not bound";

            var targetType = bound.Target.GetType().FullName ?? bound.Target.GetType().Name;
            var memberName = bound.Member.Name;
            var value = bound.LastKnownValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            return $"{targetType}.{memberName} (value={value})";
        }

        private static string GetKeyObjectsInfo()
        {
            try
            {
                var player = GetCachedPlayerGameObject();
                var camera = GetMainCamera();

                var sb = new StringBuilder();
                sb.Append("KEY_OBJECTS");
                AppendKeyObject(sb, "player", TryGetGameObject(player) ?? player);
                AppendKeyObject(sb, "camera", TryGetGameObject(camera) ?? camera);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static void AppendKeyObject(StringBuilder sb, string key, object obj)
        {
            if (sb == null || obj == null || IsNullOrWhiteSpace(key))
                return;

            var id = TrackObject(obj);
            sb.Append("|k=");
            sb.Append(SanitizePipeValue(key));
            sb.Append(";id=");
            sb.Append(id);
            sb.Append(";name=");
            sb.Append(SanitizePipeValue(GetUnityObjectName(obj) ?? ""));
            sb.Append(";type=");
            sb.Append(SanitizePipeValue(obj.GetType().FullName ?? obj.GetType().Name));
        }

        private static string GetComponentsInfo(string instanceId)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var go = TryGetGameObject(obj) ?? obj;
                var componentType = FindUnityType("UnityEngine.Component");
                if (componentType == null)
                    return "ERROR|Component type not found";

                var getComponents = GetMethodCached(go.GetType(), "GetComponents", new[] { typeof(Type) });
                if (getComponents == null)
                    return "ERROR|GetComponents(Type) not found";

                var components = CoerceToSystemArray(getComponents.Invoke(go, new object[] { componentType }));
                if (components == null)
                    components = Array.CreateInstance(typeof(object), 0);

                var sb = new StringBuilder();
                sb.Append("COMPONENTS");
                sb.Append("|id=");
                sb.Append(instanceId);
                sb.Append("|count=");
                sb.Append(components.Length);

                var emitted = 0;
                var limit = Math.Min(components.Length, 200);
                for (var i = 0; i < limit; i++)
                {
                    var c = components.GetValue(i);
                    if (c == null)
                        continue;

                    var trackedId = TrackObject(c);
                    var typeName = c.GetType().FullName ?? c.GetType().Name;

                    sb.Append("|id=");
                    sb.Append(trackedId);
                    sb.Append(";type=");
                    sb.Append(typeName.Replace("|", " ").Replace(";", " "));
                    emitted++;
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string GetNumericMembersInfo(string instanceId)
        {
            try
            {
                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var t = obj.GetType();
                var sb = new StringBuilder();
                sb.Append("NUMERIC_MEMBERS");
                sb.Append("|id=");
                sb.Append(instanceId);
                sb.Append("|type=");
                sb.Append(SanitizePipeValue(t.FullName ?? t.Name));

                var emitted = 0;
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                var fields = t.GetFields(flags);
                for (var i = 0; i < fields.Length; i++)
                {
                    var f = fields[i];
                    if (f == null || f.IsLiteral || f.IsInitOnly)
                        continue;
                    if (!TryGetNumericType(f.FieldType))
                        continue;

                    var val = "";
                    try
                    {
                        var raw = f.GetValue(obj);
                        if (TryConvertNumericToDouble(raw, out var d))
                            val = d.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                    }

                    sb.Append("|m=");
                    sb.Append(TrackObject(obj));
                    sb.Append(";kind=FIELD");
                    sb.Append(";name=");
                    sb.Append(SanitizePipeValue(f.Name));
                    sb.Append(";decl=");
                    sb.Append(SanitizePipeValue(f.DeclaringType?.FullName ?? f.DeclaringType?.Name ?? ""));
                    sb.Append(";type=");
                    sb.Append(SanitizePipeValue(f.FieldType.FullName ?? f.FieldType.Name));
                    if (!IsNullOrWhiteSpace(val))
                    {
                        sb.Append(";value=");
                        sb.Append(val);
                    }

                    emitted++;
                    if (emitted >= 800)
                        break;
                }

                if (emitted < 800)
                {
                    var props = t.GetProperties(flags);
                    for (var i = 0; i < props.Length; i++)
                    {
                        var p = props[i];
                        if (p == null)
                            continue;
                        if (!p.CanRead || !p.CanWrite)
                            continue;
                        if (p.GetIndexParameters() != null && p.GetIndexParameters().Length != 0)
                            continue;
                        if (!TryGetNumericType(p.PropertyType))
                            continue;

                        var val = "";
                        try
                        {
                            var raw = p.GetValueCompat(obj);
                            if (TryConvertNumericToDouble(raw, out var d))
                                val = d.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                        }
                        catch
                        {
                        }

                        sb.Append("|m=");
                        sb.Append(TrackObject(obj));
                        sb.Append(";kind=PROPERTY");
                        sb.Append(";name=");
                        sb.Append(SanitizePipeValue(p.Name));
                        sb.Append(";decl=");
                        sb.Append(SanitizePipeValue(p.DeclaringType?.FullName ?? p.DeclaringType?.Name ?? ""));
                        sb.Append(";type=");
                        sb.Append(SanitizePipeValue(p.PropertyType.FullName ?? p.PropertyType.Name));
                        if (!IsNullOrWhiteSpace(val))
                        {
                            sb.Append(";value=");
                            sb.Append(val);
                        }

                        emitted++;
                        if (emitted >= 800)
                            break;
                    }
                }

                sb.Append("|count=");
                sb.Append(emitted);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetNumericBinding(string slot, string instanceId, string declaringTypeName, string memberKind, string memberName)
        {
            try
            {
                if (IsNullOrWhiteSpace(slot))
                    return "ERROR|Missing slot";
                if (IsNullOrWhiteSpace(instanceId))
                    return "ERROR|Missing instance id";
                if (IsNullOrWhiteSpace(memberKind))
                    return "ERROR|Missing member kind";
                if (IsNullOrWhiteSpace(memberName))
                    return "ERROR|Missing member name";

                var obj = ResolveTrackedObject(instanceId);
                if (obj == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var bound = BuildNumericBinding(obj, declaringTypeName, memberKind, memberName);
                if (bound == null || !bound.IsValid)
                    return "ERROR|Member not found or not numeric";

                TryGetNumeric(bound, out _);

                var key = slot.Trim().ToLowerInvariant();
                if (key == "health")
                    _healthMember = bound;
                else if (key == "ammo")
                    _ammoMember = bound;
                else if (key == "money")
                    _moneyMember = bound;
                else if (key == "xp")
                    _xpMember = bound;
                else if (key == "stamina")
                    _staminaMember = bound;
                else
                    return $"ERROR|Unknown slot: {slot}";

                return $"SUCCESS|BOUND|slot={SanitizePipeValue(key)}|binding={SanitizePipeValue(DescribeBinding(bound))}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetNumericBindingByKey(string slot, string keyObjectKey, string componentTypeName, string declaringTypeName, string memberKind, string memberName)
        {
            try
            {
                if (IsNullOrWhiteSpace(slot))
                    return "ERROR|Missing slot";
                if (IsNullOrWhiteSpace(keyObjectKey))
                    return "ERROR|Missing key object key";
                if (IsNullOrWhiteSpace(memberKind))
                    return "ERROR|Missing member kind";
                if (IsNullOrWhiteSpace(memberName))
                    return "ERROR|Missing member name";

                object root = null;
                var k = keyObjectKey.Trim().ToLowerInvariant();
                if (k == "player")
                    root = GetCachedPlayerGameObject();
                else if (k == "camera")
                    root = GetMainCamera();
                else
                    return $"ERROR|Unknown key object: {keyObjectKey}";

                if (root == null)
                    return $"ERROR|Key object not available: {keyObjectKey}";

                var go = TryGetGameObject(root) ?? root;
                var target = ResolveComponentByTypeName(go, componentTypeName);
                if (target == null)
                    return $"ERROR|Component not found: {componentTypeName}";

                var bound = BuildNumericBinding(target, declaringTypeName, memberKind, memberName);
                if (bound == null || !bound.IsValid)
                    return "ERROR|Member not found or not numeric";

                TryGetNumeric(bound, out _);

                var slotKey = slot.Trim().ToLowerInvariant();
                if (slotKey == "health")
                    _healthMember = bound;
                else if (slotKey == "ammo")
                    _ammoMember = bound;
                else if (slotKey == "money")
                    _moneyMember = bound;
                else if (slotKey == "xp")
                    _xpMember = bound;
                else if (slotKey == "stamina")
                    _staminaMember = bound;
                else
                    return $"ERROR|Unknown slot: {slot}";

                return $"SUCCESS|BOUND|slot={SanitizePipeValue(slotKey)}|binding={SanitizePipeValue(DescribeBinding(bound))}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static object ResolveComponentByTypeName(object gameObjectOrComponent, string componentTypeName)
        {
            if (gameObjectOrComponent == null)
                return null;

            if (IsNullOrWhiteSpace(componentTypeName) || componentTypeName.Trim() == "(GameObject)")
                return gameObjectOrComponent;

            var go = TryGetGameObject(gameObjectOrComponent) ?? gameObjectOrComponent;
            var goType = FindUnityType("UnityEngine.GameObject");
            if (goType == null || !goType.IsInstanceOfType(go))
                return null;

            var componentType = FindUnityType("UnityEngine.Component");
            if (componentType == null)
                return null;

            var getComponents = goType.GetMethod("GetComponents", new[] { typeof(Type) });
            if (getComponents == null)
                return null;

            var components = CoerceToSystemArray(getComponents.Invoke(go, new object[] { componentType }));
            if (components == null)
                return null;

            var wanted = componentTypeName.Trim();
            foreach (var c in components)
            {
                if (c == null)
                    continue;
                var t = c.GetType();
                var full = t.FullName ?? t.Name ?? "";
                if (full.Equals(wanted, StringComparison.Ordinal) || full.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    return c;
            }

            return null;
        }

        private static BoundNumericMember BuildNumericBinding(object target, string declaringTypeName, string memberKind, string memberName)
        {
            if (target == null)
                return null;

            var kind = memberKind.Trim();
            var wantField = kind.Equals("FIELD", StringComparison.OrdinalIgnoreCase) || kind.Equals("F", StringComparison.OrdinalIgnoreCase);
            var wantProp = kind.Equals("PROPERTY", StringComparison.OrdinalIgnoreCase) || kind.Equals("P", StringComparison.OrdinalIgnoreCase);
            if (!wantField && !wantProp)
                return null;

            var desiredDeclType = !IsNullOrWhiteSpace(declaringTypeName) ? ResolveTypeByName(declaringTypeName) : null;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            var t = target.GetType();
            while (t != null)
            {
                if (wantField)
                {
                    FieldInfo f = null;
                    try { f = t.GetField(memberName, flags); } catch { }
                    if (f != null)
                    {
                        if (desiredDeclType == null || f.DeclaringType == desiredDeclType)
                        {
                            if (!f.IsLiteral && !f.IsInitOnly && TryGetNumericType(f.FieldType))
                            {
                                return new BoundNumericMember
                                {
                                    Target = target,
                                    Member = f,
                                    ValueType = f.FieldType
                                };
                            }
                        }
                    }
                }

                if (wantProp)
                {
                    PropertyInfo p = null;
                    try { p = t.GetProperty(memberName, flags); } catch { }
                    if (p != null)
                    {
                        if (desiredDeclType == null || p.DeclaringType == desiredDeclType)
                        {
                            if (p.CanRead && p.CanWrite && (p.GetIndexParameters() == null || p.GetIndexParameters().Length == 0) && TryGetNumericType(p.PropertyType))
                            {
                                return new BoundNumericMember
                                {
                                    Target = target,
                                    Member = p,
                                    ValueType = p.PropertyType
                                };
                            }
                        }
                    }
                }

                t = t.BaseType;
            }

            return null;
        }

        private static string GetUnityObjectName(object unityObject)
        {
            if (unityObject == null)
                return null;
            try
            {
                var t = unityObject.GetType();
                var nameProp = t.GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
                var name = nameProp?.GetValueCompat(unityObject)?.ToString();
                if (!IsNullOrWhiteSpace(name))
                    return name;
            }
            catch
            {
            }

            try
            {
                var goType = FindUnityType("UnityEngine.GameObject");
                if (goType != null && goType.IsInstanceOfType(unityObject))
                {
                    var nameProp = goType.GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
                    var name = nameProp?.GetValueCompat(unityObject)?.ToString();
                    if (!IsNullOrWhiteSpace(name))
                        return name;
                }
            }
            catch
            {
            }

            return unityObject.GetType().Name;
        }

        private static object FindPlayerGameObject()
        {
            return CheatManager.FindPlayerObject();
        }

        private static bool IsTransformChildOf(object childTransform, object parentTransform)
        {
            if (childTransform == null || parentTransform == null)
                return false;

            try
            {
                var t = childTransform.GetType();
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                for (var i = 0; i < methods.Length; i++)
                {
                    var m = methods[i];
                    if (m == null)
                        continue;
                    if (!string.Equals(m.Name, "IsChildOf", StringComparison.Ordinal))
                        continue;
                    var ps = m.GetParameters();
                    if (ps == null || ps.Length != 1)
                        continue;
                    try
                    {
                        var result = m.Invoke(childTransform, new[] { parentTransform });
                        if (result is bool b)
                            return b;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            try
            {
                var current = childTransform;
                for (var i = 0; i < 96 && current != null; i++)
                {
                    if (ReferenceEquals(current, parentTransform))
                        return true;

                    var pt = current.GetType();
                    var parentProp = GetPropertyCached(pt, "parent");
                    if (parentProp == null)
                        break;
                    current = parentProp.GetValueCompat(current);
                }
            }
            catch
            {
            }

            return false;
        }

        private static object GetMainCamera()
        {
            try
            {
                var cameraType = FindUnityType("UnityEngine.Camera");
                var prop = cameraType?.GetProperty("main", BindingFlags.Public | BindingFlags.Static);
                return prop?.GetValueCompat(null);
            }
            catch
            {
                return null;
            }
        }

        private static string BuildStatsBatch(string[] parts)
        {
            HashSet<string> requested = null;
            if (parts != null && parts.Length > 1)
            {
                requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < parts.Length; i++)
                {
                    var key = parts[i];
                    if (!IsNullOrWhiteSpace(key))
                        requested.Add(key.Trim());
                }
            }

            var sb = new StringBuilder();
            sb.Append("STATS_BATCH|");

            var first = true;
            void Add(string key, string value)
            {
                if (!first)
                    sb.Append('|');
                first = false;
                sb.Append(key);
                sb.Append('=');
                sb.Append(value ?? "");
            }

            var includeAll = requested == null || requested.Count == 0;
            if (includeAll || requested.Contains("fps"))
                Add("fps", _fps.ToString("F1"));
            if (includeAll || requested.Contains("memory"))
                Add("memory", (Process.GetCurrentProcess().PrivateMemorySize64 / 1024d / 1024d).ToString("F0"));
            if (includeAll || requested.Contains("health"))
                Add("health", GetPlayerHealthValue());
            if (includeAll || requested.Contains("position"))
                Add("position", GetPlayerPositionValue());
            if (includeAll || requested.Contains("level"))
                Add("level", GetLevelValue());
            if (includeAll || requested.Contains("time"))
                Add("time", GetTimeValue());
            
            // New stats
            if (includeAll || requested.Contains("money"))
                Add("money", CheatManager.GetMoneyValue());
            if (includeAll || requested.Contains("stamina"))
                Add("stamina", CheatManager.GetStaminaValue());
            if (includeAll || requested.Contains("ammo"))
                Add("ammo", CheatManager.GetAmmoValue());

            return sb.ToString();
        }

        private static string GetTimeInfo()
        {
            return $"TIME|{GetTimeValue()}";
        }

        private static string GetLevelInfo()
        {
            return $"LEVEL|{GetLevelValue()}";
        }

        private static string GetPlayerPositionInfo()
        {
            return $"POSITION|{GetPlayerPositionValue()}";
        }

        private static string GetPlayerHealthInfo()
        {
            return $"HEALTH|{GetPlayerHealthValue()}";
        }

        private static string PauseGame()
        {
            var current = GetTimeScale();
            if (current > 0)
                _timeScaleBeforePause = current;
            SetTimeScaleInternal(0f);
            return "SUCCESS|PAUSED";
        }

        private static string ResumeGame()
        {
            var restore = _timeScaleBeforePause;
            if (restore <= 0)
                restore = 1f;
            _timeFrozen = false;
            SetTimeScaleInternal(restore);
            return $"SUCCESS|RESUMED|{restore}";
        }

        private static string ToggleFreezeTime()
        {
            if (_timeFrozen)
            {
                _timeFrozen = false;
                var restore = _timeScaleBeforePause <= 0 ? 1f : _timeScaleBeforePause;
                SetTimeScaleInternal(restore);
                return $"SUCCESS|TIME_FROZEN|false|{restore}";
            }

            var current = GetTimeScale();
            if (current > 0)
                _timeScaleBeforePause = current;
            _timeFrozen = true;
            SetTimeScaleInternal(0f);
            return "SUCCESS|TIME_FROZEN|true";
        }

        private static string SetTimeScale(string args)
        {
            if (IsNullOrWhiteSpace(args))
                return "ERROR|Missing timescale value";

            if (!float.TryParse(args, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
                return "ERROR|Invalid timescale value";

            if (value < 0f)
                value = 0f;

            _timeScaleBeforePause = value <= 0 ? _timeScaleBeforePause : value;
            _timeFrozen = value == 0f;
            SetTimeScaleInternal(value);
            return $"SUCCESS|TIMESCALE|{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        private static string ToggleColliders()
        {
            var colliderType = FindUnityType("UnityEngine.Collider");
            var unityObjectType = FindUnityType("UnityEngine.Object");
            if (colliderType == null || unityObjectType == null)
                return "ERROR|Collider type not found";

            var findMethod = unityObjectType.GetMethod("FindObjectsOfType", new[] { typeof(Type) });
            if (findMethod == null)
                return "ERROR|FindObjectsOfType(Type) not found";

            var colliders = CoerceToSystemArray(findMethod.Invoke(null, new object[] { colliderType }));
            if (colliders == null)
                return "ERROR|No colliders";

            _colliderOverlayEnabled = !_colliderOverlayEnabled;
            return $"SUCCESS|COLLIDER_OVERLAY|{_colliderOverlayEnabled}|{colliders.Length}";
        }

        private static string ToggleWireframe()
        {
            _wireframeEnabled = !_wireframeEnabled;
            try
            {
                var glType = FindUnityType("UnityEngine.GL");
                if (glType == null)
                    return $"ERROR|Wireframe unsupported (GL missing) (requested={_wireframeEnabled})";

                var prop = glType.GetProperty("wireframe", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (prop == null || !prop.CanWrite)
                    return $"ERROR|Wireframe unsupported (GL.wireframe missing) (requested={_wireframeEnabled})";

                prop.SetValueCompat(null, _wireframeEnabled);
                return $"SUCCESS|WIREFRAME|{_wireframeEnabled}";
            }
            catch (Exception ex)
            {
                return $"ERROR|Wireframe failed: {ex.Message} (requested={_wireframeEnabled})";
            }
        }

        private static string ReloadScene()
        {
            var sceneManager = FindUnityType("UnityEngine.SceneManagement.SceneManager");
            if (sceneManager == null)
                return "ERROR|SceneManager not found";

            var getActive = sceneManager.GetMethod("GetActiveScene", BindingFlags.Public | BindingFlags.Static);
            if (getActive == null)
                return "ERROR|GetActiveScene not found";

            var scene = getActive.Invoke(null, null);
            if (scene == null)
                return "ERROR|Active scene unavailable";

            var buildIndexProp = scene.GetType().GetProperty("buildIndex", BindingFlags.Public | BindingFlags.Instance);
            var buildIndex = buildIndexProp != null ? (int)buildIndexProp.GetValueCompat(scene) : -1;

            var loadScene = sceneManager.GetMethod("LoadScene", new[] { typeof(int) });
            if (loadScene == null)
                return "ERROR|LoadScene(int) not found";

            loadScene.Invoke(null, new object[] { buildIndex });
            return $"SUCCESS|SCENE_RELOAD|{buildIndex}";
        }

        private static string CrashGame()
        {
            Environment.FailFast("Crash requested by external tool");
            return "ERROR|FailFast returned";
        }

        private static string SetDayTime()
        {
            return SetLighting(1.1f, 1f, 1f, 0.9f);
        }

        private static string SetNightTime()
        {
            return SetLighting(0.25f, 0.25f, 0.35f, 0.2f);
        }

        private static string SetLighting(float r, float g, float b, float intensity)
        {
            var lightType = FindUnityType("UnityEngine.Light");
            var unityObjectType = FindUnityType("UnityEngine.Object");
            var colorType = FindUnityType("UnityEngine.Color");
            if (lightType == null || unityObjectType == null)
                return "ERROR|Light types not found";

            var findMethod = unityObjectType.GetMethod("FindObjectsOfType", new[] { typeof(Type) });
            if (findMethod == null)
                return "ERROR|FindObjectsOfType(Type) not found";

            var lights = CoerceToSystemArray(findMethod.Invoke(null, new object[] { lightType }));
            if (lights == null || lights.Length == 0)
                return "ERROR|No lights found";

            object color = null;
            if (colorType != null)
            {
                try { color = Activator.CreateInstance(colorType, new object[] { r, g, b, 1f }); } catch { }
            }

            var intensityProp = lightType.GetProperty("intensity", BindingFlags.Public | BindingFlags.Instance);
            var colorProp = lightType.GetProperty("color", BindingFlags.Public | BindingFlags.Instance);

            var updated = 0;
            foreach (var light in lights)
            {
                try
                {
                    if (intensityProp != null && intensityProp.CanWrite)
                        intensityProp.SetValueCompat(light, intensity);
                    if (color != null && colorProp != null && colorProp.CanWrite)
                        colorProp.SetValueCompat(light, color);
                    updated++;
                }
                catch
                {
                }
            }

            return $"SUCCESS|LIGHTING|{updated}";
        }

        private static float GetTimeScale()
        {
            try
            {
                var timeType = FindUnityType("UnityEngine.Time");
                var prop = timeType?.GetProperty("timeScale", BindingFlags.Public | BindingFlags.Static);
                if (prop == null)
                    return 1f;
                return Convert.ToSingle(prop.GetValueCompat(null));
            }
            catch
            {
                return 1f;
            }
        }

        private static void SetTimeScaleInternal(float value)
        {
            try
            {
                var timeType = FindUnityType("UnityEngine.Time");
                var prop = timeType?.GetProperty("timeScale", BindingFlags.Public | BindingFlags.Static);
                prop?.SetValueCompat(null, value);
            }
            catch
            {
            }
        }

        private static string GetTimeValue()
        {
            try
            {
                var timeType = FindUnityType("UnityEngine.Time");
                var prop = timeType?.GetProperty("timeSinceLevelLoad", BindingFlags.Public | BindingFlags.Static);
                if (prop == null)
                    return "0";
                var seconds = Convert.ToSingle(prop.GetValueCompat(null));
                return seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return "0";
            }
        }

        private static string GetLevelValue()
        {
            try
            {
                var sceneManager = FindUnityType("UnityEngine.SceneManagement.SceneManager");
                var getActive = sceneManager?.GetMethod("GetActiveScene", BindingFlags.Public | BindingFlags.Static);
                if (getActive == null)
                    return "Unknown";
                var scene = getActive.Invoke(null, null);
                if (scene == null)
                    return "Unknown";
                var nameProp = scene.GetType().GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
                var name = nameProp?.GetValueCompat(scene)?.ToString();
                return IsNullOrWhiteSpace(name) ? "Unknown" : name;
            }
            catch
            {
                return "Unknown";
            }
        }

        private static string GetPlayerPositionValue()
        {
            try
            {
                var player = GetCachedPlayerGameObject();
                if (player == null)
                    return "Unknown";

                var goType = FindUnityType("UnityEngine.GameObject");
                var transformProp = GetPropertyCached(goType, "transform");
                var transform = transformProp?.GetValueCompat(player);
                if (transform == null)
                    return "Unknown";

                var transformType = transform.GetType();
                var posProp = GetPropertyCached(transformType, "position");
                var pos = posProp?.GetValueCompat(transform);
                if (pos == null)
                    return "Unknown";

                var posType = pos.GetType();
                var px = GetPropertyCached(posType, "x");
                var py = GetPropertyCached(posType, "y");
                var pz = GetPropertyCached(posType, "z");
                var x = Convert.ToSingle(px?.GetValueCompat(pos), System.Globalization.CultureInfo.InvariantCulture);
                var y = Convert.ToSingle(py?.GetValueCompat(pos), System.Globalization.CultureInfo.InvariantCulture);
                var z = Convert.ToSingle(pz?.GetValueCompat(pos), System.Globalization.CultureInfo.InvariantCulture);

                return $"{x:F1},{y:F1},{z:F1}";
            }
            catch
            {
                return "Unknown";
            }
        }

        private static string GetPlayerHealthValue()
        {
            return CheatManager.GetHealthValue();
        }

        private static object GetCachedPlayerGameObject()
        {
            try
            {
                var cached = _cachedPlayerObject;
                if (cached != null)
                {
                    var existing = cached.Target;
                    if (existing != null && IsUnityObjectAlive(existing))
                        return existing;
                }

                var now = Stopwatch.GetTimestamp();
                if (_cachedPlayerObjectTicks != 0 && (now - _cachedPlayerObjectTicks) < Stopwatch.Frequency * 1)
                {
                    if (cached != null)
                        return cached.Target;
                }

                _cachedPlayerObjectTicks = now;
                var found = FindPlayerGameObject();
                _cachedPlayerObject = new WeakReference(found);
                return found;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsUnityObjectAlive(object obj)
        {
            if (obj == null)
                return false;
            try
            {
                var t = obj.GetType();
                var mi = GetMethodCached(t, "GetInstanceID", Type.EmptyTypes);
                if (mi == null)
                    return true;
                mi.Invoke(obj, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ToggleGodMode()
        {
            EnsureHealthBinding();
            if (_healthMember == null || !_healthMember.IsValid)
                return "ERROR|Health not found";

            _godModeEnabled = !_godModeEnabled;
            if (_godModeEnabled)
            {
                _infiniteHealthEnabled = true;
                ApplyGodModePatches();
            }
            else
            {
                RemoveGodModePatches();
            }

            return $"SUCCESS|GODMODE|{_godModeEnabled}";
        }

        private static string ToggleInfiniteHealth()
        {
            EnsureHealthBinding();
            if (_healthMember == null || !_healthMember.IsValid)
                return "ERROR|Health not found";

            _infiniteHealthEnabled = !_infiniteHealthEnabled;
            if (!_infiniteHealthEnabled)
            {
                _godModeEnabled = false;
                RemoveGodModePatches();
            }

            return $"SUCCESS|INFINITE_HEALTH|{_infiniteHealthEnabled}";
        }

        private static void ApplyGodModePatches()
        {
            try
            {
                if (_godModePatchedMethods.Count > 0)
                    return;

                var prefix = BuildPrefixPatch("DISABLE");
                if (prefix == null)
                    return;

                var harmony = new HarmonyLib.Harmony(CheatHarmonyId);
                var keywords = new[] { "takedamage", "applydamage", "damage", "hurt", "hit", "kill", "die", "ondeath", "death", "killed", "deducthealth", "losehealth" };
                var patched = 0;
                var budget = 12000;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm == null || IsDynamicAssembly(asm))
                        continue;
                    string an;
                    try { an = asm.GetName().Name ?? ""; } catch { an = ""; }
                    if (an.Length == 0)
                        continue;
                    if (an.StartsWith("Unity", StringComparison.OrdinalIgnoreCase) ||
                        an.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
                        an.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                        an.StartsWith("Il2Cpp", StringComparison.OrdinalIgnoreCase) ||
                        an.StartsWith("Mono", StringComparison.OrdinalIgnoreCase) ||
                        an.StartsWith("MelonLoader", StringComparison.OrdinalIgnoreCase) ||
                        an.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase) ||
                        an.StartsWith("Harmony", StringComparison.OrdinalIgnoreCase) ||
                        an.StartsWith("Newtonsoft", StringComparison.OrdinalIgnoreCase) ||
                        an.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                        an.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Type[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = ex.Types.Where(t => t != null).ToArray();
                    }
                    catch
                    {
                        continue;
                    }

                    for (var ti = 0; ti < types.Length && patched < 80 && budget > 0; ti++)
                    {
                        var t = types[ti];
                        if (t == null)
                            continue;
                        MethodInfo[] methods;
                        try
                        {
                            methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                        }
                        catch
                        {
                            continue;
                        }

                        for (var mi = 0; mi < methods.Length && patched < 80 && budget-- > 0; mi++)
                        {
                            var m = methods[mi];
                            if (m == null || m.IsAbstract || m.ContainsGenericParameters)
                                continue;
                            if (m.ReturnType != typeof(void))
                                continue;

                            var name = m.Name ?? "";
                            if (name.Length == 0)
                                continue;
                            var lower = name.ToLowerInvariant();
                            var match = false;
                            for (var k = 0; k < keywords.Length; k++)
                            {
                                if (lower.Contains(keywords[k]))
                                {
                                    match = true;
                                    break;
                                }
                            }
                            if (!match)
                                continue;

                            try
                            {
                                harmony.Patch(m, prefix: new HarmonyMethod(prefix));
                                _godModePatchedMethods.Add(m);
                                patched++;
                            }
                            catch
                            {
                            }
                        }
                    }

                    if (patched >= 80 || budget <= 0)
                        break;
                }
            }
            catch
            {
            }
        }

        private static bool IsDynamicAssembly(Assembly asm)
        {
            if (asm == null)
                return false;
            try
            {
                var prop = typeof(Assembly).GetProperty("IsDynamic", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.PropertyType == typeof(bool))
                {
                    try { return (bool)prop.GetValue(asm, null); } catch { }
                }
            }
            catch
            {
            }

            try
            {
                return asm is AssemblyBuilder;
            }
            catch
            {
                return false;
            }
        }

        private static void RemoveGodModePatches()
        {
            try
            {
                if (_godModePatchedMethods.Count == 0)
                    return;

                var harmony = new HarmonyLib.Harmony(CheatHarmonyId);
                for (var i = 0; i < _godModePatchedMethods.Count; i++)
                {
                    var m = _godModePatchedMethods[i];
                    if (m == null)
                        continue;
                    try { harmony.Unpatch(m, HarmonyPatchType.All, CheatHarmonyId); } catch { }
                }
            }
            catch
            {
            }
            finally
            {
                _godModePatchedMethods.Clear();
                _godModeOverrides.Clear();
            }
        }

        private static string ToggleInfiniteAmmo()
        {
            EnsureAmmoBinding();
            if (_ammoMember == null || !_ammoMember.IsValid)
                return "ERROR|Ammo not found";

            _infiniteAmmoEnabled = !_infiniteAmmoEnabled;
            return $"SUCCESS|INFINITE_AMMO|{_infiniteAmmoEnabled}";
        }

        private static string ToggleInfiniteStamina()
        {
            EnsureStaminaBinding();
            if (_staminaMember == null || !_staminaMember.IsValid)
                return "ERROR|Stamina not found";

            _infiniteStaminaEnabled = !_infiniteStaminaEnabled;
            return $"SUCCESS|INFINITE_STAMINA|{_infiniteStaminaEnabled}";
        }

        private static string SetCapabilityState(string capability, string desiredState)
        {
            if (IsNullOrWhiteSpace(capability))
                return "ERROR|Missing capability";

            var want = desiredState != null &&
                       (desiredState.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                        desiredState.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                        desiredState.Equals("on", StringComparison.OrdinalIgnoreCase));

            var c = capability.Trim().ToUpperInvariant();

            if (c == "TOGGLE_GODMODE")
            {
                if (_godModeEnabled == want)
                    return $"SUCCESS|GODMODE|{_godModeEnabled}";
                return ToggleGodMode();
            }

            if (c == "TOGGLE_INFINITE_HEALTH")
            {
                if (_infiniteHealthEnabled == want)
                    return $"SUCCESS|INFINITE_HEALTH|{_infiniteHealthEnabled}";
                return ToggleInfiniteHealth();
            }

            if (c == "TOGGLE_INFINITE_AMMO")
            {
                if (_infiniteAmmoEnabled == want)
                    return $"SUCCESS|INFINITE_AMMO|{_infiniteAmmoEnabled}";
                return ToggleInfiniteAmmo();
            }

            if (c == "TOGGLE_INFINITE_STAMINA")
            {
                if (_infiniteStaminaEnabled == want)
                    return $"SUCCESS|INFINITE_STAMINA|{_infiniteStaminaEnabled}";
                return ToggleInfiniteStamina();
            }

            return $"ERROR|Unknown capability: {capability}";
        }

        private static string AddToHealth(string args)
        {
            EnsureHealthBinding();
            if (_healthMember == null || !_healthMember.IsValid)
                return "ERROR|Health not found";

            if (!TryParseDouble(args, out var amount))
                amount = 50;

            var current = TryGetNumeric(_healthMember, out var value) ? value : _healthMember.LastKnownValue;
            var next = current + amount;
            if (!TrySetNumeric(_healthMember, next))
                return "ERROR|Failed to set health";

            return $"SUCCESS|ADD_HEALTH|{amount}";
        }

        private static string AddToMoney(string args)
        {
            EnsureMoneyBinding();
            if (_moneyMember == null || !_moneyMember.IsValid)
                return "ERROR|Money not found";

            if (!TryParseDouble(args, out var amount))
                amount = 1000;

            var current = TryGetNumeric(_moneyMember, out var value) ? value : _moneyMember.LastKnownValue;
            var next = current + amount;
            if (!TrySetNumeric(_moneyMember, next))
                return "ERROR|Failed to set money";

            return $"SUCCESS|ADD_MONEY|{amount}";
        }

        private static string AddToXp(string args)
        {
            EnsureXpBinding();
            if (_xpMember == null || !_xpMember.IsValid)
                return "ERROR|XP not found";

            if (!TryParseDouble(args, out var amount))
                amount = 500;

            var current = TryGetNumeric(_xpMember, out var value) ? value : _xpMember.LastKnownValue;
            var next = current + amount;
            if (!TrySetNumeric(_xpMember, next))
                return "ERROR|Failed to set XP";

            return $"SUCCESS|ADD_XP|{amount}";
        }

        private static string AddToStamina(string args)
        {
            EnsureStaminaBinding();
            if (_staminaMember == null || !_staminaMember.IsValid)
                return "ERROR|Stamina not found";

            if (!TryParseDouble(args, out var amount))
                amount = 50;

            var current = TryGetNumeric(_staminaMember, out var value) ? value : _staminaMember.LastKnownValue;
            var next = current + amount;
            if (!TrySetNumeric(_staminaMember, next))
                return "ERROR|Failed to set stamina";

            return $"SUCCESS|ADD_STAMINA|{amount}";
        }

        private static string SkipLevel()
        {
            var sceneManager = FindUnityType("UnityEngine.SceneManagement.SceneManager");
            if (sceneManager == null)
                return "ERROR|SceneManager not found";

            var getActive = sceneManager.GetMethod("GetActiveScene", BindingFlags.Public | BindingFlags.Static);
            if (getActive == null)
                return "ERROR|GetActiveScene not found";

            var scene = getActive.Invoke(null, null);
            if (scene == null)
                return "ERROR|Active scene unavailable";

            var buildIndexProp = scene.GetType().GetProperty("buildIndex", BindingFlags.Public | BindingFlags.Instance);
            var index = buildIndexProp != null ? (int)buildIndexProp.GetValueCompat(scene) : -1;
            if (index < 0)
                return "ERROR|Invalid build index";

            var loadScene = sceneManager.GetMethod("LoadScene", new[] { typeof(int) });
            if (loadScene == null)
                return "ERROR|LoadScene(int) not found";

            loadScene.Invoke(null, new object[] { index + 1 });
            return $"SUCCESS|SKIP_LEVEL|{index + 1}";
        }

        private static string RestartLevel()
        {
            return ReloadScene();
        }

        private static Array GetComponentsInChildrenFallback(object gameObject, Type componentType)
        {
            if (gameObject == null || componentType == null)
                return null;

            var goType = FindUnityType("UnityEngine.GameObject");
            if (goType == null)
                return null;

            try
            {
                var mi = goType.GetMethod(
                    "GetComponentsInChildren",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(Type), typeof(bool) },
                    null);
                if (mi != null)
                    return CoerceToSystemArray(mi.Invoke(gameObject, new object[] { componentType, true }));
            }
            catch
            {
            }

            try
            {
                var mi = goType.GetMethod(
                    "GetComponentsInChildren",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(Type) },
                    null);
                if (mi != null)
                    return CoerceToSystemArray(mi.Invoke(gameObject, new object[] { componentType }));
            }
            catch
            {
            }

            try
            {
                var mi = goType.GetMethod("GetComponents", new[] { typeof(Type) });
                if (mi == null)
                    return null;

                var results = new List<object>();
                var visited = new HashSet<int>();
                var queue = new Queue<object>();
                queue.Enqueue(gameObject);
                visited.Add(RuntimeHelpers.GetHashCode(gameObject));

                var maxNodes = 600;
                var maxComponents = 8000;

                while (queue.Count > 0 && maxNodes-- > 0 && results.Count < maxComponents)
                {
                    var current = queue.Dequeue();
                    if (current == null)
                        continue;

                    try
                    {
                        var comps = CoerceToSystemArray(mi.Invoke(current, new object[] { componentType }));
                        if (comps != null)
                        {
                            foreach (var c in comps)
                            {
                                if (c != null)
                                    results.Add(c);
                            }
                        }
                    }
                    catch
                    {
                    }

                    object transform = null;
                    try { transform = GetTransform(current); } catch { transform = null; }
                    if (transform == null)
                        continue;

                    var tt = transform.GetType();
                    var childCountProp = tt.GetProperty("childCount", BindingFlags.Public | BindingFlags.Instance);
                    var getChild = tt.GetMethod("GetChild", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                    var gameObjectProp = tt.GetProperty("gameObject", BindingFlags.Public | BindingFlags.Instance);
                    if (childCountProp == null || getChild == null || gameObjectProp == null)
                        continue;

                    int childCount;
                    try { childCount = Convert.ToInt32(childCountProp.GetValueCompat(transform)); }
                    catch { childCount = 0; }
                    if (childCount <= 0)
                        continue;

                    for (var i = 0; i < childCount && results.Count < maxComponents; i++)
                    {
                        object childTransform = null;
                        try { childTransform = getChild.Invoke(transform, new object[] { i }); } catch { childTransform = null; }
                        if (childTransform == null)
                            continue;

                        object childGo = null;
                        try { childGo = gameObjectProp.GetValueCompat(childTransform); } catch { childGo = null; }
                        if (childGo == null)
                            continue;

                        var hash = RuntimeHelpers.GetHashCode(childGo);
                        if (!visited.Add(hash))
                            continue;
                        queue.Enqueue(childGo);
                    }
                }

                if (results.Count == 0)
                    return null;

                var arr = Array.CreateInstance(componentType, results.Count);
                for (var i = 0; i < results.Count; i++)
                    arr.SetValue(results[i], i);
                return arr;
            }
            catch
            {
                return null;
            }
        }

        private static void RememberOriginal(ObjectMemberKey key, object original, Dictionary<ObjectMemberKey, object> store, object component)
        {
            if (store.ContainsKey(key))
                return;
            store[key] = original;
            if (component == null)
                return;
            for (var i = 0; i < _noclipChangedComponents.Count; i++)
            {
                if (ReferenceEquals(_noclipChangedComponents[i], component))
                    return;
            }
            _noclipChangedComponents.Add(component);
        }

        private static void TrySetBoolMember(object component, string memberName, bool value, Dictionary<ObjectMemberKey, object> store)
        {
            if (component == null || IsNullOrWhiteSpace(memberName))
                return;

            try
            {
                var t = component.GetType();
                var prop = t.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null && prop.PropertyType == typeof(bool) && prop.CanWrite)
                {
                    var key = new ObjectMemberKey { Target = component, MemberName = memberName };
                    object original = null;
                    try { original = prop.GetValueCompat(component); } catch { original = null; }
                    RememberOriginal(key, original ?? true, store, component);
                    try { prop.SetValueCompat(component, value); } catch { }
                    return;
                }

                var field = t.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null && field.FieldType == typeof(bool))
                {
                    var key = new ObjectMemberKey { Target = component, MemberName = memberName };
                    object original = null;
                    try { original = field.GetValue(component); } catch { original = null; }
                    RememberOriginal(key, original ?? true, store, component);
                    try { field.SetValue(component, value); } catch { }
                }
            }
            catch
            {
            }
        }

        private static void TryRestoreBoolMember(object component, string memberName, Dictionary<ObjectMemberKey, object> store)
        {
            if (component == null || IsNullOrWhiteSpace(memberName))
                return;
            try
            {
                var key = new ObjectMemberKey { Target = component, MemberName = memberName };
                if (!store.TryGetValue(key, out var original))
                    return;
                var t = component.GetType();
                var prop = t.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null && prop.PropertyType == typeof(bool) && prop.CanWrite)
                {
                    try { prop.SetValueCompat(component, original); } catch { }
                    return;
                }
                var field = t.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null && field.FieldType == typeof(bool))
                {
                    try { field.SetValue(component, original); } catch { }
                }
            }
            catch
            {
            }
        }

        private static string ToggleNoclip()
        {
            var player = FindPlayerGameObject();
            if (player == null)
                return "ERROR|Player not found";

            var colliderType = FindUnityType("UnityEngine.Collider");
            var ccType = FindUnityType("UnityEngine.CharacterController");
            var rigidbodyType = FindUnityType("UnityEngine.Rigidbody");

            if (!_noclipEnabled)
            {
                _noclipChangedComponents.Clear();
                _noclipOriginalValues.Clear();
            }

            _noclipEnabled = !_noclipEnabled;

            if (!_noclipEnabled)
            {
                for (var i = 0; i < _noclipChangedComponents.Count; i++)
                {
                    var c = _noclipChangedComponents[i];
                    if (c == null)
                        continue;
                    TryRestoreBoolMember(c, "enabled", _noclipOriginalValues);
                    TryRestoreBoolMember(c, "detectCollisions", _noclipOriginalValues);
                    TryRestoreBoolMember(c, "isKinematic", _noclipOriginalValues);
                    TryRestoreBoolMember(c, "useGravity", _noclipOriginalValues);
                }
                return $"SUCCESS|NOCLIP|{_noclipEnabled}";
            }

            if (colliderType != null)
            {
                var colliders = GetComponentsInChildrenFallback(player, colliderType);
                if (colliders != null)
                {
                    foreach (var c in colliders)
                    {
                        if (c == null)
                            continue;
                        TrySetBoolMember(c, "enabled", false, _noclipOriginalValues);
                    }
                }
            }

            if (ccType != null)
            {
                var controllers = GetComponentsInChildrenFallback(player, ccType);
                if (controllers != null)
                {
                    foreach (var cc in controllers)
                    {
                        if (cc == null)
                            continue;
                        TrySetBoolMember(cc, "enabled", false, _noclipOriginalValues);
                    }
                }
            }

            if (rigidbodyType != null)
            {
                var bodies = GetComponentsInChildrenFallback(player, rigidbodyType);
                if (bodies != null)
                {
                    foreach (var rb in bodies)
                    {
                        if (rb == null)
                            continue;
                        TrySetBoolMember(rb, "detectCollisions", false, _noclipOriginalValues);
                        TrySetBoolMember(rb, "useGravity", false, _noclipOriginalValues);
                        TrySetBoolMember(rb, "isKinematic", true, _noclipOriginalValues);
                    }
                }
            }

            return $"SUCCESS|NOCLIP|{_noclipEnabled}";
        }

        private static string ToggleFreeCamera()
        {
            if (_freeCameraEnabled)
            {
                _freeCameraEnabled = false;
                TryDisableFreeCam();
                return "SUCCESS|FREE_CAMERA|false";
            }

            var camera = GetMainCamera();
            var cameraType = FindUnityType("UnityEngine.Camera");
            if (camera == null && cameraType != null)
            {
                try
                {
                    var all = FindUnityObjectsOfType(cameraType);
                    if (all != null)
                    {
                        foreach (var c in all)
                        {
                            if (c != null)
                            {
                                camera = c;
                                break;
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            if (camera == null)
                return "ERROR|Main camera not found";

            if (cameraType == null)
                cameraType = FindUnityType("UnityEngine.Camera");
            var gameObjectType = FindUnityType("UnityEngine.GameObject");
            if (gameObjectType == null || cameraType == null)
                return "ERROR|Unity types missing";

            object cameraGo = null;
            try
            {
                var goProp = camera.GetType().GetProperty("gameObject", BindingFlags.Public | BindingFlags.Instance);
                cameraGo = goProp?.GetValueCompat(camera);
            }
            catch
            {
                cameraGo = null;
            }
            if (cameraGo == null)
                return "ERROR|Camera gameObject not found";

            _mainCameraBeforeFreeCam = camera;
            _freeCamGameObject = cameraGo;
            _freeCamUsingMainCamera = true;

            _freeCamDisabledComponents.Clear();
            _freeCamOriginalValues.Clear();

            object transform = null;
            try { transform = GetTransform(cameraGo); } catch { transform = null; }
            if (transform != null)
            {
                var tt = transform.GetType();
                var parentProp = tt.GetProperty("parent", BindingFlags.Public | BindingFlags.Instance);
                var posProp = tt.GetProperty("position", BindingFlags.Public | BindingFlags.Instance);
                var rotProp = tt.GetProperty("rotation", BindingFlags.Public | BindingFlags.Instance);

                try { _freeCamOriginalParent = parentProp?.GetValueCompat(transform); } catch { _freeCamOriginalParent = null; }
                try { _freeCamOriginalLocalPosition = posProp?.GetValueCompat(transform); } catch { _freeCamOriginalLocalPosition = null; }
                try { _freeCamOriginalLocalEulerAngles = rotProp?.GetValueCompat(transform); } catch { _freeCamOriginalLocalEulerAngles = null; }

                if (parentProp != null && parentProp.CanWrite)
                {
                    try { parentProp.SetValueCompat(transform, null); } catch { }
                }
            }

            try
            {
                var behaviourType = FindUnityType("UnityEngine.Behaviour");
                if (behaviourType != null)
                {
                    var getComponents = gameObjectType.GetMethod("GetComponents", new[] { typeof(Type) });
                    if (getComponents != null)
                    {
                        var comps = CoerceToSystemArray(getComponents.Invoke(cameraGo, new object[] { behaviourType }));
                        if (comps != null)
                        {
                            foreach (var c in comps)
                            {
                                if (c == null)
                                    continue;
                                if (cameraType.IsInstanceOfType(c))
                                    continue;
                                var ct = c.GetType();
                                var enabledProp = ct.GetProperty("enabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (enabledProp == null || enabledProp.PropertyType != typeof(bool) || !enabledProp.CanWrite)
                                    continue;

                                object original;
                                try { original = enabledProp.GetValueCompat(c); } catch { original = true; }
                                var key = new ObjectMemberKey { Target = c, MemberName = "enabled" };
                                if (!_freeCamOriginalValues.ContainsKey(key))
                                    _freeCamOriginalValues[key] = original ?? true;
                                _freeCamDisabledComponents.Add(c);
                                try { enabledProp.SetValueCompat(c, false); } catch { }
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            _freeCameraEnabled = true;
            return "SUCCESS|FREE_CAMERA|true";
        }

        private static void TryDisableFreeCam()
        {
            try
            {
                for (var i = 0; i < _freeCamDisabledComponents.Count; i++)
                {
                    var c = _freeCamDisabledComponents[i];
                    if (c == null)
                        continue;
                    var key = new ObjectMemberKey { Target = c, MemberName = "enabled" };
                    if (!_freeCamOriginalValues.TryGetValue(key, out var original))
                        continue;
                    try
                    {
                        var prop = c.GetType().GetProperty("enabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (prop != null && prop.PropertyType == typeof(bool) && prop.CanWrite)
                            prop.SetValueCompat(c, original);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            try
            {
                if (_freeCamGameObject != null)
                {
                    var transform = GetTransform(_freeCamGameObject);
                    if (transform != null)
                    {
                        var tt = transform.GetType();
                        var parentProp = tt.GetProperty("parent", BindingFlags.Public | BindingFlags.Instance);
                        var posProp = tt.GetProperty("position", BindingFlags.Public | BindingFlags.Instance);
                        var rotProp = tt.GetProperty("rotation", BindingFlags.Public | BindingFlags.Instance);

                        if (parentProp != null && parentProp.CanWrite)
                        {
                            try { parentProp.SetValueCompat(transform, _freeCamOriginalParent); } catch { }
                        }
                        if (posProp != null && posProp.CanWrite && _freeCamOriginalLocalPosition != null)
                        {
                            try { posProp.SetValueCompat(transform, _freeCamOriginalLocalPosition); } catch { }
                        }
                        if (rotProp != null && rotProp.CanWrite && _freeCamOriginalLocalEulerAngles != null)
                        {
                            try { rotProp.SetValueCompat(transform, _freeCamOriginalLocalEulerAngles); } catch { }
                        }
                    }
                }
            }
            catch
            {
            }

            _freeCamDisabledComponents.Clear();
            _freeCamOriginalValues.Clear();
            _freeCamUsingMainCamera = false;
            _freeCamOriginalParent = null;
            _freeCamOriginalLocalPosition = null;
            _freeCamOriginalLocalEulerAngles = null;
            _freeCamGameObject = null;
            _mainCameraBeforeFreeCam = null;
        }

        private static void CopyCameraSettings(object fromCamera, object toCamera)
        {
            try
            {
                var cameraType = FindUnityType("UnityEngine.Camera");
                if (cameraType == null)
                    return;

                var fovProp = cameraType.GetProperty("fieldOfView", BindingFlags.Public | BindingFlags.Instance);
                if (fovProp != null && fovProp.CanWrite)
                {
                    var fov = fovProp.GetValueCompat(fromCamera);
                    fovProp.SetValueCompat(toCamera, fov);
                }

                var depthProp = cameraType.GetProperty("depth", BindingFlags.Public | BindingFlags.Instance);
                if (depthProp != null && depthProp.CanWrite)
                {
                    depthProp.SetValueCompat(toCamera, 100f);
                }

                var clearFlagsProp = cameraType.GetProperty("clearFlags", BindingFlags.Public | BindingFlags.Instance);
                if (clearFlagsProp != null && clearFlagsProp.CanWrite)
                {
                    clearFlagsProp.SetValueCompat(toCamera, clearFlagsProp.GetValueCompat(fromCamera));
                }

                var bgColorProp = cameraType.GetProperty("backgroundColor", BindingFlags.Public | BindingFlags.Instance);
                if (bgColorProp != null && bgColorProp.CanWrite)
                {
                    bgColorProp.SetValueCompat(toCamera, bgColorProp.GetValueCompat(fromCamera));
                }
            }
            catch
            {
            }
        }

        private static void UpdateFreeCameraControls()
        {
            try
            {
                if (_freeCamGameObject == null)
                    return;

                var transform = GetTransform(_freeCamGameObject);
                if (transform == null)
                    return;

                var timeType = FindUnityType("UnityEngine.Time");
                var deltaProp = timeType?.GetProperty("deltaTime", BindingFlags.Public | BindingFlags.Static);
                var dt = 0.016f;
                if (deltaProp != null)
                {
                try { dt = Convert.ToSingle(deltaProp.GetValueCompat(null)); } catch { }
                    if (dt <= 0) dt = 0.016f;
                }

                var speed = 10f;
                var boost = GetKey("LeftShift") || GetKey("RightShift");
                if (boost) speed = 25f;

                var forwardInput = (GetKey("W") ? 1f : 0f) + (GetKey("S") ? -1f : 0f);
                var rightInput = (GetKey("D") ? 1f : 0f) + (GetKey("A") ? -1f : 0f);
                var upInput = (GetKey("E") ? 1f : 0f) + (GetKey("Q") ? -1f : 0f);

                var transformType = transform.GetType();
                var posProp = transformType.GetProperty("position", BindingFlags.Public | BindingFlags.Instance);
                var forwardProp = transformType.GetProperty("forward", BindingFlags.Public | BindingFlags.Instance);
                var rightProp = transformType.GetProperty("right", BindingFlags.Public | BindingFlags.Instance);
                var upProp = transformType.GetProperty("up", BindingFlags.Public | BindingFlags.Instance);

                var pos = posProp?.GetValueCompat(transform);
                var forward = forwardProp?.GetValueCompat(transform);
                var right = rightProp?.GetValueCompat(transform);
                var up = upProp?.GetValueCompat(transform);
                if (pos == null || forward == null || right == null || up == null)
                    return;

                var p = ReadVector3(pos);
                var f = ReadVector3(forward);
                var r = ReadVector3(right);
                var u = ReadVector3(up);

                var step = speed * dt;
                var px = p.X + (f.X * forwardInput + r.X * rightInput + u.X * upInput) * step;
                var py = p.Y + (f.Y * forwardInput + r.Y * rightInput + u.Y * upInput) * step;
                var pz = p.Z + (f.Z * forwardInput + r.Z * rightInput + u.Z * upInput) * step;

                var vector3Type = FindUnityType("UnityEngine.Vector3");
                if (vector3Type != null && posProp != null && posProp.CanWrite)
                {
                    var newPos = Activator.CreateInstance(vector3Type, new object[] { px, py, pz });
                    posProp.SetValueCompat(transform, newPos);
                }

                var rotSpeed = 90f * dt;
                var yaw = (GetKey("RightArrow") ? 1f : 0f) + (GetKey("LeftArrow") ? -1f : 0f);
                var pitch = (GetKey("DownArrow") ? 1f : 0f) + (GetKey("UpArrow") ? -1f : 0f);
                if ((Math.Abs(yaw) > 0.01f || Math.Abs(pitch) > 0.01f) && vector3Type != null)
                {
                    var rotateMethod = transformType.GetMethod("Rotate", new[] { vector3Type });
                    if (rotateMethod != null)
                    {
                        var rotVec = Activator.CreateInstance(vector3Type, new object[] { pitch * rotSpeed, yaw * rotSpeed, 0f });
                        rotateMethod.Invoke(transform, new[] { rotVec });
                    }
                }
            }
            catch
            {
            }
        }

        private static bool GetKey(string keyName)
        {
            try
            {
                var inputType = FindUnityType("UnityEngine.Input");
                var keyCodeType = FindUnityType("UnityEngine.KeyCode");
                if (inputType == null || keyCodeType == null)
                    return false;

                var getKeyMethod = inputType.GetMethod("GetKey", BindingFlags.Public | BindingFlags.Static, null, new[] { keyCodeType }, null);
                if (getKeyMethod == null)
                    return false;

                object key;
                try { key = Enum.Parse(keyCodeType, keyName, true); }
                catch { return false; }

                return Convert.ToBoolean(getKeyMethod.Invoke(null, new[] { key }));
            }
            catch
            {
                return false;
            }
        }

        private static string ToggleZoomOut()
        {
            var camera = GetMainCamera();
            if (camera == null)
                return "ERROR|Main camera not found";

            var cameraType = FindUnityType("UnityEngine.Camera");
            if (cameraType == null)
                return "ERROR|Camera type not found";

            var fovProp = cameraType.GetProperty("fieldOfView", BindingFlags.Public | BindingFlags.Instance);
            if (fovProp == null || !fovProp.CanRead || !fovProp.CanWrite)
                return "ERROR|Camera.fieldOfView not available";

            if (!_cameraOriginalCaptured)
            {
                try { _originalFov = Convert.ToSingle(fovProp.GetValueCompat(camera)); } catch { _originalFov = 60f; }
                _cameraOriginalCaptured = true;
            }

            _zoomOutEnabled = !_zoomOutEnabled;
            var newFov = _zoomOutEnabled ? Math.Max(_originalFov, 90f) : _originalFov;
            fovProp.SetValueCompat(camera, newFov);
            return $"SUCCESS|ZOOM_OUT|{_zoomOutEnabled}";
        }

        private static string EnableFirstPerson()
        {
            return SetCameraFollowMode(firstPerson: true);
        }

        private static string EnableThirdPerson()
        {
            return SetCameraFollowMode(firstPerson: false);
        }

        private static string ResetCamera()
        {
            var cam = GetMainCamera();
            if (cam == null)
                return "ERROR|Main camera not found";

            RestoreCameraTransform(cam);
            _firstPersonEnabled = false;
            _thirdPersonEnabled = false;
            return "SUCCESS|RESET_CAMERA";
        }

        private static string SetCameraFollowMode(bool firstPerson)
        {
            var cam = GetMainCamera();
            if (cam == null)
                return "ERROR|Main camera not found";

            var player = FindPlayerGameObject();
            if (player == null)
                return "ERROR|Player not found";

            CaptureCameraOriginal(cam);

            var camTransform = GetTransform(cam);
            var playerTransform = GetTransform(player);
            if (camTransform == null || playerTransform == null)
                return "ERROR|Transform not found";

            if (firstPerson)
            {
                _firstPersonEnabled = true;
                _thirdPersonEnabled = false;
                SetTransformPositionRelativeTo(playerTransform, camTransform, 0f, 1.6f, 0f);
                return "SUCCESS|FIRST_PERSON";
            }

            _thirdPersonEnabled = true;
            _firstPersonEnabled = false;
            SetTransformPositionRelativeTo(playerTransform, camTransform, 0f, 2.0f, -4.0f);
            return "SUCCESS|THIRD_PERSON";
        }

        private static void CaptureCameraOriginal(object cam)
        {
            if (_cameraOriginalPosition != null)
                return;

            try
            {
                var transform = GetTransform(cam);
                if (transform == null)
                    return;

                var t = transform.GetType();
                var posProp = t.GetProperty("position", BindingFlags.Public | BindingFlags.Instance);
                var rotProp = t.GetProperty("rotation", BindingFlags.Public | BindingFlags.Instance);
                _cameraOriginalPosition = posProp?.GetValueCompat(transform);
                _cameraOriginalRotation = rotProp?.GetValueCompat(transform);

                var parentProp = t.GetProperty("parent", BindingFlags.Public | BindingFlags.Instance);
                _cameraOriginalParent = parentProp?.GetValueCompat(transform);
            }
            catch
            {
            }
        }

        private static void RestoreCameraTransform(object cam)
        {
            try
            {
                var transform = GetTransform(cam);
                if (transform == null)
                    return;

                var t = transform.GetType();
                var posProp = t.GetProperty("position", BindingFlags.Public | BindingFlags.Instance);
                var rotProp = t.GetProperty("rotation", BindingFlags.Public | BindingFlags.Instance);
                var parentProp = t.GetProperty("parent", BindingFlags.Public | BindingFlags.Instance);

                if (_cameraOriginalParent != null && parentProp != null && parentProp.CanWrite)
                    parentProp.SetValueCompat(transform, _cameraOriginalParent);
                if (_cameraOriginalPosition != null && posProp != null && posProp.CanWrite)
                    posProp.SetValueCompat(transform, _cameraOriginalPosition);
                if (_cameraOriginalRotation != null && rotProp != null && rotProp.CanWrite)
                    rotProp.SetValueCompat(transform, _cameraOriginalRotation);
            }
            catch
            {
            }
        }

        private static object GetTransform(object unityObject)
        {
            try
            {
                var t = unityObject.GetType();
                var transformProp = t.GetProperty("transform", BindingFlags.Public | BindingFlags.Instance);
                return transformProp?.GetValueCompat(unityObject);
            }
            catch
            {
                return null;
            }
        }

        private struct Vec3
        {
            public float X;
            public float Y;
            public float Z;

            public Vec3(float x, float y, float z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }

        private static void SetTransformPositionRelativeTo(object anchorTransform, object targetTransform, float x, float y, float z)
        {
            try
            {
                var vector3Type = FindUnityType("UnityEngine.Vector3");
                if (vector3Type == null)
                    return;

                var anchorType = anchorTransform.GetType();
                var posProp = anchorType.GetProperty("position", BindingFlags.Public | BindingFlags.Instance);
                var anchorPos = posProp?.GetValueCompat(anchorTransform);
                if (anchorPos == null)
                    return;

                var a = ReadVector3(anchorPos);
                var newPos = Activator.CreateInstance(vector3Type, new object[] { a.X + x, a.Y + y, a.Z + z });

                var targetType = targetTransform.GetType();
                var targetPosProp = targetType.GetProperty("position", BindingFlags.Public | BindingFlags.Instance);
                if (targetPosProp != null && targetPosProp.CanWrite)
                    targetPosProp.SetValueCompat(targetTransform, newPos);
            }
            catch
            {
            }
        }

        private static Vec3 ReadVector3(object vec)
        {
            try
            {
                var t = vec.GetType();
                var x = Convert.ToSingle(t.GetField("x")?.GetValue(vec) ?? t.GetProperty("x")?.GetValueCompat(vec));
                var y = Convert.ToSingle(t.GetField("y")?.GetValue(vec) ?? t.GetProperty("y")?.GetValueCompat(vec));
                var z = Convert.ToSingle(t.GetField("z")?.GetValue(vec) ?? t.GetProperty("z")?.GetValueCompat(vec));
                return new Vec3(x, y, z);
            }
            catch
            {
                return new Vec3(0f, 0f, 0f);
            }
        }

        private static string SpawnEntity(string args)
        {
            try
            {
                var goType = FindUnityType("UnityEngine.GameObject");
                if (goType == null)
                    return "ERROR|GameObject not found";

                var vector3Type = FindUnityType("UnityEngine.Vector3");
                if (vector3Type == null)
                    return "ERROR|Vector3 not found";

                var mode = "PRIMITIVE";
                var kindOrFirst = "Cube";
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (!IsNullOrWhiteSpace(args))
                {
                    if (args.Contains("|"))
                    {
                        var parts = args.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0)
                        {
                            mode = parts[0];
                            if (parts.Length > 1 && parts[1].IndexOf('=') < 0)
                                kindOrFirst = parts[1];
                        }
                        for (var i = 1; i < parts.Length; i++)
                        {
                            var p = parts[i];
                            var eq = p.IndexOf('=');
                            if (eq <= 0)
                                continue;
                            dict[p.Substring(0, eq)] = p.Substring(eq + 1);
                        }
                    }
                    else
                    {
                        var tokens = args.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length > 0)
                            kindOrFirst = tokens[0];
                        if (tokens.Length >= 4)
                        {
                            dict["pos"] = tokens[1] + "," + tokens[2] + "," + tokens[3];
                        }
                    }
                }

                if (!mode.Equals("PRIMITIVE", StringComparison.OrdinalIgnoreCase) &&
                    !mode.Equals("CLONE", StringComparison.OrdinalIgnoreCase) &&
                    !mode.Equals("RESOURCE", StringComparison.OrdinalIgnoreCase) &&
                    !mode.Equals("ADDRESSABLE", StringComparison.OrdinalIgnoreCase) &&
                    !mode.Equals("COMPONENT", StringComparison.OrdinalIgnoreCase) &&
                    !mode.Equals("FACTORY", StringComparison.OrdinalIgnoreCase))
                {
                    dict["kind"] = mode;
                    mode = "PRIMITIVE";
                }

                var parentId = dict.TryGetValue("parent", out var p1) ? p1 : (dict.TryGetValue("parentId", out var p2) ? p2 : null);
                var name = dict.TryGetValue("name", out var n1) ? n1 : null;
                var tag = dict.TryGetValue("tag", out var tag1) ? tag1 : null;
                var layerText = dict.TryGetValue("layer", out var layer1) ? layer1 : null;

                var spawnObj = CreateSpawnObject(mode, kindOrFirst, dict);
                if (spawnObj == null)
                    return "ERROR|Spawn failed";

                var go = TryGetGameObject(spawnObj) ?? spawnObj;
                var transform = GetTransform(go);
                if (transform == null)
                    return "ERROR|Transform not found";

                var pos = TryParseVector3(dict, "pos", vector3Type) ?? ComputeDefaultSpawnPosition(vector3Type);
                ApplyTransformPosition(transform, pos);

                var rot = TryParseVector3(dict, "rot", vector3Type) ?? TryParseVector3(dict, "euler", vector3Type);
                if (rot != null)
                    ApplyTransformEuler(transform, rot);

                var scale = TryParseVector3(dict, "scale", vector3Type);
                if (scale != null)
                    ApplyTransformLocalScale(transform, scale);

                if (!IsNullOrWhiteSpace(parentId) && !parentId.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    var parentObj = ResolveTrackedObject(parentId);
                    var parentGo = parentObj != null ? (TryGetGameObject(parentObj) ?? parentObj) : null;
                    if (parentGo != null && TryGetTransform(parentGo, out var parentTransform) && parentTransform != null)
                    {
                        var t = transform.GetType();
                        var setParent = GetMethodCached(t, "SetParent", new[] { t, typeof(bool) }) ?? GetMethodCached(t, "SetParent", new[] { t });
                        if (setParent != null)
                        {
                            if (setParent.GetParameters().Length == 2)
                                setParent.Invoke(transform, new object[] { parentTransform, true });
                            else
                                setParent.Invoke(transform, new object[] { parentTransform });
                        }
                    }
                }

                if (!IsNullOrWhiteSpace(name))
                    SetObjectName(TrackObject(go).ToString(), name);
                if (!IsNullOrWhiteSpace(tag))
                    SetObjectTag(TrackObject(go).ToString(), tag);
                if (!IsNullOrWhiteSpace(layerText))
                    SetObjectLayer(TrackObject(go).ToString(), layerText);

                var id = TrackObject(go);
                return $"SUCCESS|id={id}|mode={mode}|name={(GetUnityObjectName(go) ?? "")}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string FindFactoryMethods(string search)
        {
            try
            {
                EnsureFactoryIndex();

                var term = search ?? string.Empty;
                term = term.Trim();

                var matches = new List<MethodInfo>();
                lock (_factoryRegistryLock)
                {
                    foreach (var m in _factoryCandidates)
                    {
                        if (m == null)
                            continue;

                        if (!IsFactoryLikeMethodName(m.Name))
                            continue;

                        if (!IsNullOrWhiteSpace(term))
                        {
                            var full = (m.DeclaringType?.FullName ?? "") + "." + m.Name;
                            if (full.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                        }

                        matches.Add(m);
                        if (matches.Count >= 250)
                            break;
                    }
                }

                return BuildFactoryMethodsResponse(matches, null);
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string FindInstanceFactoryMethods(string instanceId, string search)
        {
            try
            {
                var target = ResolveTrackedObject(instanceId);
                if (target == null)
                    return $"ERROR|Instance not found: {instanceId}";

                var t = target.GetType();
                var term = search ?? "";

                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => IsFactoryLikeMethodName(m.Name))
                    .Where(m =>
                    {
                        if (IsNullOrWhiteSpace(term))
                            return true;
                        var full = (t.FullName ?? "") + "." + m.Name;
                        return full.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
                    })
                    .Take(200)
                    .ToList();

                return BuildFactoryMethodsResponse(methods, target);
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string BuildFactoryMethodsResponse(List<MethodInfo> methods, object instanceTarget)
        {
            var sb = new StringBuilder();
            sb.Append("FACTORY_METHODS|count=");
            sb.Append(methods?.Count ?? 0);

            if (instanceTarget != null)
            {
                sb.Append("|instanceId=");
                sb.Append(TrackObject(instanceTarget));
            }

            if (methods == null)
                return sb.ToString();

            for (var i = 0; i < methods.Count; i++)
            {
                var m = methods[i];
                if (m == null)
                    continue;

                var id = RegisterFactoryMethod(m, instanceTarget);
                var decl = m.DeclaringType?.FullName ?? m.DeclaringType?.Name ?? "UnknownType";
                var ret = m.ReturnType?.FullName ?? m.ReturnType?.Name ?? "void";
                var ps = m.GetParameters();

                var paramSig = new StringBuilder();
                for (var p = 0; p < ps.Length; p++)
                {
                    if (p > 0)
                        paramSig.Append(',');
                    paramSig.Append(ps[p].ParameterType.FullName ?? ps[p].ParameterType.Name);
                    paramSig.Append(' ');
                    paramSig.Append(ps[p].Name);
                }

                sb.Append("|id=");
                sb.Append(id);
                sb.Append(";type=");
                sb.Append(decl.Replace("|", " ").Replace(";", " "));
                sb.Append(";method=");
                sb.Append(m.Name.Replace("|", " ").Replace(";", " "));
                sb.Append(";static=");
                sb.Append(m.IsStatic ? "1" : "0");
                sb.Append(";ret=");
                sb.Append(ret.Replace("|", " ").Replace(";", " "));
                sb.Append(";params=");
                sb.Append(paramSig.ToString().Replace("|", " ").Replace(";", " "));
            }

            return sb.ToString();
        }

        private static int RegisterFactoryMethod(MethodInfo method, object target)
        {
            lock (_factoryRegistryLock)
            {
                if (_factoryMethodRegistry.Count > 5000)
                    _factoryMethodRegistry.Clear();

                var id = _nextFactoryMethodId++;
                _factoryMethodRegistry[id] = new FactoryMethodEntry
                {
                    Method = method,
                    Target = target != null ? new WeakReference(target) : null
                };
                return id;
            }
        }

        private static void EnsureFactoryIndex()
        {
            var now = Stopwatch.GetTimestamp();
            lock (_factoryRegistryLock)
            {
                if (_factoryIndexTicks != 0 && (now - _factoryIndexTicks) < Stopwatch.Frequency * 30)
                    return;

                _factoryCandidates.Clear();

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var n = asm?.GetName()?.Name ?? "";
                        if (!(n.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) ||
                              n.Equals("Assembly-CSharp-firstpass", StringComparison.OrdinalIgnoreCase)))
                            continue;

                        foreach (var t in asm.GetTypes())
                        {
                            if (t == null)
                                continue;
                            if (t.IsGenericTypeDefinition)
                                continue;

                            MethodInfo[] methods;
                            try
                            {
                                methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                            }
                            catch
                            {
                                continue;
                            }

                            for (var i = 0; i < methods.Length; i++)
                            {
                                var m = methods[i];
                                if (m == null)
                                    continue;
                                if (m.IsGenericMethodDefinition)
                                    continue;
                                _factoryCandidates.Add(m);
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                _factoryIndexTicks = now;
            }
        }

        private static bool IsFactoryLikeMethodName(string name)
        {
            if (IsNullOrWhiteSpace(name))
                return false;

            return name.StartsWith("Spawn", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Create", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Instantiate", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("AddItem", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Drop", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Give", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Generate", StringComparison.OrdinalIgnoreCase);
        }

        private static string InvokeFactoryB64(string[] parts)
        {
            try
            {
                if (parts == null || parts.Length < 2)
                    return "ERROR|Invalid invoke factory format";

                if (!int.TryParse(parts[1], out var methodId))
                    return "ERROR|Invalid method id";

                FactoryMethodEntry entry;
                lock (_factoryRegistryLock)
                {
                    if (!_factoryMethodRegistry.TryGetValue(methodId, out entry) || entry?.Method == null)
                        return "ERROR|Method not found";
                }

                object target = null;
                if (!entry.Method.IsStatic)
                {
                    if (entry.Target == null || !entry.Target.IsAlive)
                        return "ERROR|Target instance is not available";
                    target = entry.Target.Target;
                    if (target == null)
                        return "ERROR|Target instance is not available";
                }

                var ps = entry.Method.GetParameters();
                var argCount = parts.Length - 2;
                if (ps.Length != argCount)
                    return $"ERROR|Arity mismatch: expected {ps.Length}, got {argCount}";

                var args = new object[ps.Length];
                for (var i = 0; i < ps.Length; i++)
                {
                    var raw = DecodeB64(parts[i + 2]);
                    args[i] = ConvertStringToArgument(raw, ps[i].ParameterType);
                }

                var result = entry.Method.Invoke(target, args);
                if (result == null)
                    return "SUCCESS|result=null";

                var unityObjectType = FindUnityType("UnityEngine.Object");
                if (unityObjectType != null && unityObjectType.IsInstanceOfType(result))
                {
                    var id = TrackObject(result);
                    return $"SUCCESS|id={id}|type={result.GetType().FullName}|name={(GetUnityObjectName(result) ?? "")}";
                }

                return $"SUCCESS|result={FormatValue(result)}|type={result.GetType().FullName}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DecodeB64(string b64)
        {
            if (b64 == null)
                return "";
            try
            {
                var bytes = Convert.FromBase64String(b64);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return "";
            }
        }

        private static object ConvertStringToArgument(string text, Type targetType)
        {
            if (targetType == null)
                return null;

            if (targetType == typeof(string))
                return text ?? "";

            if (text != null && int.TryParse(text.Trim(), out var id))
            {
                var obj = FindObjectInstance(id);
                if (obj != null && targetType.IsInstanceOfType(obj))
                    return obj;
                var go = TryGetGameObject(obj);
                if (go != null && targetType.IsInstanceOfType(go))
                    return go;
            }

            if (targetType.IsEnum)
            {
                try { return Enum.Parse(targetType, text ?? "", true); }
                catch { return Activator.CreateInstance(targetType); }
            }

            return ConvertStringToType(text ?? "", targetType);
        }

        private static object CreateSpawnObject(string mode, string kindOrFirst, Dictionary<string, string> dict)
        {
            if (mode.Equals("PRIMITIVE", StringComparison.OrdinalIgnoreCase))
            {
                var goType = FindUnityType("UnityEngine.GameObject");
                var primitiveType = FindUnityType("UnityEngine.PrimitiveType");
                if (goType == null || primitiveType == null)
                    return null;
                var createPrimitive = goType.GetMethod("CreatePrimitive", BindingFlags.Public | BindingFlags.Static, null, new[] { primitiveType }, null);
                if (createPrimitive == null)
                    return null;

                var kind = dict.TryGetValue("kind", out var k) ? k : kindOrFirst;
                object primitiveEnum;
                try { primitiveEnum = Enum.Parse(primitiveType, kind, true); }
                catch { primitiveEnum = Enum.Parse(primitiveType, "Cube", true); }
                return createPrimitive.Invoke(null, new[] { primitiveEnum });
            }

            if (mode.Equals("CLONE", StringComparison.OrdinalIgnoreCase))
            {
                var id = dict.TryGetValue("id", out var id1) ? id1 : kindOrFirst;
                var source = ResolveTrackedObject(id);
                if (source == null)
                    return null;

                var unityObjectType = FindUnityType("UnityEngine.Object");
                if (unityObjectType == null || !unityObjectType.IsInstanceOfType(source))
                {
                    var go = TryGetGameObject(source);
                    if (go == null)
                        return null;
                    source = go;
                }

                var instantiate = GetMethodCached(unityObjectType, "Instantiate", new[] { unityObjectType });
                if (instantiate == null)
                    return null;
                return instantiate.Invoke(null, new[] { source });
            }

            if (mode.Equals("RESOURCE", StringComparison.OrdinalIgnoreCase))
            {
                var path = dict.TryGetValue("path", out var p) ? p : kindOrFirst;
                if (IsNullOrWhiteSpace(path))
                    return null;

                var resourcesType = FindUnityType("UnityEngine.Resources");
                var unityObjectType = FindUnityType("UnityEngine.Object");
                if (resourcesType == null || unityObjectType == null)
                    return null;

                var load = resourcesType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Type) }, null)
                           ?? resourcesType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (load == null)
                    return null;

                object prefab = load.GetParameters().Length == 2
                    ? load.Invoke(null, new object[] { path, unityObjectType })
                    : load.Invoke(null, new object[] { path });

                if (prefab == null)
                    return null;

                var instantiate = GetMethodCached(unityObjectType, "Instantiate", new[] { unityObjectType });
                if (instantiate == null)
                    return null;
                return instantiate.Invoke(null, new[] { prefab });
            }

            if (mode.Equals("ADDRESSABLE", StringComparison.OrdinalIgnoreCase))
            {
                var key = dict.TryGetValue("key", out var k) ? k : kindOrFirst;
                if (IsNullOrWhiteSpace(key))
                    return null;

                var addressables = FindUnityType("UnityEngine.AddressableAssets.Addressables");
                if (addressables == null)
                    return null;

                var instantiateAsync = addressables.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "InstantiateAsync" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(object))
                    ?? addressables.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "InstantiateAsync" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(string));
                if (instantiateAsync == null)
                    return null;

                object handle = null;
                var ps = instantiateAsync.GetParameters();
                if (ps[0].ParameterType == typeof(string))
                    handle = instantiateAsync.Invoke(null, new object[] { key });
                else
                    handle = instantiateAsync.Invoke(null, new object[] { (object)key });

                if (handle == null)
                    return null;

                var wait = handle.GetType().GetMethod("WaitForCompletion", BindingFlags.Public | BindingFlags.Instance);
                if (wait != null)
                {
                    try
                    {
                        return wait.Invoke(handle, null);
                    }
                    catch
                    {
                        return null;
                    }
                }

                var resultProp = handle.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
                return resultProp?.GetValueCompat(handle);
            }

            if (mode.Equals("COMPONENT", StringComparison.OrdinalIgnoreCase))
            {
                var typeName = dict.TryGetValue("type", out var t) ? t : kindOrFirst;
                if (IsNullOrWhiteSpace(typeName))
                    return null;
                var componentType = ResolveTypeByName(typeName);
                if (componentType == null)
                    return null;

                var goType = FindUnityType("UnityEngine.GameObject");
                if (goType == null)
                    return null;
                var ctor = goType.GetConstructor(new[] { typeof(string) });
                var go = ctor != null ? ctor.Invoke(new object[] { componentType.Name }) : Activator.CreateInstance(goType);
                if (go == null)
                    return null;

                var add = GetMethodCached(go.GetType(), "AddComponent", new[] { typeof(Type) });
                if (add != null)
                    add.Invoke(go, new object[] { componentType });
                return go;
            }

            if (mode.Equals("FACTORY", StringComparison.OrdinalIgnoreCase))
            {
                var spec = dict.TryGetValue("spec", out var s) ? s : kindOrFirst;
                if (IsNullOrWhiteSpace(spec))
                    return null;

                string typeName = null;
                string methodName = null;

                if (spec.Contains("::"))
                {
                    var idx = spec.IndexOf("::", StringComparison.Ordinal);
                    if (idx > 0)
                    {
                        typeName = spec.Substring(0, idx);
                        methodName = spec.Substring(idx + 2);
                    }
                }
                else
                {
                    dict.TryGetValue("type", out typeName);
                    dict.TryGetValue("method", out methodName);
                }

                if (IsNullOrWhiteSpace(typeName) || IsNullOrWhiteSpace(methodName))
                    return null;

                var type = ResolveTypeByName(typeName);
                if (type == null)
                    return null;

                var argsText = dict.TryGetValue("args", out var a) ? a : (dict.TryGetValue("params", out var p) ? p : "");
                var tokens = ParseCsvArgs(argsText);

                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                MethodInfo best = null;
                object[] bestArgs = null;

                foreach (var mi in methods)
                {
                    var ps = mi.GetParameters();
                    if (ps.Length != tokens.Count)
                        continue;

                    var converted = new object[ps.Length];
                    var ok = true;
                    for (var i = 0; i < ps.Length; i++)
                    {
                        try
                        {
                            converted[i] = ConvertStringToType(tokens[i], ps[i].ParameterType);
                        }
                        catch
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (!ok)
                        continue;

                    best = mi;
                    bestArgs = converted;
                    break;
                }

                if (best == null)
                    return null;

                var result = best.Invoke(null, bestArgs);
                return result;
            }

            return null;
        }

        private static List<string> ParseCsvArgs(string csv)
        {
            var list = new List<string>();
            if (IsNullOrWhiteSpace(csv))
                return list;
            var parts = csv.Split(new[] { ',' }, StringSplitOptions.None);
            for (var i = 0; i < parts.Length; i++)
            {
                var t = parts[i].Trim();
                if (t.Length == 0)
                    continue;
                list.Add(t);
            }
            return list;
        }

        private static object TryParseVector3(Dictionary<string, string> dict, string key, Type vector3Type)
        {
            if (!dict.TryGetValue(key, out var text))
                return null;
            if (IsNullOrWhiteSpace(text) || text.Equals("null", StringComparison.OrdinalIgnoreCase))
                return null;
            return ConvertStringToType(text, vector3Type);
        }

        private static object ComputeDefaultSpawnPosition(Type vector3Type)
        {
            try
            {
                var cam = GetMainCamera();
                if (cam != null)
                {
                    var ct = GetTransform(cam);
                    if (ct != null)
                    {
                        var tType = ct.GetType();
                        var posProp = GetPropertyCached(tType, "position");
                        var fwdProp = GetPropertyCached(tType, "forward");
                        var ppos = posProp?.GetValueCompat(ct);
                        var pfwd = fwdProp?.GetValueCompat(ct);
                        if (ppos != null && pfwd != null)
                        {
                            var p = ReadVector3(ppos);
                            var f = ReadVector3(pfwd);
                            return Activator.CreateInstance(vector3Type, new object[] { p.X + f.X * 2f, p.Y + f.Y * 2f, p.Z + f.Z * 2f });
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                var player = FindPlayerGameObject();
                var pt = player != null ? GetTransform(player) : null;
                if (pt != null)
                {
                    var ptType = pt.GetType();
                    var posProp = GetPropertyCached(ptType, "position");
                    var ppos = posProp?.GetValueCompat(pt);
                    if (ppos != null)
                    {
                        var p = ReadVector3(ppos);
                        return Activator.CreateInstance(vector3Type, new object[] { p.X + 2f, p.Y + 1f, p.Z });
                    }
                }
            }
            catch
            {
            }

            return Activator.CreateInstance(vector3Type, new object[] { 0f, 0f, 0f });
        }

        private static void ApplyTransformPosition(object transform, object pos)
        {
            try
            {
                var tType = transform.GetType();
                var posProp = GetPropertyCached(tType, "position");
                if (posProp != null && posProp.CanWrite)
                    posProp.SetValueCompat(transform, pos);
            }
            catch
            {
            }
        }

        private static void ApplyTransformEuler(object transform, object euler)
        {
            try
            {
                var tType = transform.GetType();
                var rotProp = GetPropertyCached(tType, "eulerAngles");
                if (rotProp != null && rotProp.CanWrite)
                    rotProp.SetValueCompat(transform, euler);
            }
            catch
            {
            }
        }

        private static void ApplyTransformLocalScale(object transform, object scale)
        {
            try
            {
                var tType = transform.GetType();
                var scaleProp = GetPropertyCached(tType, "localScale");
                if (scaleProp != null && scaleProp.CanWrite)
                    scaleProp.SetValueCompat(transform, scale);
            }
            catch
            {
            }
        }

        private static void EnsureHealthBinding()
        {
            if (_healthMember != null && _healthMember.IsValid)
                return;
            _healthMember = DiscoverNumericMemberOnPlayer(
                new[] { "Health", "health", "HP", "hp", "HitPoints", "hitPoints", "CurrentHealth", "currentHealth", "MaxHealth", "maxHealth" },
                new[] { "health", "hp", "player", "character", "stats" });
            if (_healthMember != null && _healthMember.IsValid)
                return;

            UiValueObservation uiObs = null;
            TryGetUiObservation("health", out uiObs);
            var now = Stopwatch.GetTimestamp();
            if (uiObs != null && uiObs.Confidence > 0 && (_lastHealthSlowScanTicks == 0 || (now - _lastHealthSlowScanTicks) > Stopwatch.Frequency * 5))
            {
                _lastHealthSlowScanTicks = now;
                var byUi = DiscoverNumericMemberByUiValue("health", uiObs);
                if (byUi != null && byUi.IsValid)
                {
                    _healthMember = byUi;
                    return;
                }
            }

            _healthMember = DiscoverNumericMemberFromTelemetry("health", uiObs);
        }

        private static void EnsureAmmoBinding()
        {
            if (_ammoMember != null && _ammoMember.IsValid)
                return;
            _ammoMember = DiscoverNumericMemberOnPlayer(
                new[] { "Ammo", "ammo", "Bullets", "bullets", "Clip", "clip", "Magazine", "magazine", "CurrentAmmo", "currentAmmo", "AmmoInClip", "ammoInClip", "ReserveAmmo", "reserveAmmo" },
                new[] { "weapon", "gun", "ammo", "shoot", "bullet" });
            if (_ammoMember != null && _ammoMember.IsValid)
                return;

            UiValueObservation uiObs = null;
            TryGetUiObservation("ammo", out uiObs);
            var now = Stopwatch.GetTimestamp();
            if (uiObs != null && uiObs.Confidence > 0 && (_lastAmmoValueScanTicks == 0 || (now - _lastAmmoValueScanTicks) > Stopwatch.Frequency * 5))
            {
                _lastAmmoValueScanTicks = now;
                var byUi = DiscoverNumericMemberByUiValue("ammo", uiObs);
                if (byUi != null && byUi.IsValid)
                {
                    _ammoMember = byUi;
                    return;
                }
            }

            _ammoMember = DiscoverNumericMemberFromTelemetry("ammo", uiObs);
        }

        private static void EnsureMoneyBinding()
        {
            if (_moneyMember != null && _moneyMember.IsValid)
                return;

            var now = Stopwatch.GetTimestamp();
            if (_lastMoneySearchTicks != 0 && (now - _lastMoneySearchTicks) < Stopwatch.Frequency * 5)
                return;
            _lastMoneySearchTicks = now;

            _moneyMember = DiscoverNumericMemberInScene(
                new[] { "Money", "money", "Coins", "coins", "Currency", "currency", "Gold", "gold", "Cash", "cash", "Credits", "credits", "Wallet", "wallet" },
                new[] { "money", "currency", "wallet", "profile", "save", "player", "economy", "inventory" });
            if (_moneyMember != null && _moneyMember.IsValid)
                return;

            UiValueObservation uiObs = null;
            TryGetUiObservation("money", out uiObs);
            var byUi = DiscoverNumericMemberByUiValue("money", uiObs);
            if (byUi != null && byUi.IsValid)
            {
                _moneyMember = byUi;
                return;
            }

            _moneyMember = DiscoverNumericMemberFromTelemetry("money", uiObs);
        }

        private static void EnsureXpBinding()
        {
            if (_xpMember != null && _xpMember.IsValid)
                return;

            var now = Stopwatch.GetTimestamp();
            if (_lastXpSearchTicks != 0 && (now - _lastXpSearchTicks) < Stopwatch.Frequency * 5)
                return;
            _lastXpSearchTicks = now;

            _xpMember = DiscoverNumericMemberInScene(
                new[] { "XP", "xp", "Exp", "exp", "Experience", "experience", "ExperiencePoints", "experiencePoints" },
                new[] { "xp", "experience", "level", "profile", "player", "progress" });
            if (_xpMember != null && _xpMember.IsValid)
                return;

            UiValueObservation uiObs = null;
            TryGetUiObservation("xp", out uiObs);
            var byUi = DiscoverNumericMemberByUiValue("xp", uiObs);
            if (byUi != null && byUi.IsValid)
            {
                _xpMember = byUi;
                return;
            }

            _xpMember = DiscoverNumericMemberFromTelemetry("xp", uiObs);
        }

        private static void EnsureStaminaBinding()
        {
            if (_staminaMember != null && _staminaMember.IsValid)
                return;

            var now = Stopwatch.GetTimestamp();
            if (_lastStaminaSearchTicks != 0 && (now - _lastStaminaSearchTicks) < Stopwatch.Frequency * 5)
                return;
            _lastStaminaSearchTicks = now;

            _staminaMember = DiscoverNumericMemberOnPlayer(
                new[] { "Stamina", "stamina", "Energy", "energy", "Sprint", "sprint", "Stam", "stam" },
                new[] { "stamina", "energy", "sprint", "player", "character", "stats" });
            if (_staminaMember != null && _staminaMember.IsValid)
                return;

            UiValueObservation uiObs = null;
            TryGetUiObservation("stamina", out uiObs);
            var byUi = DiscoverNumericMemberByUiValue("stamina", uiObs);
            if (byUi != null && byUi.IsValid)
            {
                _staminaMember = byUi;
                return;
            }

            _staminaMember = DiscoverNumericMemberFromTelemetry("stamina", uiObs);
        }

        private static long _lastUiObservationTicks = 0;
        private static UiValueObservation _cachedUiHealth;
        private static UiValueObservation _cachedUiAmmo;
        private static UiValueObservation _cachedUiMoney;
        private static UiValueObservation _cachedUiXp;
        private static UiValueObservation _cachedUiStamina;

        private static void UpdateTelemetrySampler()
        {
            try
            {
                var now = Stopwatch.GetTimestamp();
                if (_telemetryComponents == null || _telemetryComponentsRefreshTicks == 0 || (now - _telemetryComponentsRefreshTicks) > Stopwatch.Frequency * 2)
                {
                    var componentType = FindUnityType("UnityEngine.Component");
                    if (componentType != null)
                        _telemetryComponents = FindUnityObjectsOfType(componentType);
                    else
                        _telemetryComponents = null;
                    _telemetryComponentIndex = 0;
                    _telemetryComponentsRefreshTicks = now;
                }

                if (_telemetryComponents == null)
                    return;

                var count = _telemetryComponents.Length;
                if (count == 0)
                    return;

                var perFrame = 28;
                var seen = new HashSet<int>();
                var budget = 2600;
                for (var i = 0; i < perFrame && budget > 0; i++)
                {
                    if (_telemetryComponentIndex >= count)
                        _telemetryComponentIndex = 0;
                    var comp = _telemetryComponents.GetValue(_telemetryComponentIndex++);
                    if (comp == null)
                        continue;
                    if (!IsUnityObjectAlive(comp))
                        continue;
                    var typeName = comp.GetType().FullName ?? comp.GetType().Name ?? "";
                    if (IsUiTypeName(typeName))
                        continue;
                    SampleTelemetryRecursive(comp, comp, 1, seen, ref budget, now);
                }

                if (_telemetryPruneTicks == 0 || (now - _telemetryPruneTicks) > Stopwatch.Frequency * 5)
                {
                    PruneTelemetry(now);
                    _telemetryPruneTicks = now;
                }
            }
            catch
            {
            }
        }

        private static void PruneTelemetry(long nowTicks)
        {
            lock (_telemetryLock)
            {
                if (_telemetry.Count == 0)
                    return;

                var remove = new List<TelemetryKey>();
                foreach (var kv in _telemetry)
                {
                    var e = kv.Value;
                    if (e == null)
                    {
                        remove.Add(kv.Key);
                        continue;
                    }

                    if (e.Target == null || e.Target.Target == null)
                    {
                        remove.Add(kv.Key);
                        continue;
                    }

                    if ((nowTicks - e.LastSeenTicks) > Stopwatch.Frequency * 30)
                    {
                        remove.Add(kv.Key);
                        continue;
                    }
                }

                for (var i = 0; i < remove.Count; i++)
                    _telemetry.Remove(remove[i]);

                if (_telemetry.Count > 9000)
                {
                    var keys = _telemetry.Keys.ToArray();
                    for (var i = 0; i < keys.Length && _telemetry.Count > 9000; i++)
                        _telemetry.Remove(keys[i]);
                }
            }
        }

        private static void SampleTelemetryRecursive(object root, object target, int depth, HashSet<int> seen, ref int budget, long nowTicks)
        {
            if (target == null || budget <= 0)
                return;

            budget--;

            var t = target.GetType();
            if (t == typeof(string))
                return;

            if (!t.IsValueType)
            {
                var key = RuntimeHelpers.GetHashCode(target);
                if (seen != null)
                {
                    if (seen.Contains(key))
                        return;
                    seen.Add(key);
                }
            }

            try
            {
                var fields = GetCachedNumericFields(t);
                for (var i = 0; i < fields.Length && budget > 0; i++)
                {
                    var f = fields[i];
                    if (f == null || f.IsLiteral)
                        continue;
                    object raw = null;
                    try { raw = f.GetValue(target); } catch { raw = null; }
                    if (!TryConvertNumericToDouble(raw, out var v))
                        continue;
                    UpdateTelemetryEntry(root, target, f, f.FieldType, true, v, nowTicks);
                }
            }
            catch
            {
            }

            try
            {
                var props = GetCachedNumericProps(t);
                for (var i = 0; i < props.Length && budget > 0; i++)
                {
                    var p = props[i];
                    if (p == null || !p.CanRead)
                        continue;
                    var idx = p.GetIndexParameters();
                    if (idx != null && idx.Length != 0)
                        continue;
                    object raw = null;
                    try { raw = p.GetValueCompat(target); } catch { raw = null; }
                    if (!TryConvertNumericToDouble(raw, out var v))
                        continue;
                    UpdateTelemetryEntry(root, target, p, p.PropertyType, p.CanWrite, v, nowTicks);
                }
            }
            catch
            {
            }

            if (depth <= 0 || budget <= 0)
                return;

            try
            {
                var fields = GetCachedRefFields(t);
                for (var i = 0; i < fields.Length && budget > 0; i++)
                {
                    var f = fields[i];
                    if (f == null || f.IsLiteral)
                        continue;
                    object child = null;
                    try { child = f.GetValue(target); } catch { child = null; }
                    if (child == null)
                        continue;
                    SampleTelemetryRecursive(root, child, depth - 1, seen, ref budget, nowTicks);
                }
            }
            catch
            {
            }

            try
            {
                var props = GetCachedRefProps(t);
                for (var i = 0; i < props.Length && budget > 0; i++)
                {
                    var p = props[i];
                    if (p == null || !p.CanRead)
                        continue;
                    var idx = p.GetIndexParameters();
                    if (idx != null && idx.Length != 0)
                        continue;
                    object child = null;
                    try { child = p.GetValueCompat(target); } catch { child = null; }
                    if (child == null)
                        continue;
                    SampleTelemetryRecursive(root, child, depth - 1, seen, ref budget, nowTicks);
                }
            }
            catch
            {
            }
        }

        private static void UpdateTelemetryEntry(object root, object target, MemberInfo member, Type valueType, bool canWrite, double value, long nowTicks)
        {
            if (target == null || member == null || valueType == null)
                return;

            var key = new TelemetryKey { TargetHash = RuntimeHelpers.GetHashCode(target), Member = member };

            lock (_telemetryLock)
            {
                if (!_telemetry.TryGetValue(key, out var entry) || entry == null)
                {
                    entry = new NumericTelemetry
                    {
                        Target = new WeakReference(target),
                        Root = root != null ? new WeakReference(root) : null,
                        Member = member,
                        ValueType = valueType,
                        CanWrite = canWrite,
                        LastValue = value,
                        MinValue = value,
                        MaxValue = value,
                        Samples = 1,
                        Changes = 0,
                        Increases = 0,
                        Decreases = 0,
                        AbsDeltaSum = 0,
                        LastSeenTicks = nowTicks,
                        LastChangedTicks = 0
                    };
                    _telemetry[key] = entry;
                    return;
                }

                entry.LastSeenTicks = nowTicks;
                entry.Samples++;
                if (value < entry.MinValue) entry.MinValue = value;
                if (value > entry.MaxValue) entry.MaxValue = value;

                var delta = value - entry.LastValue;
                if (delta != 0)
                {
                    entry.Changes++;
                    entry.AbsDeltaSum += Math.Abs(delta);
                    if (delta > 0) entry.Increases++;
                    if (delta < 0) entry.Decreases++;
                    entry.LastChangedTicks = nowTicks;
                }
                entry.LastValue = value;
                entry.CanWrite = entry.CanWrite || canWrite;
            }
        }

        private static FieldInfo[] GetCachedNumericFields(Type t)
        {
            lock (_numericMemberCacheLock)
            {
                if (_numericFieldsCache.TryGetValue(t, out var cached) && cached != null)
                    return cached;
            }

            FieldInfo[] fields = null;
            try { fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); } catch { fields = null; }
            if (fields == null)
                fields = new FieldInfo[0];

            var list = new List<FieldInfo>();
            for (var i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                if (f == null || f.IsLiteral)
                    continue;
                if (!TryGetNumericType(f.FieldType))
                    continue;
                list.Add(f);
            }

            var arr = list.ToArray();
            lock (_numericMemberCacheLock)
                _numericFieldsCache[t] = arr;
            return arr;
        }

        private static PropertyInfo[] GetCachedNumericProps(Type t)
        {
            lock (_numericMemberCacheLock)
            {
                if (_numericPropsCache.TryGetValue(t, out var cached) && cached != null)
                    return cached;
            }

            PropertyInfo[] props = null;
            try { props = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); } catch { props = null; }
            if (props == null)
                props = new PropertyInfo[0];

            var list = new List<PropertyInfo>();
            for (var i = 0; i < props.Length; i++)
            {
                var p = props[i];
                if (p == null || !p.CanRead)
                    continue;
                var idx = p.GetIndexParameters();
                if (idx != null && idx.Length != 0)
                    continue;
                if (!TryGetNumericType(p.PropertyType))
                    continue;
                list.Add(p);
            }

            var arr = list.ToArray();
            lock (_numericMemberCacheLock)
                _numericPropsCache[t] = arr;
            return arr;
        }

        private static FieldInfo[] GetCachedRefFields(Type t)
        {
            lock (_numericMemberCacheLock)
            {
                if (_refFieldsCache.TryGetValue(t, out var cached) && cached != null)
                    return cached;
            }

            FieldInfo[] fields = null;
            try { fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); } catch { fields = null; }
            if (fields == null)
                fields = new FieldInfo[0];

            var list = new List<FieldInfo>();
            for (var i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                if (f == null || f.IsLiteral)
                    continue;
                if (!ShouldFollowReferenceType(f.FieldType))
                    continue;
                list.Add(f);
            }

            var arr = list.ToArray();
            lock (_numericMemberCacheLock)
                _refFieldsCache[t] = arr;
            return arr;
        }

        private static PropertyInfo[] GetCachedRefProps(Type t)
        {
            lock (_numericMemberCacheLock)
            {
                if (_refPropsCache.TryGetValue(t, out var cached) && cached != null)
                    return cached;
            }

            PropertyInfo[] props = null;
            try { props = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); } catch { props = null; }
            if (props == null)
                props = new PropertyInfo[0];

            var list = new List<PropertyInfo>();
            for (var i = 0; i < props.Length; i++)
            {
                var p = props[i];
                if (p == null || !p.CanRead)
                    continue;
                var idx = p.GetIndexParameters();
                if (idx != null && idx.Length != 0)
                    continue;
                if (!ShouldFollowReferenceType(p.PropertyType))
                    continue;
                list.Add(p);
            }

            var arr = list.ToArray();
            lock (_numericMemberCacheLock)
                _refPropsCache[t] = arr;
            return arr;
        }

        private static bool IsUiTypeName(string typeName)
        {
            if (IsNullOrWhiteSpace(typeName))
                return false;
            if (typeName.StartsWith("UnityEngine.UI.", StringComparison.Ordinal))
                return true;
            if (typeName.StartsWith("TMPro.", StringComparison.Ordinal))
                return true;
            return false;
        }

        private static bool TryGetUiObservation(string slot, out UiValueObservation observation)
        {
            observation = null;
            try
            {
                if (IsNullOrWhiteSpace(slot))
                    return false;

                var now = Stopwatch.GetTimestamp();
                if (_lastUiObservationTicks == 0 || (now - _lastUiObservationTicks) > Stopwatch.Frequency * 1)
                {
                    _cachedUiHealth = ScanUiForObservation("health");
                    _cachedUiAmmo = ScanUiForObservation("ammo");
                    _cachedUiMoney = ScanUiForObservation("money");
                    _cachedUiXp = ScanUiForObservation("xp");
                    _cachedUiStamina = ScanUiForObservation("stamina");
                    _lastUiObservationTicks = now;
                }

                var s = slot.Trim().ToLowerInvariant();
                observation = s switch
                {
                    "health" => _cachedUiHealth,
                    "ammo" => _cachedUiAmmo,
                    "money" => _cachedUiMoney,
                    "xp" => _cachedUiXp,
                    "stamina" => _cachedUiStamina,
                    _ => null
                };

                return observation != null && observation.Confidence > 0;
            }
            catch
            {
                observation = null;
                return false;
            }
        }

        private static UiValueObservation ScanUiForObservation(string slot)
        {
            if (IsNullOrWhiteSpace(slot))
                return null;

            var best = (UiValueObservation)null;
            var slotLower = slot.Trim().ToLowerInvariant();

            var textType = FindUnityType("UnityEngine.UI.Text");
            if (textType != null)
            {
                var texts = FindUnityObjectsOfType(textType);
                best = SelectBestUiObservationFromObjects(slotLower, texts, best);
            }

            var tmpType = FindUnityType("TMPro.TMP_Text");
            if (tmpType != null)
            {
                var tmps = FindUnityObjectsOfType(tmpType);
                best = SelectBestUiObservationFromObjects(slotLower, tmps, best);
            }

            return best;
        }

        private static UiValueObservation SelectBestUiObservationFromObjects(string slotLower, Array objects, UiValueObservation currentBest)
        {
            if (objects == null || objects.Length == 0)
                return currentBest;

            for (var i = 0; i < objects.Length; i++)
            {
                var obj = objects.GetValue(i);
                if (obj == null)
                    continue;
                if (!IsUnityObjectAlive(obj))
                    continue;
                var typeName = obj.GetType().FullName ?? obj.GetType().Name ?? "";
                if (!IsUiTypeName(typeName))
                    continue;
                var t = obj.GetType();
                var textProp = GetPropertyCached(t, "text");
                if (textProp == null || !textProp.CanRead)
                    continue;
                string text = null;
                try { text = textProp.GetValueCompat(obj) as string; } catch { text = null; }
                if (IsNullOrWhiteSpace(text))
                    continue;

                if (!TryExtractUiObservationFromText(slotLower, text, out var obs))
                    continue;

                if (currentBest == null || obs.Confidence > currentBest.Confidence)
                    currentBest = obs;
            }

            return currentBest;
        }

        private static bool TryExtractUiObservationFromText(string slotLower, string text, out UiValueObservation observation)
        {
            observation = null;
            if (IsNullOrWhiteSpace(slotLower) || IsNullOrWhiteSpace(text))
                return false;

            var lower = text.ToLowerInvariant();
            var confidence = 0;

            var keywords = slotLower switch
            {
                "money" => new[] { "money", "cash", "coin", "coins", "gold", "credits", "credit", "wallet", "$", "€", "£", "¥", "₽" },
                "xp" => new[] { "xp", "exp", "experience", "level", "lvl" },
                "ammo" => new[] { "ammo", "bullet", "bullets", "clip", "mag", "magazine" },
                "health" => new[] { "hp", "health", "life" },
                "stamina" => new[] { "stamina", "energy", "sprint" },
                _ => null
            };

            if (keywords != null)
            {
                for (var i = 0; i < keywords.Length; i++)
                {
                    var k = keywords[i];
                    if (IsNullOrWhiteSpace(k))
                        continue;
                    if (lower.Contains(k))
                        confidence += 25;
                }
            }

            if (!TryExtractNumbers(text, out var a, out var b, out var count, out var hasSlash))
                return false;

            if (slotLower == "ammo" || slotLower == "health")
            {
                if (count >= 2 && hasSlash)
                {
                    observation = new UiValueObservation { Slot = slotLower, PrimaryValue = a, SecondaryValue = b, HasSecondary = true, Confidence = confidence + 40 };
                    return observation.Confidence > 0;
                }
            }

            if (count >= 1)
            {
                observation = new UiValueObservation { Slot = slotLower, PrimaryValue = a, SecondaryValue = 0, HasSecondary = false, Confidence = confidence + 15 };
                return observation.Confidence > 0;
            }

            return false;
        }

        private static bool TryExtractNumbers(string text, out double first, out double second, out int count, out bool hasSlash)
        {
            first = 0;
            second = 0;
            count = 0;
            hasSlash = false;
            if (IsNullOrWhiteSpace(text))
                return false;

            var len = text.Length;
            var i = 0;
            double v1 = 0;
            double v2 = 0;
            while (i < len && count < 2)
            {
                var c = text[i];
                if (c == '/')
                    hasSlash = true;

                if ((c >= '0' && c <= '9') || (c == '.' && i + 1 < len && text[i + 1] >= '0' && text[i + 1] <= '9'))
                {
                    var start = i;
                    var dot = 0;
                    while (i < len)
                    {
                        var ch = text[i];
                        if (ch >= '0' && ch <= '9')
                        {
                            i++;
                            continue;
                        }
                        if (ch == '.' && dot == 0)
                        {
                            dot++;
                            i++;
                            continue;
                        }
                        break;
                    }
                    var token = text.Substring(start, i - start);
                    if (double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    {
                        if (count == 0) v1 = parsed; else v2 = parsed;
                        count++;
                    }
                    continue;
                }

                i++;
            }

            first = v1;
            second = v2;
            return count > 0;
        }

        private static BoundNumericMember DiscoverNumericMemberFromTelemetry(string slot, UiValueObservation uiObs)
        {
            try
            {
                var s = (slot ?? "").Trim().ToLowerInvariant();
                if (IsNullOrWhiteSpace(s))
                    return null;

                object player = null;
                object playerTransform = null;
                if (s == "health" || s == "ammo" || s == "stamina")
                {
                    player = GetCachedPlayerGameObject();
                    playerTransform = player != null ? GetTransform(player) : null;
                }

                var bestScore = int.MinValue;
                BoundNumericMember best = null;
                var now = Stopwatch.GetTimestamp();

                lock (_telemetryLock)
                {
                    foreach (var kv in _telemetry)
                    {
                        var e = kv.Value;
                        if (e == null)
                            continue;
                        if ((now - e.LastSeenTicks) > Stopwatch.Frequency * 10)
                            continue;
                        if (!e.CanWrite)
                            continue;
                        var target = e.Target?.Target;
                        if (target == null)
                            continue;
                        var member = e.Member;
                        if (member == null)
                            continue;

                        var v = e.LastValue;
                        if (!IsPlausibleSlotValue(s, v, e.MinValue, e.MaxValue))
                            continue;

                        var score = ScoreTelemetryCandidate(s, e, playerTransform, uiObs);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = new BoundNumericMember { Target = target, Member = member, ValueType = e.ValueType, LastKnownValue = v };
                        }
                    }
                }

                return best;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsPlausibleSlotValue(string slot, double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return false;

            if (slot == "ammo")
                return value >= 0 && value <= 5000 && max <= 1000000;
            if (slot == "health")
                return value >= 0 && value <= 1000000 && max <= 1000000000;
            if (slot == "stamina")
                return value >= 0 && value <= 1000000 && max <= 1000000000;
            if (slot == "money")
                return value >= 0 && value <= 1000000000000 && max <= 1000000000000;
            if (slot == "xp")
                return value >= 0 && value <= 1000000000000 && max <= 1000000000000;

            return true;
        }

        private static int ScoreTelemetryCandidate(string slot, NumericTelemetry e, object playerTransform, UiValueObservation uiObs)
        {
            var score = 0;
            var v = e.LastValue;
            if (e.Member is FieldInfo)
                score += 8;
            if (e.Member is PropertyInfo)
                score += 4;

            if (uiObs != null && uiObs.Confidence > 0)
            {
                var tol = IsNearInteger(uiObs.PrimaryValue) ? 0.0001 : 0.01;
                if (Math.Abs(v - uiObs.PrimaryValue) <= tol)
                    score += 300 + uiObs.Confidence;
                if (uiObs.HasSecondary && Math.Abs(e.MaxValue - uiObs.SecondaryValue) <= (IsNearInteger(uiObs.SecondaryValue) ? 0.0001 : 0.01))
                    score += 80;
            }

            if (slot == "ammo")
            {
                score += e.Changes * 2;
                score += e.Decreases * 3;
                if (v <= 500) score += 15;
                if (IsNearInteger(v)) score += 10;
            }
            else if (slot == "health" || slot == "stamina")
            {
                score += e.Changes * 2;
                if (v <= 5000) score += 10;
            }
            else if (slot == "money" || slot == "xp")
            {
                score += e.Increases * 3;
                score -= e.Decreases * 2;
                if (IsNearInteger(v)) score += 10;
            }

            if (playerTransform != null && e.Root != null)
            {
                try
                {
                    var rootObj = e.Root.Target;
                    if (rootObj != null && TryGetTransform(rootObj, out var t) && t != null)
                    {
                        if (IsTransformChildOf(t, playerTransform))
                            score += 60;
                    }
                }
                catch
                {
                }
            }

            return score;
        }

        private static bool IsNearInteger(double value)
        {
            return Math.Abs(value - Math.Round(value)) < 0.0001;
        }

        private static BoundNumericMember DiscoverNumericMemberByUiValue(string slot, UiValueObservation uiObs)
        {
            if (uiObs == null || uiObs.Confidence <= 0)
                return null;

            var primary = uiObs.PrimaryValue;
            var tol = IsNearInteger(primary) ? 0.0001 : 0.01;

            return FindBestValueMatchInScene(slot, primary, tol, uiObs.HasSecondary ? uiObs.SecondaryValue : 0, uiObs.HasSecondary);
        }

        private static BoundNumericMember FindBestValueMatchInScene(string slot, double primaryValue, double tol, double secondaryValue, bool hasSecondary)
        {
            try
            {
                var componentType = FindUnityType("UnityEngine.Component");
                if (componentType == null)
                    return null;

                var components = FindUnityObjectsOfType(componentType);
                if (components == null || components.Length == 0)
                    return null;

                var player = GetCachedPlayerGameObject();
                var playerTransform = player != null ? GetTransform(player) : null;

                var bestScore = int.MinValue;
                BoundNumericMember best = null;
                var seen = new HashSet<int>();
                var budget = 22000;
                var depth = 3;

                for (var i = 0; i < components.Length && budget > 0; i++)
                {
                    var comp = components.GetValue(i);
                    if (comp == null)
                        continue;
                    if (!IsUnityObjectAlive(comp))
                        continue;
                    var typeName = comp.GetType().FullName ?? comp.GetType().Name ?? "";
                    if (IsUiTypeName(typeName))
                        continue;
                    FindBestValueMatchRecursive(comp, comp, depth, slot, primaryValue, tol, secondaryValue, hasSecondary, playerTransform, seen, ref budget, ref bestScore, ref best);
                }

                return best;
            }
            catch
            {
                return null;
            }
        }

        private static void FindBestValueMatchRecursive(object root, object target, int depth, string slot, double primaryValue, double tol, double secondaryValue, bool hasSecondary, object playerTransform, HashSet<int> seen, ref int budget, ref int bestScore, ref BoundNumericMember best)
        {
            if (target == null || budget <= 0)
                return;

            budget--;

            var t = target.GetType();
            if (t == typeof(string))
                return;

            if (!t.IsValueType)
            {
                var key = RuntimeHelpers.GetHashCode(target);
                if (seen != null)
                {
                    if (seen.Contains(key))
                        return;
                    seen.Add(key);
                }
            }

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            try
            {
                var fields = t.GetFields(flags);
                for (var i = 0; i < fields.Length && budget > 0; i++)
                {
                    var f = fields[i];
                    if (f == null || f.IsLiteral)
                        continue;
                    if (!TryGetNumericType(f.FieldType))
                        continue;
                    object raw = null;
                    try { raw = f.GetValue(target); } catch { raw = null; }
                    if (!TryConvertNumericToDouble(raw, out var v))
                        continue;
                    if (Math.Abs(v - primaryValue) <= tol)
                    {
                        var score = ScoreValueMatchCandidate(slot, root, target, f, v, playerTransform, secondaryValue, hasSecondary);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = new BoundNumericMember { Target = target, Member = f, ValueType = f.FieldType, LastKnownValue = v };
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                var props = t.GetProperties(flags);
                for (var i = 0; i < props.Length && budget > 0; i++)
                {
                    var p = props[i];
                    if (p == null || !p.CanRead || !p.CanWrite)
                        continue;
                    var idx = p.GetIndexParameters();
                    if (idx != null && idx.Length != 0)
                        continue;
                    if (!TryGetNumericType(p.PropertyType))
                        continue;
                    object raw = null;
                    try { raw = p.GetValueCompat(target); } catch { raw = null; }
                    if (!TryConvertNumericToDouble(raw, out var v))
                        continue;
                    if (Math.Abs(v - primaryValue) <= tol)
                    {
                        var score = ScoreValueMatchCandidate(slot, root, target, p, v, playerTransform, secondaryValue, hasSecondary);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = new BoundNumericMember { Target = target, Member = p, ValueType = p.PropertyType, LastKnownValue = v };
                        }
                    }
                }
            }
            catch
            {
            }

            if (depth <= 0 || budget <= 0)
                return;

            try
            {
                var fields = t.GetFields(flags);
                for (var i = 0; i < fields.Length && budget > 0; i++)
                {
                    var f = fields[i];
                    if (f == null || f.IsLiteral)
                        continue;
                    if (!ShouldFollowReferenceType(f.FieldType))
                        continue;
                    object child = null;
                    try { child = f.GetValue(target); } catch { child = null; }
                    if (child == null)
                        continue;
                    FindBestValueMatchRecursive(root, child, depth - 1, slot, primaryValue, tol, secondaryValue, hasSecondary, playerTransform, seen, ref budget, ref bestScore, ref best);
                }
            }
            catch
            {
            }

            try
            {
                var props = t.GetProperties(flags);
                for (var i = 0; i < props.Length && budget > 0; i++)
                {
                    var p = props[i];
                    if (p == null || !p.CanRead)
                        continue;
                    var idx = p.GetIndexParameters();
                    if (idx != null && idx.Length != 0)
                        continue;
                    if (!ShouldFollowReferenceType(p.PropertyType))
                        continue;
                    object child = null;
                    try { child = p.GetValueCompat(target); } catch { child = null; }
                    if (child == null)
                        continue;
                    FindBestValueMatchRecursive(root, child, depth - 1, slot, primaryValue, tol, secondaryValue, hasSecondary, playerTransform, seen, ref budget, ref bestScore, ref best);
                }
            }
            catch
            {
            }
        }

        private static int ScoreValueMatchCandidate(string slot, object root, object target, MemberInfo member, double value, object playerTransform, double secondaryValue, bool hasSecondary)
        {
            var score = 0;

            var rootType = root?.GetType().FullName ?? root?.GetType().Name ?? "";
            if (IsUiTypeName(rootType))
                score -= 200;

            var targetType = target?.GetType().FullName ?? target?.GetType().Name ?? "";
            if (IsUiTypeName(targetType))
                score -= 200;

            if (member is FieldInfo)
                score += 10;
            if (member is PropertyInfo)
                score += 5;

            if (slot == "money")
            {
                if (value >= 0) score += 20;
                if (IsNearInteger(value)) score += 25;
            }
            else if (slot == "ammo")
            {
                if (value >= 0 && value <= 999) score += 30;
                if (IsNearInteger(value)) score += 20;
            }
            else if (slot == "health" || slot == "stamina")
            {
                if (value >= 0 && value <= 5000) score += 20;
            }
            else if (slot == "xp")
            {
                if (value >= 0) score += 10;
                if (IsNearInteger(value)) score += 15;
            }

            if (playerTransform != null && root != null)
            {
                try
                {
                    if (TryGetTransform(root, out var t) && t != null)
                    {
                        if (IsTransformChildOf(t, playerTransform))
                        {
                            if (slot == "ammo" || slot == "health" || slot == "stamina")
                                score += 70;
                            else
                                score += 20;
                        }
                    }
                }
                catch
                {
                }
            }

            if (hasSecondary && Math.Abs(secondaryValue) > 0)
            {
                if (HasSecondaryValueNearby(target, secondaryValue))
                    score += 60;
            }

            var memberName = member?.Name ?? "";
            if (!IsNullOrWhiteSpace(memberName))
            {
                if (memberName.IndexOf(slot, StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 40;
                if (memberName.IndexOf("current", StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 10;
                if (memberName.IndexOf("max", StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 5;
            }

            return score;
        }

        private static bool HasSecondaryValueNearby(object target, double secondaryValue)
        {
            if (target == null)
                return false;

            var t = target.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            try
            {
                var fields = t.GetFields(flags);
                for (var i = 0; i < fields.Length; i++)
                {
                    var f = fields[i];
                    if (f == null || f.IsLiteral)
                        continue;
                    if (!TryGetNumericType(f.FieldType))
                        continue;
                    object raw = null;
                    try { raw = f.GetValue(target); } catch { raw = null; }
                    if (!TryConvertNumericToDouble(raw, out var v))
                        continue;
                    if (Math.Abs(v - secondaryValue) <= (IsNearInteger(secondaryValue) ? 0.0001 : 0.01))
                        return true;
                }
            }
            catch
            {
            }

            try
            {
                var props = t.GetProperties(flags);
                for (var i = 0; i < props.Length; i++)
                {
                    var p = props[i];
                    if (p == null || !p.CanRead)
                        continue;
                    var idx = p.GetIndexParameters();
                    if (idx != null && idx.Length != 0)
                        continue;
                    if (!TryGetNumericType(p.PropertyType))
                        continue;
                    object raw = null;
                    try { raw = p.GetValueCompat(target); } catch { raw = null; }
                    if (!TryConvertNumericToDouble(raw, out var v))
                        continue;
                    if (Math.Abs(v - secondaryValue) <= (IsNearInteger(secondaryValue) ? 0.0001 : 0.01))
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static BoundNumericMember DiscoverNumericMemberOnPlayer(string[] memberNames, string[] typeHints)
        {
            try
            {
                var player = GetCachedPlayerGameObject();
                if (player == null)
                    return null;

                return DiscoverNumericMemberOnGameObject(player, memberNames, typeHints);
            }
            catch
            {
                return null;
            }
        }

        private static BoundNumericMember DiscoverNumericMemberInScene(string[] memberNames, string[] typeHints)
        {
            try
            {
                var componentType = FindUnityType("UnityEngine.Component");
                if (componentType == null)
                    return null;

                var components = FindUnityObjectsOfType(componentType);
                if (components == null)
                    return null;

                var player = GetCachedPlayerGameObject();
                var playerTransform = player != null ? GetTransform(player) : null;
                BoundNumericMember best = null;
                var bestScore = int.MinValue;
                var budget = 4000;

                foreach (var comp in components)
                {
                    if (comp == null)
                        continue;
                    var t = comp.GetType();
                    var typeName = t.FullName ?? t.Name ?? "";
                    var typeHintMatched = false;
                    if (typeHints != null && typeHints.Length > 0)
                    {
                        foreach (var hint in typeHints)
                        {
                            if (typeName.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                typeHintMatched = true;
                                break;
                            }
                        }
                    }

                    BoundNumericMember candidate = null;
                    candidate = DiscoverNumericMemberOnObject(comp, memberNames);
                    if (candidate == null || !candidate.IsValid)
                    {
                        if (typeHints == null || typeHints.Length == 0 || typeHintMatched)
                            candidate = DiscoverNumericMemberOnObjectDeep(comp, memberNames, 3, ref budget);
                    }
                    if (candidate != null && candidate.IsValid)
                    {
                        var score = ScoreNumericCandidate(comp, typeName, candidate, memberNames, typeHints, playerTransform);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = candidate;
                        }
                    }
                }

                return best;
            }
            catch
            {
                return null;
            }
        }

        private static BoundNumericMember DiscoverNumericMemberOnGameObject(object gameObject, string[] memberNames, string[] typeHints)
        {
            try
            {
                var goType = FindUnityType("UnityEngine.GameObject");
                var componentType = FindUnityType("UnityEngine.Component");
                if (goType == null || componentType == null)
                    return null;

                var getComponentsInChildren = goType.GetMethod(
                    "GetComponentsInChildren",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(Type), typeof(bool) },
                    null);

                var getComponents = goType.GetMethod("GetComponents", new[] { typeof(Type) });
                if (getComponentsInChildren == null && getComponents == null)
                    return null;

                Array components = null;
                try
                {
                    if (getComponentsInChildren != null)
                        components = CoerceToSystemArray(getComponentsInChildren.Invoke(gameObject, new object[] { componentType, true }));
                }
                catch
                {
                }

                if (components == null && getComponents != null)
                    components = CoerceToSystemArray(getComponents.Invoke(gameObject, new object[] { componentType }));

                if (components == null)
                    return null;

                BoundNumericMember best = null;
                var bestScore = int.MinValue;
                var budget = 2500;
                var playerTransform = GetTransform(gameObject);
                foreach (var comp in components)
                {
                    if (comp == null)
                        continue;

                    var typeName = comp.GetType().FullName ?? comp.GetType().Name ?? "";
                    var typeHintMatched = false;
                    if (typeHints != null && typeHints.Length > 0)
                    {
                        foreach (var hint in typeHints)
                        {
                            if (typeName.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                typeHintMatched = true;
                                break;
                            }
                        }
                    }

                    BoundNumericMember candidate = null;
                    candidate = DiscoverNumericMemberOnObject(comp, memberNames);
                    if (candidate == null || !candidate.IsValid)
                    {
                        if (typeHints == null || typeHints.Length == 0 || typeHintMatched)
                            candidate = DiscoverNumericMemberOnObjectDeep(comp, memberNames, 3, ref budget);
                    }
                    if (candidate != null && candidate.IsValid)
                    {
                        var score = ScoreNumericCandidate(comp, typeName, candidate, memberNames, typeHints, playerTransform);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = candidate;
                        }
                    }
                }

                return best;
            }
            catch
            {
                return null;
            }
        }

        private static int ScoreNumericCandidate(object contextObject, string contextTypeName, BoundNumericMember bound, string[] memberNames, string[] typeHints, object playerTransform)
        {
            var score = 0;
            if (!IsNullOrWhiteSpace(contextTypeName))
            {
                if (typeHints != null)
                {
                    for (var i = 0; i < typeHints.Length; i++)
                    {
                        var hint = typeHints[i];
                        if (IsNullOrWhiteSpace(hint))
                            continue;
                        if (contextTypeName.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                            score += 15;
                    }
                }
            }

            var memberName = bound?.Member?.Name ?? "";
            if (!IsNullOrWhiteSpace(memberName) && memberNames != null)
            {
                for (var i = 0; i < memberNames.Length; i++)
                {
                    var n = memberNames[i];
                    if (IsNullOrWhiteSpace(n))
                        continue;
                    if (memberName.Equals(n, StringComparison.OrdinalIgnoreCase))
                        score += 120;
                    else if (memberName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 60;
                }
            }

            if (playerTransform != null && contextObject != null)
            {
                try
                {
                    if (TryGetTransform(contextObject, out var t) && t != null)
                    {
                        if (IsTransformChildOf(t, playerTransform))
                            score += 40;
                    }
                }
                catch
                {
                }
            }

            if (bound != null && bound.IsValid)
            {
                try
                {
                    if (TryGetNumeric(bound, out var v))
                        score += Math.Abs(v) < 1000000000 ? 8 : 0;
                }
                catch
                {
                }
            }

            return score;
        }

        private static BoundNumericMember DiscoverNumericMemberOnObjectDeep(object target, string[] memberNames, int maxDepth)
        {
            var budget = 1200;
            return DiscoverNumericMemberOnObjectDeep(target, memberNames, maxDepth, ref budget);
        }

        private static BoundNumericMember DiscoverNumericMemberOnObjectDeep(object target, string[] memberNames, int maxDepth, ref int budget)
        {
            try
            {
                if (target == null || memberNames == null || memberNames.Length == 0)
                    return null;
                if (maxDepth < 0 || budget <= 0)
                    return null;

                var seen = new HashSet<int>();
                return DiscoverNumericMemberOnObjectDeepInternal(target, memberNames, maxDepth, seen, ref budget);
            }
            catch
            {
                return null;
            }
        }

        private static BoundNumericMember DiscoverNumericMemberOnObjectDeepInternal(object target, string[] memberNames, int maxDepth, HashSet<int> seen, ref int budget)
        {
            if (target == null || budget <= 0)
                return null;

            budget--;

            var t = target.GetType();
            if (t == typeof(string))
                return null;

            if (!t.IsValueType)
            {
                var key = RuntimeHelpers.GetHashCode(target);
                if (seen != null)
                {
                    if (seen.Contains(key))
                        return null;
                    seen.Add(key);
                }
            }

            var direct = DiscoverNumericMemberOnObject(target, memberNames);
            if (direct != null && direct.IsValid)
                return direct;

            if (maxDepth <= 0)
                return null;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo[] fields = null;
            PropertyInfo[] props = null;
            try { fields = t.GetFields(flags); } catch { fields = null; }
            try { props = t.GetProperties(flags); } catch { props = null; }

            if (fields != null)
            {
                for (var i = 0; i < fields.Length; i++)
                {
                    if (budget <= 0)
                        return null;
                    var f = fields[i];
                    if (f == null)
                        continue;
                    if (f.IsLiteral)
                        continue;
                    var ft = f.FieldType;
                    if (!ShouldFollowReferenceType(ft))
                        continue;
                    object child = null;
                    try { child = f.GetValue(target); } catch { child = null; }
                    if (child == null)
                        continue;
                    var found = DiscoverNumericMemberOnObjectDeepInternal(child, memberNames, maxDepth - 1, seen, ref budget);
                    if (found != null && found.IsValid)
                        return found;
                }
            }

            if (props != null)
            {
                for (var i = 0; i < props.Length; i++)
                {
                    if (budget <= 0)
                        return null;
                    var p = props[i];
                    if (p == null)
                        continue;
                    if (!p.CanRead)
                        continue;
                    var indexParams = p.GetIndexParameters();
                    if (indexParams != null && indexParams.Length != 0)
                        continue;
                    var pt = p.PropertyType;
                    if (!ShouldFollowReferenceType(pt))
                        continue;
                    object child = null;
                    try { child = p.GetValueCompat(target); } catch { child = null; }
                    if (child == null)
                        continue;
                    var found = DiscoverNumericMemberOnObjectDeepInternal(child, memberNames, maxDepth - 1, seen, ref budget);
                    if (found != null && found.IsValid)
                        return found;
                }
            }

            return null;
        }

        private static bool ShouldFollowReferenceType(Type t)
        {
            if (t == null)
                return false;
            if (t.IsValueType)
                return false;
            if (t == typeof(string))
                return false;
            if (typeof(Delegate).IsAssignableFrom(t))
                return false;
            if (t.IsArray)
                return false;

            var n = t.FullName ?? t.Name ?? "";
            if (n.StartsWith("UnityEngine.", StringComparison.Ordinal))
                return false;
            if (n.StartsWith("Il2CppSystem.", StringComparison.Ordinal))
                return false;
            if (n.StartsWith("System.Reflection.", StringComparison.Ordinal))
                return false;
            if (n.StartsWith("System.Threading.", StringComparison.Ordinal))
                return false;
            if (n.StartsWith("System.IO.", StringComparison.Ordinal))
                return false;

            return true;
        }

        private static string BuildAutoDetectBindingDetails(BoundNumericMember bound)
        {
            if (bound == null || !bound.IsValid)
                return "";

            var goType = FindUnityType("UnityEngine.GameObject");
            var componentTypeName =
                (goType != null && goType.IsInstanceOfType(bound.Target)) ? "(GameObject)" :
                (bound.Target.GetType().FullName ?? bound.Target.GetType().Name ?? "");

            var decl = bound.Member.DeclaringType?.FullName ?? bound.Member.DeclaringType?.Name ?? "";
            var kind = bound.Member is FieldInfo ? "FIELD" : "PROPERTY";
            var name = bound.Member.Name ?? "";

            return $"|key=player|component={SanitizePipeValue(componentTypeName)}|decl={SanitizePipeValue(decl)}|kind={SanitizePipeValue(kind)}|member={SanitizePipeValue(name)}";
        }

        private static string AutoDetectBinding(string slot)
        {
            try
            {
                if (IsNullOrWhiteSpace(slot))
                    return "ERROR|Missing slot";

                var s = slot.Trim().ToLowerInvariant();

                if (s == "all")
                {
                    _healthMember = null;
                    _ammoMember = null;
                    _moneyMember = null;
                    _xpMember = null;
                    _staminaMember = null;
                    EnsureHealthBinding();
                    EnsureAmmoBinding();
                    EnsureMoneyBinding();
                    EnsureXpBinding();
                    EnsureStaminaBinding();
                    return "SUCCESS|AUTO_DETECT|" + GetBindingsInfo();
                }

                if (s == "health")
                {
                    _healthMember = null;
                    EnsureHealthBinding();
                    return $"SUCCESS|AUTO_DETECT|slot=health|binding={SanitizePipeValue(DescribeBinding(_healthMember))}{BuildAutoDetectBindingDetails(_healthMember)}";
                }
                if (s == "ammo")
                {
                    _ammoMember = null;
                    EnsureAmmoBinding();
                    return $"SUCCESS|AUTO_DETECT|slot=ammo|binding={SanitizePipeValue(DescribeBinding(_ammoMember))}{BuildAutoDetectBindingDetails(_ammoMember)}";
                }
                if (s == "money")
                {
                    _moneyMember = null;
                    EnsureMoneyBinding();
                    return $"SUCCESS|AUTO_DETECT|slot=money|binding={SanitizePipeValue(DescribeBinding(_moneyMember))}{BuildAutoDetectBindingDetails(_moneyMember)}";
                }
                if (s == "xp")
                {
                    _xpMember = null;
                    EnsureXpBinding();
                    return $"SUCCESS|AUTO_DETECT|slot=xp|binding={SanitizePipeValue(DescribeBinding(_xpMember))}{BuildAutoDetectBindingDetails(_xpMember)}";
                }
                if (s == "stamina")
                {
                    _staminaMember = null;
                    EnsureStaminaBinding();
                    return $"SUCCESS|AUTO_DETECT|slot=stamina|binding={SanitizePipeValue(DescribeBinding(_staminaMember))}{BuildAutoDetectBindingDetails(_staminaMember)}";
                }

                return $"ERROR|Unknown slot: {slot}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private sealed class ValueScanHit
        {
            public object Context;
            public BoundNumericMember Bound;
        }

        private static string ValueScan(string[] parts)
        {
            try
            {
                if (parts == null || parts.Length < 2 || IsNullOrWhiteSpace(parts[1]))
                    return "ERROR|Missing value";

                if (!TryParseDouble(parts[1], out var targetValue))
                    return "ERROR|Invalid value";

                var tol = 0d;
                if (parts.Length > 2 && TryParseDouble(parts[2], out var t))
                    tol = Math.Abs(t);

                var max = 100;
                if (parts.Length > 3 && int.TryParse(parts[3], out var m) && m > 0)
                    max = Math.Min(m, 400);

                var depth = 2;
                if (parts.Length > 4 && int.TryParse(parts[4], out var d) && d >= 0)
                    depth = Math.Min(d, 4);

                var componentType = FindUnityType("UnityEngine.Component");
                if (componentType == null)
                    return "ERROR|Component type not found";

                var components = FindUnityObjectsOfType(componentType);
                if (components == null)
                    return "ERROR|No components";

                var hits = new List<ValueScanHit>(capacity: Math.Min(max, 128));
                var budget = 16000;
                var seen = new HashSet<int>();

                for (var i = 0; i < components.Length; i++)
                {
                    if (hits.Count >= max || budget <= 0)
                        break;
                    var comp = components.GetValue(i);
                    if (comp == null)
                        continue;
                    CollectValueScanHits(comp, depth, targetValue, tol, hits, max, seen, ref budget);
                }

                var sb = new StringBuilder();
                sb.Append("VALUE_SCAN");
                sb.Append("|value=");
                sb.Append(parts[1]);
                sb.Append("|tol=");
                sb.Append(tol.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append("|count=");
                sb.Append(hits.Count);

                for (var i = 0; i < hits.Count; i++)
                {
                    var hit = hits[i];
                    if (hit?.Bound == null || !hit.Bound.IsValid)
                        continue;
                    var ctxType = hit.Context?.GetType().FullName ?? hit.Context?.GetType().Name ?? "";
                    var kind = hit.Bound.Member is FieldInfo ? "FIELD" : "PROPERTY";
                    var decl = hit.Bound.Member.DeclaringType?.FullName ?? hit.Bound.Member.DeclaringType?.Name ?? "";
                    var name = hit.Bound.Member.Name ?? "";
                    var vt = hit.Bound.ValueType?.FullName ?? hit.Bound.ValueType?.Name ?? "";
                    var val = hit.Bound.LastKnownValue.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                    var id = TrackObject(hit.Bound.Target).ToString();

                    sb.Append("|h=");
                    sb.Append(id);
                    sb.Append(";kind=");
                    sb.Append(kind);
                    sb.Append(";name=");
                    sb.Append(SanitizePipeValue(name));
                    sb.Append(";decl=");
                    sb.Append(SanitizePipeValue(decl));
                    sb.Append(";type=");
                    sb.Append(SanitizePipeValue(vt));
                    sb.Append(";value=");
                    sb.Append(val);
                    sb.Append(";context=");
                    sb.Append(SanitizePipeValue(ctxType));
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DiscoveryScan(string[] parts)
        {
            try
            {
                var max = 12000;
                var budgetMs = 2;
                var scope = "mono";
                var kinds = new HashSet<DiscoveryCandidateKind>
                {
                    DiscoveryCandidateKind.Numeric,
                    DiscoveryCandidateKind.Boolean,
                    DiscoveryCandidateKind.CollectionCount,
                    DiscoveryCandidateKind.Enum
                };

                if (parts != null && parts.Length > 1)
                {
                    var maxText = GetPipeArg(parts, "max");
                    if (!IsNullOrWhiteSpace(maxText) && int.TryParse(maxText, out var m) && m > 0)
                        max = Math.Min(m, 60000);

                    var budgetText = GetPipeArg(parts, "budgetMs");
                    if (!IsNullOrWhiteSpace(budgetText) && int.TryParse(budgetText, out var b) && b > 0)
                        budgetMs = Math.Max(1, Math.Min(b, 16));

                    var scopeText = GetPipeArg(parts, "scope");
                    if (!IsNullOrWhiteSpace(scopeText))
                        scope = scopeText.Trim().ToLowerInvariant();

                    var kindsText = GetPipeArg(parts, "kinds");
                    if (!IsNullOrWhiteSpace(kindsText))
                    {
                        kinds.Clear();
                        var tokens = kindsText.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                        for (var i = 0; i < tokens.Length; i++)
                        {
                            var t = tokens[i]?.Trim();
                            if (IsNullOrWhiteSpace(t))
                                continue;
                            if (t.Equals("numeric", StringComparison.OrdinalIgnoreCase))
                                kinds.Add(DiscoveryCandidateKind.Numeric);
                            else if (t.Equals("bool", StringComparison.OrdinalIgnoreCase) || t.Equals("boolean", StringComparison.OrdinalIgnoreCase))
                                kinds.Add(DiscoveryCandidateKind.Boolean);
                            else if (t.Equals("count", StringComparison.OrdinalIgnoreCase) || t.Equals("collection", StringComparison.OrdinalIgnoreCase) || t.Equals("collectioncount", StringComparison.OrdinalIgnoreCase))
                                kinds.Add(DiscoveryCandidateKind.CollectionCount);
                            else if (t.Equals("enum", StringComparison.OrdinalIgnoreCase))
                                kinds.Add(DiscoveryCandidateKind.Enum);
                        }
                        if (kinds.Count == 0)
                        {
                            kinds.Add(DiscoveryCandidateKind.Numeric);
                            kinds.Add(DiscoveryCandidateKind.Boolean);
                            kinds.Add(DiscoveryCandidateKind.CollectionCount);
                            kinds.Add(DiscoveryCandidateKind.Enum);
                        }
                    }
                }

                lock (_discoverySync)
                {
                    foreach (var j in _discoveryScanJobs.Values)
                    {
                        if (j != null && !j.Completed)
                            return "ERROR|Discovery scan already running";
                    }
                }

                var scanId = Interlocked.Increment(ref _discoveryNextScanId);

                var typeName = scope == "all" ? "UnityEngine.Component" : "UnityEngine.MonoBehaviour";
                var componentType = FindUnityType(typeName);
                if (componentType == null)
                    componentType = FindUnityType("UnityEngine.Component");
                if (componentType == null)
                    return "ERROR|Component type not found";

                var components = FindUnityObjectsOfType(componentType);
                if (components == null)
                    return "ERROR|No components";

                var entries = new List<DiscoveryCandidateDescriptor>(Math.Min(max, 16384));
                var byKey = new Dictionary<string, DiscoveryCandidateDescriptor>(StringComparer.Ordinal);

                var job = new DiscoveryScanJob
                {
                    ScanId = scanId,
                    StartedTicks = Stopwatch.GetTimestamp(),
                    MaxCandidates = max,
                    Kinds = kinds,
                    BudgetMs = budgetMs,
                    Scope = scope,
                    Components = components,
                    Index = 0,
                    Total = components.Length,
                    Entries = entries,
                    ByKey = byKey,
                    Completed = false,
                    Stage = "scan"
                };

                lock (_discoverySync)
                {
                    _discoveryScanJobs[scanId] = job;
                }

                _discoveryOverlayText = "Scan started";
                _discoveryOverlayProgress = 0;

                return $"DISCOVERY_SCAN|scanId={scanId}|running=1|total={components.Length}|scope={SanitizePipeValue(scope)}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DiscoveryScanStatus(string scanIdText)
        {
            try
            {
                if (!long.TryParse(scanIdText, out var scanId) || scanId <= 0)
                    return "ERROR|Invalid scan id";

                DiscoveryScanJob job = null;
                lock (_discoverySync)
                {
                    _discoveryScanJobs.TryGetValue(scanId, out job);
                }

                if (job == null)
                {
                    lock (_discoverySync)
                    {
                        if (_discoveryScans.ContainsKey(scanId))
                            return $"DISCOVERY_SCAN_STATUS|scanId={scanId}|running=0|done=1|progress=1";
                    }
                    return $"DISCOVERY_SCAN_STATUS|scanId={scanId}|running=0|done=0|progress=0";
                }

                var progress = job.Total > 0 ? (job.Index / (double)job.Total) : 0;
                if (progress < 0) progress = 0;
                if (progress > 1) progress = 1;

                return $"DISCOVERY_SCAN_STATUS|scanId={scanId}|running={(job.Completed ? 0 : 1)}|done={(job.Completed ? 1 : 0)}|progress={progress.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}|processed={job.Index}|total={job.Total}|candidates={job.Entries?.Count ?? 0}|stage={SanitizePipeValue(job.Stage ?? "")}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DiscoveryScanCancel(string scanIdText)
        {
            try
            {
                if (!long.TryParse(scanIdText, out var scanId) || scanId <= 0)
                    return "ERROR|Invalid scan id";
                lock (_discoverySync)
                {
                    if (_discoveryScanJobs.Remove(scanId))
                    {
                        _discoveryOverlayText = "";
                        _discoveryOverlayProgress = 0;
                        return $"DISCOVERY_SCAN_CANCEL|scanId={scanId}|canceled=1";
                    }
                }
                return $"DISCOVERY_SCAN_CANCEL|scanId={scanId}|canceled=0";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static void UpdateDiscoveryScanJobs()
        {
            try
            {
                DiscoveryScanJob[] jobs;
                lock (_discoverySync)
                {
                    if (_discoveryScanJobs.Count == 0)
                        return;
                    jobs = _discoveryScanJobs.Values.ToArray();
                }

                for (var ji = 0; ji < jobs.Length; ji++)
                {
                    var job = jobs[ji];
                    if (job == null || job.Completed)
                        continue;

                    var budgetTicks = (long)(Math.Max(1, job.BudgetMs) * (Stopwatch.Frequency / 1000d));
                    var start = Stopwatch.GetTimestamp();

                    while (job.Index < job.Total && (job.Entries?.Count ?? 0) < job.MaxCandidates)
                    {
                        if ((Stopwatch.GetTimestamp() - start) >= budgetTicks)
                            break;

                        object comp = null;
                        try { comp = job.Components?.GetValue(job.Index); } catch { comp = null; }
                        job.Index++;
                        if (comp == null)
                            continue;

                        var compType = comp.GetType();
                        var compTypeName = compType.FullName ?? compType.Name ?? "";
                        if (ShouldSkipDiscoveryComponentType(compTypeName))
                            continue;

                        var trackedId = TrackObject(comp);
                        if (trackedId == 0)
                            continue;

                        object go = null;
                        try { go = TryGetGameObject(comp) ?? comp; } catch { go = comp; }

                        var goName = "";
                        try { goName = GetUnityObjectName(go) ?? ""; } catch { goName = ""; }
                        var goPath = "";
                        try { goPath = BuildHierarchyNamePath(go, 10); } catch { goPath = goName; }

                        CollectDiscoveryCandidatesOnComponentOptimized(comp, trackedId, compTypeName, goName, goPath, job.Kinds, job.Entries, job.ByKey, job.MaxCandidates);
                    }

                    var progress = job.Total > 0 ? (job.Index / (double)job.Total) : 0;
                    if (progress < 0) progress = 0;
                    if (progress > 1) progress = 1;
                    _discoveryOverlayText = $"Scanning {job.Index}/{job.Total} ({job.Entries.Count} vars)";
                    _discoveryOverlayProgress = progress;

                    if (job.Index >= job.Total || job.Entries.Count >= job.MaxCandidates)
                    {
                        job.Completed = true;
                        var cache = new DiscoveryScanCache
                        {
                            ScanId = job.ScanId,
                            CreatedTicks = Stopwatch.GetTimestamp(),
                            Entries = job.Entries,
                            ByKey = job.ByKey
                        };

                        lock (_discoverySync)
                        {
                            _discoveryScans[job.ScanId] = cache;
                            _discoveryScanJobs.Remove(job.ScanId);
                            if (_discoveryScans.Count > 6)
                            {
                                var oldest = _discoveryScans.Keys.OrderBy(k => k).Take(Math.Max(1, _discoveryScans.Count - 6)).ToArray();
                                for (var i = 0; i < oldest.Length; i++)
                                    _discoveryScans.Remove(oldest[i]);
                            }
                        }

                        _discoveryOverlayText = "Scan complete";
                        _discoveryOverlayProgress = 1;
                    }
                }
            }
            catch
            {
            }
        }

        private static bool ShouldSkipDiscoveryComponentType(string componentTypeName)
        {
            if (IsNullOrWhiteSpace(componentTypeName))
                return true;

            if (componentTypeName.StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase))
                return true;
            if (componentTypeName.StartsWith("TMPro.", StringComparison.OrdinalIgnoreCase))
                return true;
            if (componentTypeName.StartsWith("UnityEngine.UI", StringComparison.OrdinalIgnoreCase))
                return true;
            if (componentTypeName.StartsWith("Il2Cpp", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private static void CollectDiscoveryCandidatesOnComponentOptimized(
            object component,
            int trackedId,
            string componentTypeName,
            string goName,
            string goPath,
            HashSet<DiscoveryCandidateKind> kinds,
            List<DiscoveryCandidateDescriptor> entries,
            Dictionary<string, DiscoveryCandidateDescriptor> byKey,
            int max)
        {
            if (component == null || entries == null || byKey == null || entries.Count >= max)
                return;

            var members = GetCachedDiscoveryMembers(component.GetType());
            if (members == null || members.Length == 0)
                return;

            for (var i = 0; i < members.Length && entries.Count < max; i++)
            {
                var m = members[i];
                if (m == null)
                    continue;
                if (kinds != null && !kinds.Contains(m.Kind))
                    continue;

                var key = BuildDiscoveryKey(trackedId, componentTypeName, m.DeclaringTypeName ?? "", m.MemberName ?? "", (int)m.Kind);
                if (byKey.ContainsKey(key))
                    continue;

                var fp = ComputeDiscoveryFingerprint(componentTypeName, m.DeclaringTypeName ?? "", m.MemberName ?? "", m.MemberTypeName ?? "", goPath);
                var d = new DiscoveryCandidateDescriptor
                {
                    Key = key,
                    Fingerprint = fp,
                    Kind = m.Kind,
                    Component = new WeakReference(component),
                    Member = m.Member,
                    GameObjectName = goName,
                    GameObjectPath = goPath,
                    ComponentTypeName = componentTypeName,
                    DeclaringTypeName = m.DeclaringTypeName ?? "",
                    MemberName = m.MemberName ?? "",
                    MemberTypeName = m.MemberTypeName ?? "",
                    IsStatic = m.IsStatic,
                    CanWrite = m.CanWrite
                };
                entries.Add(d);
                byKey[key] = d;
            }
        }

        private static CachedDiscoveryMember[] GetCachedDiscoveryMembers(Type componentType)
        {
            if (componentType == null)
                return new CachedDiscoveryMember[0];

            lock (_discoveryMemberCacheSync)
            {
                if (_discoveryMemberCache.TryGetValue(componentType, out var cached) && cached != null)
                    return cached;
            }

            var list = new List<CachedDiscoveryMember>(128);
            for (var cur = componentType; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                var declName = cur.FullName ?? cur.Name ?? "";
                if (declName.StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase) || declName.StartsWith("Il2Cpp", StringComparison.OrdinalIgnoreCase))
                    continue;

                FieldInfo[] fields = null;
                try { fields = cur.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); } catch { fields = null; }
                if (fields != null)
                {
                    for (var i = 0; i < fields.Length; i++)
                    {
                        var f = fields[i];
                        if (f == null || f.IsLiteral)
                            continue;
                        if (!TryGetDiscoveryKindForType(f.FieldType, out var kind))
                            continue;
                        var mt = f.FieldType?.FullName ?? f.FieldType?.Name ?? "";
                        list.Add(new CachedDiscoveryMember
                        {
                            Member = f,
                            Kind = kind,
                            CanWrite = !f.IsInitOnly,
                            IsStatic = f.IsStatic,
                            DeclaringTypeName = declName,
                            MemberName = f.Name ?? "",
                            MemberTypeName = mt
                        });
                        if (list.Count >= 320)
                            break;
                    }
                }
                if (list.Count >= 320)
                    break;

                PropertyInfo[] props = null;
                try { props = cur.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); } catch { props = null; }
                if (props != null)
                {
                    for (var i = 0; i < props.Length; i++)
                    {
                        var p = props[i];
                        if (p == null || !p.CanRead)
                            continue;
                        var idx = p.GetIndexParameters();
                        if (idx != null && idx.Length != 0)
                            continue;
                        if (!TryGetDiscoveryKindForType(p.PropertyType, out var kind))
                            continue;
                        var mt = p.PropertyType?.FullName ?? p.PropertyType?.Name ?? "";
                        var setter = p.GetSetMethod(true);
                        var isStatic = setter != null && setter.IsStatic;
                        list.Add(new CachedDiscoveryMember
                        {
                            Member = p,
                            Kind = kind,
                            CanWrite = p.CanWrite,
                            IsStatic = isStatic,
                            DeclaringTypeName = declName,
                            MemberName = p.Name ?? "",
                            MemberTypeName = mt
                        });
                        if (list.Count >= 320)
                            break;
                    }
                }

                if (list.Count >= 320)
                    break;
            }

            var arr = list.ToArray();
            lock (_discoveryMemberCacheSync)
            {
                _discoveryMemberCache[componentType] = arr;
            }
            return arr;
        }

        private static string DiscoveryScanPage(string scanIdText, string offsetText, string limitText)
        {
            try
            {
                if (!long.TryParse(scanIdText, out var scanId) || scanId <= 0)
                    return "ERROR|Invalid scan id";
                if (!int.TryParse(offsetText, out var offset) || offset < 0)
                    offset = 0;
                if (!int.TryParse(limitText, out var limit) || limit <= 0)
                    limit = 200;
                limit = Math.Min(limit, 1200);

                DiscoveryScanCache cache = null;
                lock (_discoverySync)
                {
                    if (!_discoveryScans.TryGetValue(scanId, out cache))
                        return "ERROR|Scan not found";
                }

                var total = cache?.Entries?.Count ?? 0;
                if (total <= 0)
                    return $"DISCOVERY_SCAN_PAGE|scanId={scanId}|offset={offset}|total=0";

                if (offset >= total)
                    return $"DISCOVERY_SCAN_PAGE|scanId={scanId}|offset={offset}|total={total}";

                var end = Math.Min(total, offset + limit);
                var sb = new StringBuilder();
                sb.Append("DISCOVERY_SCAN_PAGE");
                sb.Append("|scanId=");
                sb.Append(scanId);
                sb.Append("|offset=");
                sb.Append(offset);
                sb.Append("|total=");
                sb.Append(total);

                for (var i = offset; i < end; i++)
                {
                    var d = cache.Entries[i];
                    if (d == null)
                        continue;
                    sb.Append("|k=");
                    sb.Append(SanitizePipeValue(d.Key ?? ""));
                    sb.Append(";fp=");
                    sb.Append(SanitizePipeValue(d.Fingerprint ?? ""));
                    sb.Append(";kind=");
                    sb.Append((int)d.Kind);
                    sb.Append(";go=");
                    sb.Append(SanitizePipeValue(d.GameObjectName ?? ""));
                    sb.Append(";path=");
                    sb.Append(SanitizePipeValue(d.GameObjectPath ?? ""));
                    sb.Append(";comp=");
                    sb.Append(SanitizePipeValue(d.ComponentTypeName ?? ""));
                    sb.Append(";decl=");
                    sb.Append(SanitizePipeValue(d.DeclaringTypeName ?? ""));
                    sb.Append(";mem=");
                    sb.Append(SanitizePipeValue(d.MemberName ?? ""));
                    sb.Append(";mt=");
                    sb.Append(SanitizePipeValue(d.MemberTypeName ?? ""));
                    sb.Append(";rw=");
                    sb.Append(d.CanWrite ? "1" : "0");
                    sb.Append(";st=");
                    sb.Append(d.IsStatic ? "1" : "0");
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DiscoverySnapshot(string[] parts)
        {
            try
            {
                if (parts == null || parts.Length < 3)
                    return "ERROR|Invalid snapshot format";
                if (!long.TryParse(parts[1], out var scanId) || scanId <= 0)
                    return "ERROR|Invalid scan id";

                DiscoveryScanCache cache = null;
                lock (_discoverySync)
                {
                    if (!_discoveryScans.TryGetValue(scanId, out cache))
                        return "ERROR|Scan not found";
                }

                var sb = new StringBuilder();
                sb.Append("DISCOVERY_SNAPSHOT");
                sb.Append("|scanId=");
                sb.Append(scanId);
                sb.Append("|ticks=");
                sb.Append(Stopwatch.GetTimestamp());

                var emitted = 0;
                for (var i = 2; i < parts.Length; i++)
                {
                    var key = parts[i];
                    if (IsNullOrWhiteSpace(key))
                        continue;
                    if (cache.ByKey == null || !cache.ByKey.TryGetValue(key, out var d) || d == null)
                        continue;

                    if (!TryReadDiscoveryCandidateValue(d, out var valueText))
                        continue;

                    sb.Append("|k=");
                    sb.Append(SanitizePipeValue(key));
                    sb.Append(";kind=");
                    sb.Append((int)d.Kind);
                    sb.Append(";v=");
                    sb.Append(SanitizePipeValue(valueText ?? "null"));
                    emitted++;
                    if (emitted >= 1200)
                        break;
                }

                sb.Append("|count=");
                sb.Append(emitted);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DiscoveryObserveStart(string[] parts)
        {
            try
            {
                if (parts == null || parts.Length < 5)
                    return "ERROR|Invalid observe start format";
                if (!long.TryParse(parts[1], out var scanId) || scanId <= 0)
                    return "ERROR|Invalid scan id";
                if (!int.TryParse(parts[2], out var intervalMs) || intervalMs <= 0)
                    intervalMs = 100;
                intervalMs = Math.Max(20, Math.Min(intervalMs, 5000));
                if (!int.TryParse(parts[3], out var maxSamples) || maxSamples <= 0)
                    maxSamples = 600;
                maxSamples = Math.Max(50, Math.Min(maxSamples, 5000));
                var summaryOnly = false;
                var perTickKeys = 450;

                DiscoveryScanCache cache = null;
                lock (_discoverySync)
                {
                    if (!_discoveryScans.TryGetValue(scanId, out cache))
                        return "ERROR|Scan not found";
                }

                var keys = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 4; i < parts.Length; i++)
                {
                    var k = parts[i];
                    if (IsNullOrWhiteSpace(k))
                        continue;
                    if (k.IndexOf('=') > 0)
                    {
                        var eq = k.IndexOf('=');
                        var name = k.Substring(0, eq).Trim();
                        var val = k.Substring(eq + 1).Trim();
                        if (name.Equals("mode", StringComparison.OrdinalIgnoreCase) && val.Equals("summary", StringComparison.OrdinalIgnoreCase))
                            summaryOnly = true;
                        else if (name.Equals("summary", StringComparison.OrdinalIgnoreCase) && (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase)))
                            summaryOnly = true;
                        else if (name.Equals("perTickKeys", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var ptk) && ptk > 0)
                            perTickKeys = Math.Max(10, Math.Min(ptk, 5000));
                        continue;
                    }
                    if (cache.ByKey == null || !cache.ByKey.ContainsKey(k))
                        continue;
                    keys.Add(k);
                    if (keys.Count >= 4000)
                        break;
                }
                if (keys.Count == 0)
                    return "ERROR|No keys";

                var sessionId = Interlocked.Increment(ref _discoveryNextSessionId);
                var list = keys.ToList();
                var session = new DiscoveryObservationSession
                {
                    SessionId = sessionId,
                    ScanId = scanId,
                    IntervalMs = intervalMs,
                    Keys = keys,
                    KeyList = list,
                    Cursor = 0,
                    PerTickKeys = Math.Max(10, Math.Min(perTickKeys, keys.Count)),
                    SummaryOnly = summaryOnly,
                    Stats = summaryOnly ? new Dictionary<string, DiscoveryObservationStats>(Math.Min(keys.Count, 1024), StringComparer.Ordinal) : null,
                    SamplesTaken = 0,
                    Completed = false,
                    ExpiresAtTicks = 0,
                    NextSampleTicks = Stopwatch.GetTimestamp(),
                    NextSeq = 0,
                    LastPulledSeq = 0,
                    MaxSamples = maxSamples,
                    Samples = summaryOnly ? null : new Queue<DiscoverySample>(Math.Min(maxSamples, 1024)),
                    Events = new Queue<string>(256)
                };

                lock (_discoverySync)
                {
                    _discoverySessions[sessionId] = session;
                }

                return $"DISCOVERY_OBSERVE_START|sessionId={sessionId}|scanId={scanId}|intervalMs={intervalMs}|keys={keys.Count}|maxSamples={maxSamples}|mode={(summaryOnly ? "summary" : "raw")}|perTickKeys={session.PerTickKeys}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DiscoveryObserveStop(string sessionIdText)
        {
            try
            {
                if (!long.TryParse(sessionIdText, out var sessionId) || sessionId <= 0)
                    return "ERROR|Invalid session id";
                lock (_discoverySync)
                {
                    if (_discoverySessions.Remove(sessionId))
                        return $"DISCOVERY_OBSERVE_STOP|sessionId={sessionId}|stopped=1";
                }
                return $"DISCOVERY_OBSERVE_STOP|sessionId={sessionId}|stopped=0";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DiscoveryObservePull(string[] parts)
        {
            try
            {
                if (parts == null || parts.Length < 2)
                    return "ERROR|Invalid observe pull format";
                if (!long.TryParse(parts[1], out var sessionId) || sessionId <= 0)
                    return "ERROR|Invalid session id";

                var sinceSeq = 0L;
                if (parts.Length > 2 && !IsNullOrWhiteSpace(parts[2]))
                    long.TryParse(parts[2], out sinceSeq);

                var max = 120;
                if (parts.Length > 3 && !IsNullOrWhiteSpace(parts[3]) && int.TryParse(parts[3], out var m) && m > 0)
                    max = Math.Min(m, 800);

                DiscoveryObservationSession session = null;
                lock (_discoverySync)
                {
                    if (!_discoverySessions.TryGetValue(sessionId, out session))
                        return "ERROR|Session not found";
                }

                var sb = new StringBuilder();
                sb.Append("DISCOVERY_OBSERVE_PULL");
                sb.Append("|sessionId=");
                sb.Append(sessionId);
                sb.Append("|ticks=");
                sb.Append(Stopwatch.GetTimestamp());

                var evts = new List<string>(64);
                var samples = new List<DiscoverySample>(Math.Min(max, 256));
                lock (_discoverySync)
                {
                    if (session.Events != null)
                    {
                        while (session.Events.Count > 0 && evts.Count < 120)
                            evts.Add(session.Events.Dequeue());
                    }
                    if (session.Samples != null && session.Samples.Count > 0)
                    {
                        foreach (var s in session.Samples)
                        {
                            if (s.Seq <= sinceSeq)
                                continue;
                            samples.Add(s);
                            if (samples.Count >= max)
                                break;
                        }
                    }
                }

                sb.Append("|events=");
                sb.Append(evts.Count);
                for (var i = 0; i < evts.Count; i++)
                {
                    sb.Append("|e=");
                    sb.Append(SanitizePipeValue(evts[i] ?? ""));
                }

                sb.Append("|samples=");
                sb.Append(samples.Count);
                var lastSeq = sinceSeq;
                for (var i = 0; i < samples.Count; i++)
                {
                    var s = samples[i];
                    if (s.Values == null || s.Values.Count == 0)
                        continue;
                    sb.Append("|s=seq:");
                    sb.Append(s.Seq);
                    sb.Append(";t:");
                    sb.Append(s.Ticks);
                    var emitted = 0;
                    foreach (var kv in s.Values)
                    {
                        if (kv.Key == null)
                            continue;
                        if (emitted >= 1200)
                            break;
                        sb.Append(";k:");
                        sb.Append(SanitizePipeValue(kv.Key));
                        sb.Append(",v:");
                        sb.Append(SanitizePipeValue(kv.Value ?? "null"));
                        emitted++;
                    }
                    if (s.Seq > lastSeq)
                        lastSeq = s.Seq;
                }

                sb.Append("|lastSeq=");
                sb.Append(lastSeq);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DiscoveryObserveStatus(string sessionIdText)
        {
            try
            {
                if (!long.TryParse(sessionIdText, out var sessionId) || sessionId <= 0)
                    return "ERROR|Invalid session id";

                DiscoveryObservationSession session = null;
                lock (_discoverySync)
                {
                    if (!_discoverySessions.TryGetValue(sessionId, out session))
                        return "ERROR|Session not found";
                }

                var samples = session.SummaryOnly ? session.SamplesTaken : (session.Samples?.Count ?? 0);
                var max = session.MaxSamples <= 0 ? 1 : session.MaxSamples;
                var progress = Math.Max(0, Math.Min(1, samples / (double)max));

                return $"DISCOVERY_OBSERVE_STATUS|sessionId={sessionId}|mode={(session.SummaryOnly ? "summary" : "raw")}|running={(session.Completed ? 0 : 1)}|done={(session.Completed ? 1 : 0)}|samples={samples}|maxSamples={max}|progress={progress.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}|keys={(session.Keys?.Count ?? 0)}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DiscoveryObserveSummary(string sessionIdText)
        {
            try
            {
                if (!long.TryParse(sessionIdText, out var sessionId) || sessionId <= 0)
                    return "ERROR|Invalid session id";

                DiscoveryObservationSession session = null;
                lock (_discoverySync)
                {
                    if (!_discoverySessions.TryGetValue(sessionId, out session))
                        return "ERROR|Session not found";
                }

                if (!session.SummaryOnly || session.Stats == null)
                    return "ERROR|Session not summary mode";

                var sb = new StringBuilder();
                sb.Append("DISCOVERY_OBSERVE_SUMMARY");
                sb.Append("|sessionId=");
                sb.Append(sessionId);
                sb.Append("|ticks=");
                sb.Append(Stopwatch.GetTimestamp());
                sb.Append("|samples=");
                sb.Append(session.SamplesTaken);
                sb.Append("|keys=");
                sb.Append(session.Keys?.Count ?? 0);
                sb.Append("|done=");
                sb.Append(session.Completed ? "1" : "0");

                var emitted = 0;
                foreach (var kv in session.Stats)
                {
                    if (kv.Key == null)
                        continue;
                    if (emitted >= 1200)
                        break;
                    var st = kv.Value;
                    if (st.Count <= 0)
                        continue;
                    var varText = st.Variance.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    var meanText = st.Mean.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    var minText = st.Min.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    var maxText = st.Max.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    var updRate = st.Count > 1 ? (st.Updates / (double)(st.Count - 1)) : 0;
                    var intRate = st.Count > 0 ? (st.IntCount / (double)st.Count) : 0;
                    sb.Append("|k=");
                    sb.Append(SanitizePipeValue(kv.Key));
                    sb.Append(";n=");
                    sb.Append(st.Count);
                    sb.Append(";min=");
                    sb.Append(minText);
                    sb.Append(";max=");
                    sb.Append(maxText);
                    sb.Append(";mean=");
                    sb.Append(meanText);
                    sb.Append(";var=");
                    sb.Append(varText);
                    sb.Append(";upd=");
                    sb.Append(updRate.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(";int=");
                    sb.Append(intRate.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    emitted++;
                }

                sb.Append("|count=");
                sb.Append(emitted);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DiscoveryMarkEvent(string sessionIdText, string eventName)
        {
            try
            {
                if (!long.TryParse(sessionIdText, out var sessionId) || sessionId <= 0)
                    return "ERROR|Invalid session id";
                if (IsNullOrWhiteSpace(eventName))
                    return "ERROR|Invalid event name";

                DiscoveryObservationSession session = null;
                lock (_discoverySync)
                {
                    if (!_discoverySessions.TryGetValue(sessionId, out session))
                        return "ERROR|Session not found";
                    if (session.Events == null)
                        session.Events = new Queue<string>(256);
                    while (session.Events.Count > 250)
                        session.Events.Dequeue();
                    session.Events.Enqueue($"t={Stopwatch.GetTimestamp()};name={SanitizePipeValue(eventName)}");
                }

                return $"DISCOVERY_MARK_EVENT|sessionId={sessionId}|ok=1";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static void UpdateDiscoveryObservationSessions()
        {
            try
            {
                DiscoveryObservationSession[] sessions;
                lock (_discoverySync)
                {
                    if (_discoverySessions.Count == 0)
                        return;
                    sessions = _discoverySessions.Values.ToArray();
                }

                var now = Stopwatch.GetTimestamp();
                var intervalFloorTicks = Stopwatch.Frequency / 1000;
                for (var i = 0; i < sessions.Length; i++)
                {
                    var session = sessions[i];
                    if (session == null)
                        continue;
                    if (session.IntervalMs <= 0 || session.Keys == null || session.Keys.Count == 0)
                        continue;

                    if (session.Completed)
                    {
                        if (session.ExpiresAtTicks > 0 && now >= session.ExpiresAtTicks)
                        {
                            lock (_discoverySync)
                            {
                                _discoverySessions.Remove(session.SessionId);
                            }
                        }
                        continue;
                    }

                    if (now < session.NextSampleTicks)
                        continue;

                    var nextDeltaTicks = (long)session.IntervalMs * intervalFloorTicks;
                    if (nextDeltaTicks < 1)
                        nextDeltaTicks = 1;
                    session.NextSampleTicks = now + nextDeltaTicks;

                    DiscoveryScanCache cache = null;
                    lock (_discoverySync)
                    {
                        if (!_discoveryScans.TryGetValue(session.ScanId, out cache) || cache == null)
                        {
                            _discoverySessions.Remove(session.SessionId);
                            continue;
                        }
                    }

                    if (session.KeyList == null)
                        session.KeyList = session.Keys.ToList();

                    var keyCount = session.KeyList.Count;
                    if (keyCount == 0)
                        continue;

                    var take = session.PerTickKeys > 0 ? Math.Min(session.PerTickKeys, keyCount) : keyCount;
                    if (session.SummaryOnly)
                    {
                        if (session.Stats == null)
                            session.Stats = new Dictionary<string, DiscoveryObservationStats>(Math.Min(session.Keys.Count, 1024), StringComparer.Ordinal);

                        for (var j = 0; j < take; j++)
                        {
                            var idx = session.Cursor++;
                            if (idx >= keyCount)
                            {
                                session.Cursor = 0;
                                idx = 0;
                            }
                            var key = session.KeyList[idx];
                            if (IsNullOrWhiteSpace(key))
                                continue;
                            if (cache.ByKey == null || !cache.ByKey.TryGetValue(key, out var d) || d == null)
                                continue;
                            if (!TryReadDiscoveryCandidateValue(d, out var valueText))
                                continue;
                            if (!TryParseDouble(valueText, out var v))
                                continue;

                            if (!session.Stats.TryGetValue(key, out var st))
                                st = default;
                            st.Push(v);
                            if (Math.Abs(v - Math.Round(v)) < 1e-6)
                                st.IntCount++;
                            session.Stats[key] = st;
                        }

                        session.SamplesTaken++;
                        if (session.SamplesTaken >= session.MaxSamples)
                        {
                            session.Completed = true;
                            session.ExpiresAtTicks = now + (Stopwatch.Frequency * 30);
                        }

                        if (IsNullOrWhiteSpace(_discoveryOverlayText) || _discoveryOverlayText.StartsWith("Observe", StringComparison.OrdinalIgnoreCase))
                        {
                            var pct = Math.Max(0, Math.Min(1, session.SamplesTaken / (double)Math.Max(1, session.MaxSamples)));
                            _discoveryOverlayText = $"Observe {session.SamplesTaken}/{session.MaxSamples} (keys={session.Keys.Count})";
                            _discoveryOverlayProgress = pct;
                        }
                    }
                    else
                    {
                        var values = new Dictionary<string, string>(Math.Min(take, 512), StringComparer.Ordinal);
                        for (var j = 0; j < take; j++)
                        {
                            var idx = session.Cursor++;
                            if (idx >= keyCount)
                            {
                                session.Cursor = 0;
                                idx = 0;
                            }
                            var key = session.KeyList[idx];
                            if (IsNullOrWhiteSpace(key))
                                continue;
                            if (cache.ByKey == null || !cache.ByKey.TryGetValue(key, out var d) || d == null)
                                continue;
                            if (!TryReadDiscoveryCandidateValue(d, out var valueText))
                                continue;
                            values[key] = valueText ?? "null";
                            if (values.Count >= 5000)
                                break;
                        }

                        var sample = new DiscoverySample
                        {
                            Seq = Interlocked.Increment(ref session.NextSeq),
                            Ticks = now,
                            Values = values
                        };

                        lock (_discoverySync)
                        {
                            if (session.Samples == null)
                                session.Samples = new Queue<DiscoverySample>(Math.Min(session.MaxSamples, 1024));
                            while (session.Samples.Count >= session.MaxSamples)
                                session.Samples.Dequeue();
                            session.Samples.Enqueue(sample);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static string DiscoveryExperimentBegin(string[] parts)
        {
            try
            {
                if (parts == null || parts.Length < 4)
                    return "ERROR|Invalid experiment begin format";
                if (!long.TryParse(parts[1], out var scanId) || scanId <= 0)
                    return "ERROR|Invalid scan id";
                var label = parts[2] ?? "";
                if (IsNullOrWhiteSpace(label))
                    label = "experiment";

                DiscoveryScanCache cache = null;
                lock (_discoverySync)
                {
                    if (!_discoveryScans.TryGetValue(scanId, out cache))
                        return "ERROR|Scan not found";
                }

                var keys = new List<string>(Math.Min(parts.Length - 3, 1024));
                for (var i = 3; i < parts.Length; i++)
                {
                    var k = parts[i];
                    if (IsNullOrWhiteSpace(k))
                        continue;
                    if (cache.ByKey == null || !cache.ByKey.ContainsKey(k))
                        continue;
                    keys.Add(k);
                    if (keys.Count >= 1200)
                        break;
                }

                if (keys.Count == 0)
                    return "ERROR|No keys";

                var before = new Dictionary<string, string>(keys.Count, StringComparer.Ordinal);
                for (var i = 0; i < keys.Count; i++)
                {
                    var k = keys[i];
                    if (cache.ByKey.TryGetValue(k, out var d) && d != null && TryReadDiscoveryCandidateValue(d, out var valueText))
                        before[k] = valueText ?? "null";
                }

                var expId = Interlocked.Increment(ref _discoveryNextExperimentId);
                var session = new DiscoveryExperimentSession
                {
                    ExperimentId = expId,
                    ScanId = scanId,
                    Label = label,
                    StartedTicks = Stopwatch.GetTimestamp(),
                    Keys = keys,
                    BeforeValues = before
                };

                lock (_discoverySync)
                {
                    _discoveryExperiments[expId] = session;
                    if (_discoveryExperiments.Count > 8)
                    {
                        var oldest = _discoveryExperiments.Keys.OrderBy(k => k).Take(Math.Max(1, _discoveryExperiments.Count - 8)).ToArray();
                        for (var i = 0; i < oldest.Length; i++)
                            _discoveryExperiments.Remove(oldest[i]);
                    }
                }

                var sb = new StringBuilder();
                sb.Append("DISCOVERY_EXPERIMENT_BEGIN");
                sb.Append("|expId=");
                sb.Append(expId);
                sb.Append("|scanId=");
                sb.Append(scanId);
                sb.Append("|label=");
                sb.Append(SanitizePipeValue(label));
                sb.Append("|ticks=");
                sb.Append(session.StartedTicks);
                sb.Append("|count=");
                sb.Append(before.Count);
                var emitted = 0;
                foreach (var kv in before)
                {
                    if (emitted++ >= 1200)
                        break;
                    sb.Append("|k=");
                    sb.Append(SanitizePipeValue(kv.Key));
                    sb.Append(";v=");
                    sb.Append(SanitizePipeValue(kv.Value ?? "null"));
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DiscoveryExperimentEnd(string[] parts)
        {
            try
            {
                if (parts == null || parts.Length < 2)
                    return "ERROR|Invalid experiment end format";
                if (!long.TryParse(parts[1], out var expId) || expId <= 0)
                    return "ERROR|Invalid experiment id";

                DiscoveryExperimentSession session = null;
                lock (_discoverySync)
                {
                    if (!_discoveryExperiments.TryGetValue(expId, out session))
                        return "ERROR|Experiment not found";
                }

                DiscoveryScanCache cache = null;
                lock (_discoverySync)
                {
                    if (!_discoveryScans.TryGetValue(session.ScanId, out cache))
                        return "ERROR|Scan not found";
                }

                var changed = new List<string>(256);
                var unchanged = 0;
                var afterTicks = Stopwatch.GetTimestamp();

                for (var i = 0; i < session.Keys.Count; i++)
                {
                    var key = session.Keys[i];
                    if (IsNullOrWhiteSpace(key))
                        continue;
                    if (cache.ByKey == null || !cache.ByKey.TryGetValue(key, out var d) || d == null)
                        continue;
                    if (!TryReadDiscoveryCandidateValue(d, out var afterText))
                        continue;

                    session.BeforeValues.TryGetValue(key, out var beforeText);
                    beforeText = beforeText ?? "null";
                    afterText = afterText ?? "null";

                    if (d.Kind == DiscoveryCandidateKind.Numeric || d.Kind == DiscoveryCandidateKind.CollectionCount)
                    {
                        if (TryParseDouble(beforeText, out var b) && TryParseDouble(afterText, out var a))
                        {
                            var delta = a - b;
                            if (Math.Abs(delta) > 1e-9)
                                changed.Add($"k={SanitizePipeValue(key)};kind={(int)d.Kind};before={b.ToString("R", System.Globalization.CultureInfo.InvariantCulture)};after={a.ToString("R", System.Globalization.CultureInfo.InvariantCulture)};delta={delta.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
                            else
                                unchanged++;
                        }
                        else if (!string.Equals(beforeText, afterText, StringComparison.Ordinal))
                        {
                            changed.Add($"k={SanitizePipeValue(key)};kind={(int)d.Kind};before={SanitizePipeValue(beforeText)};after={SanitizePipeValue(afterText)}");
                        }
                        else
                        {
                            unchanged++;
                        }
                    }
                    else
                    {
                        if (!string.Equals(beforeText, afterText, StringComparison.Ordinal))
                            changed.Add($"k={SanitizePipeValue(key)};kind={(int)d.Kind};before={SanitizePipeValue(beforeText)};after={SanitizePipeValue(afterText)}");
                        else
                            unchanged++;
                    }

                    if (changed.Count >= 1200)
                        break;
                }

                lock (_discoverySync)
                {
                    _discoveryExperiments.Remove(expId);
                }

                var sb = new StringBuilder();
                sb.Append("DISCOVERY_EXPERIMENT_END");
                sb.Append("|expId=");
                sb.Append(expId);
                sb.Append("|scanId=");
                sb.Append(session.ScanId);
                sb.Append("|label=");
                sb.Append(SanitizePipeValue(session.Label ?? ""));
                sb.Append("|ticks=");
                sb.Append(afterTicks);
                sb.Append("|changed=");
                sb.Append(changed.Count);
                sb.Append("|unchanged=");
                sb.Append(unchanged);
                for (var i = 0; i < changed.Count; i++)
                {
                    sb.Append("|c=");
                    sb.Append(changed[i]);
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DiscoveryExperimentCancel(string experimentIdText)
        {
            try
            {
                if (!long.TryParse(experimentIdText, out var expId) || expId <= 0)
                    return "ERROR|Invalid experiment id";
                lock (_discoverySync)
                {
                    if (_discoveryExperiments.Remove(expId))
                        return $"DISCOVERY_EXPERIMENT_CANCEL|expId={expId}|canceled=1";
                }
                return $"DISCOVERY_EXPERIMENT_CANCEL|expId={expId}|canceled=0";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DiscoveryCheatBind(string[] parts)
        {
            try
            {
                if (parts == null || parts.Length < 4)
                    return "ERROR|Invalid cheat bind format";
                if (!long.TryParse(parts[1], out var scanId) || scanId <= 0)
                    return "ERROR|Invalid scan id";
                var key = parts[2] ?? "";
                if (IsNullOrWhiteSpace(key))
                    return "ERROR|Invalid key";
                var modeText = parts[3] ?? "";
                if (IsNullOrWhiteSpace(modeText))
                    return "ERROR|Invalid mode";

                DiscoveryScanCache cache = null;
                DiscoveryCandidateDescriptor d = null;
                lock (_discoverySync)
                {
                    if (!_discoveryScans.TryGetValue(scanId, out cache) || cache == null)
                        return "ERROR|Scan not found";
                    if (cache.ByKey == null || !cache.ByKey.TryGetValue(key, out d) || d == null)
                        return "ERROR|Key not found";
                }

                var mode = DynamicCheatMode.Freeze;
                if (modeText.Equals("freeze", StringComparison.OrdinalIgnoreCase))
                    mode = DynamicCheatMode.Freeze;
                else if (modeText.Equals("autofill", StringComparison.OrdinalIgnoreCase) || modeText.Equals("autorefill", StringComparison.OrdinalIgnoreCase))
                    mode = DynamicCheatMode.AutoRefill;
                else if (modeText.Equals("clampmin", StringComparison.OrdinalIgnoreCase))
                    mode = DynamicCheatMode.ClampMin;
                else if (modeText.Equals("clampmax", StringComparison.OrdinalIgnoreCase))
                    mode = DynamicCheatMode.ClampMax;
                else if (modeText.Equals("nodecrease", StringComparison.OrdinalIgnoreCase))
                    mode = DynamicCheatMode.NoDecrease;
                else
                    return "ERROR|Unknown mode";

                var cheat = new DynamicCheat
                {
                    CheatId = 0,
                    ScanId = scanId,
                    Key = key,
                    Fingerprint = d.Fingerprint,
                    Kind = d.Kind,
                    Component = d.Component,
                    Member = d.Member,
                    IsStatic = d.IsStatic,
                    CanWrite = d.CanWrite,
                    Mode = mode,
                    NumericValue = 0,
                    SecondaryNumericValue = double.NaN,
                    BoolValue = false,
                    EnumValue = 0,
                    MaxKey = null,
                    MaxComponent = null,
                    MaxMember = null,
                    PatchedMethods = null
                };

                var valueArg = GetPipeArg(parts, "value");
                if (IsNullOrWhiteSpace(valueArg) && parts.Length > 4 && !parts[4].Contains("="))
                    valueArg = parts[4];

                if (mode == DynamicCheatMode.Freeze)
                {
                    if (d.Kind == DiscoveryCandidateKind.Numeric || d.Kind == DiscoveryCandidateKind.CollectionCount)
                    {
                        if (!IsNullOrWhiteSpace(valueArg))
                        {
                            if (!TryParseDouble(valueArg, out var v))
                                return "ERROR|Invalid numeric value";
                            cheat.NumericValue = v;
                        }
                        else
                        {
                            if (!TryReadDiscoveryCandidateValue(d, out var vt) || !TryParseDouble(vt, out var v))
                                return "ERROR|Failed to read value";
                            cheat.NumericValue = v;
                        }
                    }
                    else if (d.Kind == DiscoveryCandidateKind.Boolean)
                    {
                        if (!IsNullOrWhiteSpace(valueArg))
                        {
                            cheat.BoolValue = valueArg == "1" || valueArg.Equals("true", StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            if (!TryReadDiscoveryCandidateValue(d, out var vt))
                                return "ERROR|Failed to read value";
                            cheat.BoolValue = vt == "1";
                        }
                    }
                    else if (d.Kind == DiscoveryCandidateKind.Enum)
                    {
                        if (!IsNullOrWhiteSpace(valueArg))
                        {
                            if (!long.TryParse(valueArg, out var v))
                                return "ERROR|Invalid enum value";
                            cheat.EnumValue = v;
                        }
                        else
                        {
                            if (!TryReadDiscoveryCandidateValue(d, out var vt) || !long.TryParse(vt, out var v))
                                return "ERROR|Failed to read value";
                            cheat.EnumValue = v;
                        }
                    }
                }
                else if (mode == DynamicCheatMode.ClampMin || mode == DynamicCheatMode.ClampMax)
                {
                    if (!TryParseDouble(valueArg, out var v))
                        return "ERROR|Missing clamp value";
                    cheat.NumericValue = v;
                }
                else if (mode == DynamicCheatMode.AutoRefill)
                {
                    var thresholdText = GetPipeArg(parts, "threshold");
                    if (!TryParseDouble(thresholdText, out var threshold) || threshold <= 0)
                        threshold = 0.95;
                    cheat.NumericValue = threshold;

                    var maxKey = GetPipeArg(parts, "maxKey");
                    if (IsNullOrWhiteSpace(maxKey))
                        maxKey = GetPipeArg(parts, "max");
                    if (!IsNullOrWhiteSpace(maxKey))
                    {
                        if (cache.ByKey == null || !cache.ByKey.TryGetValue(maxKey, out var maxDesc) || maxDesc == null)
                            return "ERROR|maxKey not found";
                        cheat.MaxKey = maxKey;
                        cheat.MaxComponent = maxDesc.Component;
                        cheat.MaxMember = maxDesc.Member;
                    }
                    else
                    {
                        var maxValueText = GetPipeArg(parts, "maxValue");
                        if (!TryParseDouble(maxValueText, out var maxV) || maxV <= 0)
                            return "ERROR|Missing maxKey/maxValue";
                        cheat.SecondaryNumericValue = maxV;
                    }
                }
                else if (mode == DynamicCheatMode.NoDecrease)
                {
                    if (d.Kind != DiscoveryCandidateKind.Numeric)
                        return "ERROR|NoDecrease requires numeric";
                    if (!TryReadDiscoveryCandidateValue(d, out var vt) || !TryParseDouble(vt, out var v))
                        return "ERROR|Failed to read value";
                    cheat.NumericValue = v;
                    cheat.LastResolvedTicks = Stopwatch.GetTimestamp();
                    if (d.Member is FieldInfo field)
                    {
                        if (TryPatchNoDecrease(field, out var patched))
                            cheat.PatchedMethods = patched;
                    }
                }

                var cheatId = Interlocked.Increment(ref _dynamicCheatNextId);
                cheat.CheatId = cheatId;

                lock (_discoverySync)
                {
                    _dynamicCheats[cheatId] = cheat;
                }

                var patchedCount = cheat.PatchedMethods != null ? cheat.PatchedMethods.Count : 0;
                return $"DISCOVERY_CHEAT_BIND|id={cheatId}|scanId={scanId}|mode={(int)mode}|kind={(int)cheat.Kind}|patched={patchedCount}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DiscoveryCheatUnbind(string cheatIdText)
        {
            try
            {
                if (!long.TryParse(cheatIdText, out var cheatId) || cheatId <= 0)
                    return "ERROR|Invalid cheat id";

                DynamicCheat cheat = null;
                lock (_discoverySync)
                {
                    if (_dynamicCheats.TryGetValue(cheatId, out cheat))
                        _dynamicCheats.Remove(cheatId);
                }

                if (cheat == null)
                    return $"DISCOVERY_CHEAT_UNBIND|id={cheatId}|removed=0";

                if (cheat.PatchedMethods != null && cheat.PatchedMethods.Count > 0)
                {
                    try
                    {
                        var harmony = new HarmonyLib.Harmony(CheatHarmonyId);
                        for (var i = 0; i < cheat.PatchedMethods.Count; i++)
                        {
                            var m = cheat.PatchedMethods[i];
                            if (m == null)
                                continue;
                            try { harmony.Unpatch(m, HarmonyPatchType.All, CheatHarmonyId); } catch { }
                            lock (_discoverySync) { _noDecreaseMethodToField.Remove(m); }
                        }
                    }
                    catch
                    {
                    }
                }

                return $"DISCOVERY_CHEAT_UNBIND|id={cheatId}|removed=1";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DiscoveryCheatList()
        {
            try
            {
                DynamicCheat[] cheats;
                lock (_discoverySync)
                {
                    cheats = _dynamicCheats.Values.ToArray();
                }

                var sb = new StringBuilder();
                sb.Append("DISCOVERY_CHEAT_LIST");
                sb.Append("|count=");
                sb.Append(cheats.Length);
                for (var i = 0; i < cheats.Length; i++)
                {
                    var c = cheats[i];
                    if (c == null)
                        continue;
                    sb.Append("|c=id:");
                    sb.Append(c.CheatId);
                    sb.Append(";mode:");
                    sb.Append((int)c.Mode);
                    sb.Append(";kind:");
                    sb.Append((int)c.Kind);
                    sb.Append(";key:");
                    sb.Append(SanitizePipeValue(c.Key ?? ""));
                    sb.Append(";fp:");
                    sb.Append(SanitizePipeValue(c.Fingerprint ?? ""));
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DiscoveryWriteProbe(string[] parts)
        {
            try
            {
                if (parts == null || parts.Length < 3)
                    return "ERROR|Invalid write probe format";
                if (!long.TryParse(parts[1], out var scanId) || scanId <= 0)
                    return "ERROR|Invalid scan id";
                var key = parts[2] ?? "";
                if (IsNullOrWhiteSpace(key))
                    return "ERROR|Invalid key";

                var delayMs = 250;
                var delayText = GetPipeArg(parts, "delayMs");
                if (!IsNullOrWhiteSpace(delayText) && int.TryParse(delayText, out var dm))
                    delayMs = Math.Max(0, Math.Min(1200, dm));

                var revert = true;
                var revertText = GetPipeArg(parts, "revert");
                if (!IsNullOrWhiteSpace(revertText) && (revertText == "0" || revertText.Equals("false", StringComparison.OrdinalIgnoreCase)))
                    revert = false;

                DiscoveryScanCache cache = null;
                DiscoveryCandidateDescriptor d = null;
                lock (_discoverySync)
                {
                    if (!_discoveryScans.TryGetValue(scanId, out cache) || cache == null)
                        return "ERROR|Scan not found";
                    if (cache.ByKey == null || !cache.ByKey.TryGetValue(key, out d) || d == null)
                        return "ERROR|Key not found";
                }

                if (d.IsStatic)
                    return "ERROR|Static not supported";
                if (!d.CanWrite)
                    return "ERROR|Not writable";
                if (d.Kind == DiscoveryCandidateKind.CollectionCount)
                    return "ERROR|Count not supported";

                object instance = null;
                try { instance = d.Component?.Target; } catch { instance = null; }
                if (instance == null)
                    return "ERROR|Target missing";

                var mag = double.NaN;
                var magText = GetPipeArg(parts, "mag");
                if (!IsNullOrWhiteSpace(magText) && TryParseDouble(magText, out var mv))
                    mag = mv;

                var beforeText = "";
                var after0Text = "";
                var afterText = "";
                var afterRevertText = "";

                var wrote = false;
                var reverted = false;

                var sticky = false;
                var rubber = false;
                var clamped = false;
                var score = 0d;

                var probeValue = double.NaN;
                if (d.Kind == DiscoveryCandidateKind.Numeric)
                {
                    if (!TryReadDiscoveryCandidateValue(d, out beforeText) || !TryParseDouble(beforeText, out var before))
                        return "ERROR|Failed to read before";

                    if (double.IsNaN(mag) || Math.Abs(mag) < 1e-9)
                    {
                        var abs = Math.Abs(before);
                        mag = abs <= 1 ? 1 : Math.Min(50, Math.Max(1, abs * 0.05));
                    }

                    probeValue = before + mag;
                    if (!TryWriteNumericMemberValue(instance, d.Member, d.IsStatic, probeValue))
                        return "ERROR|Write failed";
                    wrote = true;

                    TryReadDiscoveryCandidateValue(d, out after0Text);

                    if (delayMs > 0)
                        Thread.Sleep(delayMs);
                    TryReadDiscoveryCandidateValue(d, out afterText);

                    var tol = GuessNumericTolerance(d.MemberTypeName, before, probeValue);
                    if (TryParseDouble(after0Text, out var a0) && Math.Abs(a0 - probeValue) <= tol)
                        score = 0.65;
                    if (TryParseDouble(afterText, out var a1))
                    {
                        sticky = Math.Abs(a1 - probeValue) <= tol;
                        rubber = Math.Abs(a1 - before) <= tol && TryParseDouble(after0Text, out a0) && Math.Abs(a0 - probeValue) <= tol;
                        clamped = TryParseDouble(after0Text, out a0) && Math.Abs(a0 - probeValue) > (tol * 2) && Math.Abs(a0 - before) > tol;
                        if (sticky)
                            score = 1.0;
                        else if (rubber)
                            score = Math.Min(score, 0.25);
                        else if (clamped)
                            score = Math.Min(score, 0.20);
                        else if (score <= 1e-9 && Math.Abs(a1 - before) > tol)
                            score = 0.40;
                    }

                    if (revert)
                    {
                        if (TryWriteNumericMemberValue(instance, d.Member, d.IsStatic, before))
                        {
                            reverted = true;
                            TryReadDiscoveryCandidateValue(d, out afterRevertText);
                        }
                    }
                }
                else if (d.Kind == DiscoveryCandidateKind.Boolean)
                {
                    if (!TryReadDiscoveryCandidateValue(d, out beforeText))
                        return "ERROR|Failed to read before";

                    var before = beforeText == "1";
                    var probe = !before;

                    if (!TryWriteBoolMemberValue(instance, d.Member, d.IsStatic, probe))
                        return "ERROR|Write failed";
                    wrote = true;

                    TryReadDiscoveryCandidateValue(d, out after0Text);
                    if (delayMs > 0)
                        Thread.Sleep(delayMs);
                    TryReadDiscoveryCandidateValue(d, out afterText);

                    sticky = afterText == (probe ? "1" : "0");
                    rubber = after0Text == (probe ? "1" : "0") && afterText == (before ? "1" : "0");
                    score = sticky ? 1.0 : (rubber ? 0.2 : 0.5);

                    if (revert)
                    {
                        if (TryWriteBoolMemberValue(instance, d.Member, d.IsStatic, before))
                        {
                            reverted = true;
                            TryReadDiscoveryCandidateValue(d, out afterRevertText);
                        }
                    }
                }
                else if (d.Kind == DiscoveryCandidateKind.Enum)
                {
                    if (!TryReadDiscoveryCandidateValue(d, out beforeText) || !long.TryParse(beforeText, out var before))
                        return "ERROR|Failed to read before";

                    var probe = before + 1;
                    if (!TryWriteEnumMemberValue(instance, d.Member, d.IsStatic, probe))
                        return "ERROR|Write failed";
                    wrote = true;

                    TryReadDiscoveryCandidateValue(d, out after0Text);
                    if (delayMs > 0)
                        Thread.Sleep(delayMs);
                    TryReadDiscoveryCandidateValue(d, out afterText);

                    sticky = afterText == probe.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    rubber = after0Text == probe.ToString(System.Globalization.CultureInfo.InvariantCulture) && afterText == before.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    score = sticky ? 1.0 : (rubber ? 0.2 : 0.5);

                    if (revert)
                    {
                        if (TryWriteEnumMemberValue(instance, d.Member, d.IsStatic, before))
                        {
                            reverted = true;
                            TryReadDiscoveryCandidateValue(d, out afterRevertText);
                        }
                    }
                }
                else
                {
                    return "ERROR|Unsupported kind";
                }

                var sb = new StringBuilder();
                sb.Append("DISCOVERY_WRITE_PROBE");
                sb.Append("|scanId=");
                sb.Append(scanId);
                sb.Append("|key=");
                sb.Append(SanitizePipeValue(key));
                sb.Append("|fp=");
                sb.Append(SanitizePipeValue(d.Fingerprint ?? ""));
                sb.Append("|kind=");
                sb.Append((int)d.Kind);
                sb.Append("|delayMs=");
                sb.Append(delayMs);
                sb.Append("|wrote=");
                sb.Append(wrote ? "1" : "0");
                sb.Append("|reverted=");
                sb.Append(reverted ? "1" : "0");
                sb.Append("|sticky=");
                sb.Append(sticky ? "1" : "0");
                sb.Append("|rubber=");
                sb.Append(rubber ? "1" : "0");
                sb.Append("|clamp=");
                sb.Append(clamped ? "1" : "0");
                sb.Append("|score=");
                sb.Append(Clamp01(score).ToString("R", System.Globalization.CultureInfo.InvariantCulture));

                if (!IsNullOrWhiteSpace(beforeText))
                {
                    sb.Append("|before=");
                    sb.Append(SanitizePipeValue(beforeText));
                }
                if (!IsNullOrWhiteSpace(after0Text))
                {
                    sb.Append("|after0=");
                    sb.Append(SanitizePipeValue(after0Text));
                }
                if (!IsNullOrWhiteSpace(afterText))
                {
                    sb.Append("|after=");
                    sb.Append(SanitizePipeValue(afterText));
                }
                if (!IsNullOrWhiteSpace(afterRevertText))
                {
                    sb.Append("|afterRevert=");
                    sb.Append(SanitizePipeValue(afterRevertText));
                }

                if (!double.IsNaN(probeValue) && !double.IsInfinity(probeValue))
                {
                    sb.Append("|probe=");
                    sb.Append(probeValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private static double GuessNumericTolerance(string memberTypeName, double before, double probe)
        {
            var mt = (memberTypeName ?? "").ToLowerInvariant();
            var isIntLike =
                mt.Contains("int") ||
                mt.Contains("uint") ||
                mt.Contains("short") ||
                mt.Contains("ushort") ||
                mt.Contains("byte") ||
                mt.Contains("sbyte") ||
                mt.Contains("long") ||
                mt.Contains("ulong");

            if (isIntLike)
                return 0.75;

            var delta = Math.Abs(probe - before);
            return Math.Max(0.001, Math.Min(1.0, delta * 0.02));
        }

        private static double Clamp01(double v)
        {
            if (v < 0)
                return 0;
            if (v > 1)
                return 1;
            return v;
        }

        private static void NoDecreasePrefix(object __instance, MethodBase __originalMethod, ref double __state)
        {
            __state = double.NaN;
            if (__originalMethod == null)
                return;

            FieldInfo field = null;
            lock (_discoverySync)
            {
                if (!_noDecreaseMethodToField.TryGetValue(__originalMethod, out field))
                    return;
            }
            if (field == null)
                return;

            object target = field.IsStatic ? null : __instance;
            if (!field.IsStatic && target == null)
                return;

            try
            {
                var raw = field.GetValue(target);
                if (TryConvertNumericToDouble(raw, out var v))
                    __state = v;
            }
            catch
            {
            }
        }

        private static void NoDecreasePostfix(object __instance, MethodBase __originalMethod, double __state)
        {
            if (double.IsNaN(__state) || __originalMethod == null)
                return;

            FieldInfo field = null;
            lock (_discoverySync)
            {
                if (!_noDecreaseMethodToField.TryGetValue(__originalMethod, out field))
                    return;
            }
            if (field == null)
                return;

            object target = field.IsStatic ? null : __instance;
            if (!field.IsStatic && target == null)
                return;

            try
            {
                var raw = field.GetValue(target);
                if (!TryConvertNumericToDouble(raw, out var v))
                    return;
                if (v + 1e-9 < __state)
                {
                    var converted = ConvertDoubleToNumeric(__state, field.FieldType);
                    field.SetValue(target, converted);
                }
            }
            catch
            {
            }
        }

        private static bool TryPatchNoDecrease(FieldInfo field, out List<MethodBase> patchedMethods)
        {
            patchedMethods = null;
            if (field == null)
                return false;
            if (!_isMono)
                return false;

            var decl = field.DeclaringType;
            if (decl == null)
                return false;

            var methods = new List<MethodInfo>();
            try
            {
                methods.AddRange(decl.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static));
            }
            catch
            {
                return false;
            }

            var harmony = new HarmonyLib.Harmony(CheatHarmonyId);
            var prefix = typeof(UniversalMelonMod).GetMethod(nameof(NoDecreasePrefix), BindingFlags.NonPublic | BindingFlags.Static);
            var postfix = typeof(UniversalMelonMod).GetMethod(nameof(NoDecreasePostfix), BindingFlags.NonPublic | BindingFlags.Static);
            if (prefix == null || postfix == null)
                return false;

            var patched = new List<MethodBase>(32);
            var token = field.MetadataToken;
            var budget = 12000;
            for (var i = 0; i < methods.Count && patched.Count < 60 && budget-- > 0; i++)
            {
                var m = methods[i];
                if (m == null || m.IsAbstract || m.ContainsGenericParameters)
                    continue;

                if (!MethodBodyContainsFieldStore(m, token))
                    continue;

                try
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
                    patched.Add(m);
                    lock (_discoverySync)
                    {
                        _noDecreaseMethodToField[m] = field;
                    }
                }
                catch
                {
                }
            }

            if (patched.Count == 0)
                return false;

            patchedMethods = patched;
            return true;
        }

        private static bool MethodBodyContainsFieldStore(MethodInfo method, int fieldToken)
        {
            try
            {
                var body = method.GetMethodBody();
                if (body == null)
                    return false;
                var il = body.GetILAsByteArray();
                if (il == null || il.Length == 0)
                    return false;

                var i = 0;
                while (i < il.Length)
                {
                    OpCode op;
                    var b = il[i++];
                    if (b == 0xFE)
                    {
                        if (i >= il.Length)
                            break;
                        var b2 = il[i++];
                        op = GetOpCode((ushort)(0xFE00 | b2));
                    }
                    else
                    {
                        op = GetOpCode(b);
                    }

                    var operandSize = GetOperandSize(op.OperandType, il, i);
                    if ((op == OpCodes.Stfld || op == OpCodes.Stsfld) && operandSize >= 4 && i + 4 <= il.Length)
                    {
                        var token = il[i] | (il[i + 1] << 8) | (il[i + 2] << 16) | (il[i + 3] << 24);
                        if (token == fieldToken)
                            return true;
                    }

                    i += operandSize;
                }
            }
            catch
            {
            }

            return false;
        }

        private static OpCode GetOpCode(ushort value)
        {
            EnsureOpCodeMap();
            if (_ilOpCodeMap.TryGetValue(value, out var op))
                return op;
            return OpCodes.Nop;
        }

        private static int GetOperandSize(OperandType operandType, byte[] il, int index)
        {
            switch (operandType)
            {
                case OperandType.InlineNone:
                    return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    return 1;
                case OperandType.InlineVar:
                    return 2;
                case OperandType.InlineI:
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR:
                    return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    return 8;
                case OperandType.InlineSwitch:
                    if (il == null || index + 4 > il.Length)
                        return 0;
                    var count = il[index] | (il[index + 1] << 8) | (il[index + 2] << 16) | (il[index + 3] << 24);
                    if (count < 0)
                        count = 0;
                    return 4 + (count * 4);
                default:
                    return 0;
            }
        }

        private static Dictionary<ushort, OpCode> _ilOpCodeMap;
        private static void EnsureOpCodeMap()
        {
            if (_ilOpCodeMap != null)
                return;
            var map = new Dictionary<ushort, OpCode>();
            var fields = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static);
            for (var i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                if (f == null || f.FieldType != typeof(OpCode))
                    continue;
                var op = (OpCode)f.GetValue(null);
                map[(ushort)op.Value] = op;
            }
            _ilOpCodeMap = map;
        }

        private static void CollectDiscoveryCandidatesOnComponent(
            object component,
            int trackedId,
            string componentTypeName,
            string goName,
            string goPath,
            HashSet<DiscoveryCandidateKind> kinds,
            List<DiscoveryCandidateDescriptor> entries,
            Dictionary<string, DiscoveryCandidateDescriptor> byKey,
            ref int budget,
            int max)
        {
            if (component == null || entries == null || byKey == null || entries.Count >= max || budget <= 0)
                return;

            var componentType = component.GetType();

            for (var cur = componentType; cur != null && cur != typeof(object) && entries.Count < max && budget > 0; cur = cur.BaseType)
            {
                FieldInfo[] fields = null;
                try { fields = cur.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); } catch { fields = null; }
                if (fields != null)
                {
                    for (var i = 0; i < fields.Length && entries.Count < max && budget > 0; i++)
                    {
                        budget--;
                        var f = fields[i];
                        if (f == null || f.IsLiteral)
                            continue;
                        if (!TryGetDiscoveryKindForType(f.FieldType, out var kind))
                            continue;
                        if (!kinds.Contains(kind))
                            continue;
                        var key = BuildDiscoveryKey(trackedId, componentTypeName, cur.FullName ?? cur.Name ?? "", f.Name ?? "", (int)kind);
                        if (byKey.ContainsKey(key))
                            continue;
                        var mt = f.FieldType?.FullName ?? f.FieldType?.Name ?? "";
                        var fp = ComputeDiscoveryFingerprint(componentTypeName, cur.FullName ?? cur.Name ?? "", f.Name ?? "", mt, goPath);
                        var d = new DiscoveryCandidateDescriptor
                        {
                            Key = key,
                            Fingerprint = fp,
                            Kind = kind,
                            Component = new WeakReference(component),
                            Member = f,
                            GameObjectName = goName,
                            GameObjectPath = goPath,
                            ComponentTypeName = componentTypeName,
                            DeclaringTypeName = cur.FullName ?? cur.Name ?? "",
                            MemberName = f.Name ?? "",
                            MemberTypeName = mt,
                            IsStatic = f.IsStatic,
                            CanWrite = !f.IsInitOnly
                        };
                        entries.Add(d);
                        byKey[key] = d;
                    }
                }

                PropertyInfo[] props = null;
                try { props = cur.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); } catch { props = null; }
                if (props != null)
                {
                    for (var i = 0; i < props.Length && entries.Count < max && budget > 0; i++)
                    {
                        budget--;
                        var p = props[i];
                        if (p == null || !p.CanRead)
                            continue;
                        var idx = p.GetIndexParameters();
                        if (idx != null && idx.Length != 0)
                            continue;
                        if (!TryGetDiscoveryKindForType(p.PropertyType, out var kind))
                            continue;
                        if (!kinds.Contains(kind))
                            continue;
                        var key = BuildDiscoveryKey(trackedId, componentTypeName, cur.FullName ?? cur.Name ?? "", p.Name ?? "", (int)kind);
                        if (byKey.ContainsKey(key))
                            continue;
                        var mt = p.PropertyType?.FullName ?? p.PropertyType?.Name ?? "";
                        var fp = ComputeDiscoveryFingerprint(componentTypeName, cur.FullName ?? cur.Name ?? "", p.Name ?? "", mt, goPath);
                        var setter = p.GetSetMethod(true);
                        var d = new DiscoveryCandidateDescriptor
                        {
                            Key = key,
                            Fingerprint = fp,
                            Kind = kind,
                            Component = new WeakReference(component),
                            Member = p,
                            GameObjectName = goName,
                            GameObjectPath = goPath,
                            ComponentTypeName = componentTypeName,
                            DeclaringTypeName = cur.FullName ?? cur.Name ?? "",
                            MemberName = p.Name ?? "",
                            MemberTypeName = mt,
                            IsStatic = setter != null && setter.IsStatic,
                            CanWrite = p.CanWrite
                        };
                        entries.Add(d);
                        byKey[key] = d;
                    }
                }
            }
        }

        private static string BuildHierarchyNamePath(object goOrComponent, int maxDepth)
        {
            if (goOrComponent == null)
                return "";
            if (!TryGetTransform(goOrComponent, out var transform) || transform == null)
                return (GetUnityObjectName(goOrComponent) ?? "");

            var transformType = transform.GetType();
            var parentProp = GetPropertyCached(transformType, "parent");
            var goProp = GetPropertyCached(transformType, "gameObject");

            var names = new List<string>(32);
            object current = transform;
            var depth = 0;
            while (current != null && depth++ < maxDepth)
            {
                object currentGo = null;
                try { currentGo = goProp?.GetValueCompat(current) ?? TryGetTransformGameObject(current); } catch { currentGo = null; }
                if (currentGo != null)
                    names.Add(GetUnityObjectName(currentGo) ?? "");

                try { current = parentProp?.GetValueCompat(current); } catch { current = null; }
            }

            if (names.Count == 0)
                return (GetUnityObjectName(goOrComponent) ?? "");

            names.Reverse();
            var sb = new StringBuilder();
            for (var i = 0; i < names.Count; i++)
            {
                if (i != 0)
                    sb.Append('/');
                sb.Append(names[i]);
            }
            return sb.ToString();
        }

        private static bool TryGetDiscoveryKindForType(Type t, out DiscoveryCandidateKind kind)
        {
            kind = DiscoveryCandidateKind.Numeric;
            if (t == null)
                return false;

            try
            {
                if (t.IsEnum)
                {
                    kind = DiscoveryCandidateKind.Enum;
                    return true;
                }
            }
            catch
            {
            }

            if (IsBoolType(t))
            {
                kind = DiscoveryCandidateKind.Boolean;
                return true;
            }

            if (TryGetNumericType(t))
            {
                kind = DiscoveryCandidateKind.Numeric;
                return true;
            }

            if (IsCollectionLikeType(t))
            {
                kind = DiscoveryCandidateKind.CollectionCount;
                return true;
            }

            return false;
        }

        private static bool IsBoolType(Type t)
        {
            if (t == null)
                return false;
            if (t == typeof(bool))
                return true;
            var name = t.FullName ?? t.Name ?? "";
            if (name == "Il2CppSystem.Boolean" || name == "System.Boolean")
                return true;
            return false;
        }

        private static bool IsCollectionLikeType(Type t)
        {
            if (t == null)
                return false;
            if (t == typeof(string))
                return false;
            try
            {
                if (t.IsArray)
                    return true;
            }
            catch
            {
            }

            var fullName = t.FullName ?? t.Name ?? "";
            if (fullName.IndexOf("System.Collections", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            try
            {
                var countProp = t.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                if (countProp != null && countProp.CanRead && countProp.PropertyType == typeof(int) && countProp.GetIndexParameters().Length == 0)
                    return true;
            }
            catch
            {
            }

            try
            {
                var lengthProp = t.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
                if (lengthProp != null && lengthProp.CanRead && lengthProp.PropertyType == typeof(int) && lengthProp.GetIndexParameters().Length == 0)
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static string BuildDiscoveryKey(int trackedId, string componentTypeName, string declaringTypeName, string memberName, int kind)
        {
            var sb = new StringBuilder();
            sb.Append(trackedId);
            sb.Append(':');
            sb.Append(componentTypeName ?? "");
            sb.Append(':');
            sb.Append(declaringTypeName ?? "");
            sb.Append(':');
            sb.Append(memberName ?? "");
            sb.Append(':');
            sb.Append(kind);
            return sb.ToString();
        }

        private static string ComputeDiscoveryFingerprint(string componentTypeName, string declaringTypeName, string memberName, string memberTypeName, string goPath)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                h = Fnva64Step(h, componentTypeName);
                h = Fnva64Step(h, "|");
                h = Fnva64Step(h, declaringTypeName);
                h = Fnva64Step(h, "|");
                h = Fnva64Step(h, memberName);
                h = Fnva64Step(h, "|");
                h = Fnva64Step(h, memberTypeName);
                h = Fnva64Step(h, "|");
                h = Fnva64Step(h, goPath);
                return h.ToString("X16");
            }
        }

        private static ulong Fnva64Step(ulong h, string s)
        {
            if (s == null)
                return h;
            for (var i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 1099511628211UL;
            }
            return h;
        }

        private static bool TryReadDiscoveryCandidateValue(DiscoveryCandidateDescriptor d, out string valueText)
        {
            valueText = null;
            if (d == null || d.Component == null || d.Member == null)
                return false;
            object component = null;
            try { component = d.Component.Target; } catch { component = null; }
            if (component == null)
                return false;

            object raw = null;
            try
            {
                if (d.Member is FieldInfo f)
                {
                    raw = f.GetValue(f.IsStatic ? null : component);
                }
                else if (d.Member is PropertyInfo p)
                {
                    var getter = p.GetGetMethod(true);
                    if (getter == null)
                        return false;
                    raw = p.GetValueCompat(getter.IsStatic ? null : component);
                }
                else
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            if (d.Kind == DiscoveryCandidateKind.Boolean)
            {
                if (raw is bool b)
                {
                    valueText = b ? "1" : "0";
                    return true;
                }
                try
                {
                    valueText = Convert.ToBoolean(raw) ? "1" : "0";
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            if (d.Kind == DiscoveryCandidateKind.Enum)
            {
                try
                {
                    if (raw == null)
                        return false;
                    valueText = Convert.ToInt64(raw).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            if (d.Kind == DiscoveryCandidateKind.CollectionCount)
            {
                if (raw == null)
                    return false;
                try
                {
                    if (raw is Array arr)
                    {
                        valueText = arr.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        return true;
                    }
                }
                catch
                {
                }

                try
                {
                    var rt = raw.GetType();
                    var countProp = rt.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                    if (countProp != null && countProp.CanRead && countProp.PropertyType == typeof(int) && countProp.GetIndexParameters().Length == 0)
                    {
                        var cv = countProp.GetValueCompat(raw);
                        valueText = Convert.ToInt32(cv).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        return true;
                    }
                }
                catch
                {
                }

                try
                {
                    var rt = raw.GetType();
                    var lengthProp = rt.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
                    if (lengthProp != null && lengthProp.CanRead && lengthProp.PropertyType == typeof(int) && lengthProp.GetIndexParameters().Length == 0)
                    {
                        var lv = lengthProp.GetValueCompat(raw);
                        valueText = Convert.ToInt32(lv).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        return true;
                    }
                }
                catch
                {
                }

                return false;
            }

            if (TryConvertNumericToDouble(raw, out var v))
            {
                valueText = v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            return false;
        }

        private static string GetPipeArg(string[] parts, string name)
        {
            if (parts == null || parts.Length <= 1 || IsNullOrWhiteSpace(name))
                return null;
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                if (IsNullOrWhiteSpace(p))
                    continue;
                var eq = p.IndexOf('=');
                if (eq <= 0)
                    continue;
                var k = p.Substring(0, eq);
                if (!k.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                return p.Substring(eq + 1);
            }
            return null;
        }

        private static void CollectValueScanHits(object target, int depth, double targetValue, double tol, List<ValueScanHit> hits, int maxHits, HashSet<int> seen, ref int budget)
        {
            if (target == null || hits == null || hits.Count >= maxHits || budget <= 0)
                return;

            budget--;

            var t = target.GetType();
            if (t == typeof(string))
                return;

            if (!t.IsValueType)
            {
                var key = RuntimeHelpers.GetHashCode(target);
                if (seen != null)
                {
                    if (seen.Contains(key))
                        return;
                    seen.Add(key);
                }
            }

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            try
            {
                var fields = t.GetFields(flags);
                for (var i = 0; i < fields.Length && hits.Count < maxHits && budget > 0; i++)
                {
                    var f = fields[i];
                    if (f == null || f.IsLiteral)
                        continue;
                    if (!TryGetNumericType(f.FieldType))
                        continue;
                    object raw = null;
                    try { raw = f.GetValue(target); } catch { raw = null; }
                    if (!TryConvertNumericToDouble(raw, out var v))
                        continue;
                    if (Math.Abs(v - targetValue) <= tol)
                    {
                        var bound = new BoundNumericMember { Target = target, Member = f, ValueType = f.FieldType, LastKnownValue = v };
                        hits.Add(new ValueScanHit { Context = target, Bound = bound });
                    }
                }
            }
            catch
            {
            }

            try
            {
                var props = t.GetProperties(flags);
                for (var i = 0; i < props.Length && hits.Count < maxHits && budget > 0; i++)
                {
                    var p = props[i];
                    if (p == null || !p.CanRead || !p.CanWrite)
                        continue;
                    var idx = p.GetIndexParameters();
                    if (idx != null && idx.Length != 0)
                        continue;
                    if (!TryGetNumericType(p.PropertyType))
                        continue;
                    object raw = null;
                    try { raw = p.GetValueCompat(target); } catch { raw = null; }
                    if (!TryConvertNumericToDouble(raw, out var v))
                        continue;
                    if (Math.Abs(v - targetValue) <= tol)
                    {
                        var bound = new BoundNumericMember { Target = target, Member = p, ValueType = p.PropertyType, LastKnownValue = v };
                        hits.Add(new ValueScanHit { Context = target, Bound = bound });
                    }
                }
            }
            catch
            {
            }

            if (depth <= 0 || hits.Count >= maxHits || budget <= 0)
                return;

            try
            {
                var fields = t.GetFields(flags);
                for (var i = 0; i < fields.Length && hits.Count < maxHits && budget > 0; i++)
                {
                    var f = fields[i];
                    if (f == null || f.IsLiteral)
                        continue;
                    var ft = f.FieldType;
                    if (!ShouldFollowReferenceType(ft))
                        continue;
                    object child = null;
                    try { child = f.GetValue(target); } catch { child = null; }
                    if (child == null)
                        continue;
                    CollectValueScanHits(child, depth - 1, targetValue, tol, hits, maxHits, seen, ref budget);
                }
            }
            catch
            {
            }

            try
            {
                var props = t.GetProperties(flags);
                for (var i = 0; i < props.Length && hits.Count < maxHits && budget > 0; i++)
                {
                    var p = props[i];
                    if (p == null || !p.CanRead)
                        continue;
                    var idx = p.GetIndexParameters();
                    if (idx != null && idx.Length != 0)
                        continue;
                    var pt = p.PropertyType;
                    if (!ShouldFollowReferenceType(pt))
                        continue;
                    object child = null;
                    try { child = p.GetValueCompat(target); } catch { child = null; }
                    if (child == null)
                        continue;
                    CollectValueScanHits(child, depth - 1, targetValue, tol, hits, maxHits, seen, ref budget);
                }
            }
            catch
            {
            }
        }

        private static BoundNumericMember DiscoverNumericMemberOnObject(object target, string[] memberNames)
        {
            try
            {
                var t = target.GetType();
                foreach (var name in memberNames)
                {
                    var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (prop != null && prop.CanRead && prop.CanWrite)
                    {
                        if (TryGetNumericType(prop.PropertyType))
                        {
                            var m = new BoundNumericMember { Target = target, Member = prop, ValueType = prop.PropertyType };
                            TryGetNumeric(m, out var value);
                            m.LastKnownValue = value;
                            return m;
                        }
                    }

                    var field = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null)
                    {
                        if (TryGetNumericType(field.FieldType))
                        {
                            var m = new BoundNumericMember { Target = target, Member = field, ValueType = field.FieldType };
                            TryGetNumeric(m, out var value);
                            m.LastKnownValue = value;
                            return m;
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool TryGetIl2CppNumericSystemType(Type t, out Type systemNumericType)
        {
            systemNumericType = null;
            if (t == null)
                return false;

            if (t == typeof(int) || t == typeof(float) || t == typeof(double) || t == typeof(long) || t == typeof(short) || t == typeof(uint) || t == typeof(ulong) || t == typeof(byte) || t == typeof(decimal))
            {
                systemNumericType = t;
                return true;
            }

            var n = t.FullName ?? "";
            if (n.Length == 0)
                return false;

            if (n == "Il2CppSystem.Int32") { systemNumericType = typeof(int); return true; }
            if (n == "Il2CppSystem.Int64") { systemNumericType = typeof(long); return true; }
            if (n == "Il2CppSystem.Single") { systemNumericType = typeof(float); return true; }
            if (n == "Il2CppSystem.Double") { systemNumericType = typeof(double); return true; }
            if (n == "Il2CppSystem.Int16") { systemNumericType = typeof(short); return true; }
            if (n == "Il2CppSystem.UInt32") { systemNumericType = typeof(uint); return true; }
            if (n == "Il2CppSystem.UInt64") { systemNumericType = typeof(ulong); return true; }
            if (n == "Il2CppSystem.Byte") { systemNumericType = typeof(byte); return true; }
            if (n == "Il2CppSystem.Decimal") { systemNumericType = typeof(decimal); return true; }

            return false;
        }

        private static bool TryConvertSystemNumericToIl2Cpp(object value, Type il2cppNumericType, out object il2cppValue)
        {
            il2cppValue = null;
            if (value == null || il2cppNumericType == null)
                return false;

            var valueType = value.GetType();

            try
            {
                var implicitOp = il2cppNumericType.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, null, new[] { valueType }, null);
                if (implicitOp != null && il2cppNumericType.IsAssignableFrom(implicitOp.ReturnType))
                {
                    il2cppValue = implicitOp.Invoke(null, new[] { value });
                    return il2cppValue != null;
                }
            }
            catch
            {
            }

            try
            {
                var ctor = il2cppNumericType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { valueType }, null);
                if (ctor != null)
                {
                    il2cppValue = ctor.Invoke(new[] { value });
                    return il2cppValue != null;
                }
            }
            catch
            {
            }

            try
            {
                var parse = il2cppNumericType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (parse != null && il2cppNumericType.IsAssignableFrom(parse.ReturnType))
                {
                    il2cppValue = parse.Invoke(null, new object[] { Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) });
                    return il2cppValue != null;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryConvertNumericToDouble(object raw, out double value)
        {
            value = 0;
            if (raw == null)
                return false;

            try
            {
                if (raw is IConvertible)
                {
                    value = Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }
            }
            catch
            {
            }

            var rawType = raw.GetType();
            if (TryGetIl2CppNumericSystemType(rawType, out var systemNumericType) && (rawType.FullName ?? "").StartsWith("Il2CppSystem.", StringComparison.Ordinal))
            {
                try
                {
                    var op = rawType.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, null, new[] { rawType }, null);
                    if (op != null && op.ReturnType == systemNumericType)
                    {
                        var sys = op.Invoke(null, new[] { raw });
                        if (sys != null)
                        {
                            value = Convert.ToDouble(sys, System.Globalization.CultureInfo.InvariantCulture);
                            return true;
                        }
                    }
                }
                catch
                {
                }

                try
                {
                    var s = raw.ToString();
                    if (!IsNullOrWhiteSpace(s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    {
                        value = parsed;
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private static object ConvertDoubleToNumeric(double value, Type targetType)
        {
            if (targetType == null)
                return null;

            if ((targetType.FullName ?? "").StartsWith("Il2CppSystem.", StringComparison.Ordinal) && TryGetIl2CppNumericSystemType(targetType, out var systemNumericType))
            {
                object sysValue = null;
                try
                {
                    sysValue = Convert.ChangeType(value, systemNumericType, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch
                {
                    return null;
                }

                if (TryConvertSystemNumericToIl2Cpp(sysValue, targetType, out var il2cppValue))
                    return il2cppValue;
                return null;
            }

            if (TryGetIl2CppNumericSystemType(targetType, out _))
            {
                try
                {
                    return Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static bool TryGetNumericType(Type t)
        {
            return TryGetIl2CppNumericSystemType(t, out _);
        }

        private static bool TryGetNumeric(BoundNumericMember bound, out double value)
        {
            value = 0;
            if (bound == null || !bound.IsValid)
                return false;
            try
            {
                object raw = null;
                if (bound.Member is PropertyInfo pi)
                    raw = pi.GetValueCompat(bound.Target);
                else if (bound.Member is FieldInfo fi)
                    raw = fi.GetValue(bound.Target);

                if (raw == null)
                    return false;

                if (!TryConvertNumericToDouble(raw, out value))
                    return false;
                bound.LastKnownValue = value;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetNumeric(BoundNumericMember bound, double value)
        {
            if (bound == null || !bound.IsValid)
                return false;
            try
            {
                object converted = ConvertDoubleToNumeric(value, bound.ValueType);
                if (converted == null)
                    return false;
                if (bound.Member is PropertyInfo pi)
                    pi.SetValueCompat(bound.Target, converted);
                else if (bound.Member is FieldInfo fi)
                    fi.SetValue(bound.Target, converted);
                bound.LastKnownValue = value;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadNumericMemberValue(object instance, MemberInfo member, bool isStatic, out double value)
        {
            value = 0;
            if (member == null)
                return false;
            try
            {
                object raw = null;
                if (member is FieldInfo f)
                {
                    raw = f.GetValue(isStatic || f.IsStatic ? null : instance);
                }
                else if (member is PropertyInfo p)
                {
                    var getter = p.GetGetMethod(true);
                    if (getter == null)
                        return false;
                    raw = p.GetValueCompat(getter.IsStatic ? null : instance);
                }
                else
                {
                    return false;
                }

                if (raw == null)
                    return false;
                return TryConvertNumericToDouble(raw, out value);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryWriteNumericMemberValue(object instance, MemberInfo member, bool isStatic, double value)
        {
            if (member == null)
                return false;
            try
            {
                Type t = null;
                if (member is FieldInfo f)
                    t = f.FieldType;
                else if (member is PropertyInfo p)
                    t = p.PropertyType;
                if (t == null)
                    return false;

                var converted = ConvertDoubleToNumeric(value, t);
                if (converted == null)
                    return false;

                if (member is FieldInfo ff)
                {
                    ff.SetValue(isStatic || ff.IsStatic ? null : instance, converted);
                    return true;
                }
                if (member is PropertyInfo pp)
                {
                    var setter = pp.GetSetMethod(true);
                    if (setter == null)
                        return false;
                    pp.SetValueCompat(setter.IsStatic ? null : instance, converted);
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryWriteBoolMemberValue(object instance, MemberInfo member, bool isStatic, bool value)
        {
            if (member == null)
                return false;
            try
            {
                if (member is FieldInfo f)
                {
                    f.SetValue(isStatic || f.IsStatic ? null : instance, value);
                    return true;
                }
                if (member is PropertyInfo p)
                {
                    var setter = p.GetSetMethod(true);
                    if (setter == null)
                        return false;
                    p.SetValueCompat(setter.IsStatic ? null : instance, value);
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool TryWriteEnumMemberValue(object instance, MemberInfo member, bool isStatic, long value)
        {
            if (member == null)
                return false;
            try
            {
                Type t = null;
                if (member is FieldInfo f)
                    t = f.FieldType;
                else if (member is PropertyInfo p)
                    t = p.PropertyType;
                if (t == null)
                    return false;

                object boxed = null;
                try
                {
                    if (t.IsEnum)
                        boxed = Enum.ToObject(t, value);
                    else if (TryGetNumericType(t))
                        boxed = ConvertDoubleToNumeric(value, t);
                    else
                        boxed = Convert.ChangeType(value, t, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch
                {
                    boxed = null;
                }

                if (boxed == null)
                    return false;

                if (member is FieldInfo ff)
                {
                    ff.SetValue(isStatic || ff.IsStatic ? null : instance, boxed);
                    return true;
                }
                if (member is PropertyInfo pp)
                {
                    var setter = pp.GetSetMethod(true);
                    if (setter == null)
                        return false;
                    pp.SetValueCompat(setter.IsStatic ? null : instance, boxed);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryParseDouble(string text, out double value)
        {
            if (IsNullOrWhiteSpace(text))
            {
                value = 0;
                return false;
            }

            return double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }
        
        private static string ApplyHarmonyPatch(string targetType, string targetMethod, string patchType, string patchMethod)
        {
            try
            {
                if (IsNullOrWhiteSpace(targetType) || IsNullOrWhiteSpace(targetMethod))
                    return "ERROR|Missing target";
                if (IsNullOrWhiteSpace(patchType))
                    return "ERROR|Missing patch type";
                if (IsNullOrWhiteSpace(patchMethod))
                    return "ERROR|Missing patch method";

                var type = ResolveTypeByName(targetType);
                if (type == null)
                    return $"ERROR|Type not found: {targetType}";

                var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .Where(m => string.Equals(m.Name, targetMethod, StringComparison.Ordinal))
                    .Cast<MethodBase>()
                    .ToList();

                if (candidates.Count == 0)
                    return $"ERROR|Method not found: {targetType}.{targetMethod}";

                if (candidates.Count > 1)
                    return $"ERROR|Ambiguous method: {targetType}.{targetMethod} ({candidates.Count} overloads)";

                var target = candidates[0];
                var patchId = $"{targetType}::{targetMethod}::{patchType}::{patchMethod}";

                lock (_appliedHarmonyPatches)
                {
                    if (_appliedHarmonyPatches.ContainsKey(patchId))
                        return $"SUCCESS|ALREADY_APPLIED|{patchId}";
                }

                var harmony = new HarmonyLib.Harmony(HarmonyId);

                var typeNorm = patchType.Trim();
                var methodNorm = patchMethod.Trim();

                if (typeNorm.Equals("Prefix", StringComparison.OrdinalIgnoreCase))
                {
                    var dm = BuildPrefixPatch(methodNorm);
                    if (dm == null)
                        return $"ERROR|Unsupported patch method: {patchMethod}";

                    harmony.Patch(target, prefix: new HarmonyMethod(dm));
                }
                else if (typeNorm.Equals("Postfix", StringComparison.OrdinalIgnoreCase))
                {
                    var dm = BuildPostfixPatch(methodNorm);
                    if (dm == null)
                        return $"ERROR|Unsupported patch method: {patchMethod}";

                    harmony.Patch(target, postfix: new HarmonyMethod(dm));
                }
                else
                {
                    return $"ERROR|Unsupported patch type: {patchType}";
                }

                lock (_appliedHarmonyPatches)
                {
                    _appliedHarmonyPatches[patchId] = target;
                }

                return $"SUCCESS|APPLIED|{patchId}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
        
        private static string RemoveHarmonyPatch(string patchId)
        {
            try
            {
                if (IsNullOrWhiteSpace(patchId))
                    return "ERROR|Missing patch id";

                MethodBase target;
                lock (_appliedHarmonyPatches)
                {
                    if (!_appliedHarmonyPatches.TryGetValue(patchId, out target))
                        return $"ERROR|Unknown patch id: {patchId}";
                }

                var harmony = new HarmonyLib.Harmony(HarmonyId);
                harmony.Unpatch(target, HarmonyPatchType.All, HarmonyId);

                lock (_appliedHarmonyPatches)
                {
                    _appliedHarmonyPatches.Remove(patchId);
                }

                return $"SUCCESS|REMOVED|{patchId}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
    }
}
