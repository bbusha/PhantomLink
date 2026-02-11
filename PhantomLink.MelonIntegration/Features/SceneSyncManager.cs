using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Linq;
using MelonLoader;
using PhantomLink.MelonIntegration.Core;

namespace PhantomLink.MelonIntegration.Features
{
    public class SceneSyncEntry
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

    public static class SceneSyncManager
    {
        private static int _sceneSyncSessionId;
        private static bool _sceneSyncActive;
        private static bool _sceneSyncCleanupRequested;
        private static long _sceneSyncLastScanTicks;
        private static long _sceneSyncLastSceneSignatureCheckTicks;
        private static long _sceneSyncNextScanStartTicks;
        private static bool _sceneSyncScanInProgress;
        private static bool _sceneSyncScanForceCreate;
        private static string _sceneSyncLastSceneSignature = "";
        private static bool _sceneSyncAwaitingDestroyDrain;
        
        private static int _sceneSyncScanProcessed;
        private static int _sceneSyncScanMaxObjects = 6000; // Limit per frame/scan

        // State for the current scan
        private static readonly Stack<object> _sceneSyncScanStack = new Stack<object>();
        private static readonly HashSet<int> _sceneSyncScanVisited = new HashSet<int>();
        private static readonly HashSet<int> _sceneSyncScanCurrent = new HashSet<int>();
        private static readonly Dictionary<int, SceneSyncEntry> _sceneSyncObjects = new Dictionary<int, SceneSyncEntry>();
        private static readonly Queue<int> _sceneSyncPendingDestroy = new Queue<int>();
        
        // Event queue for IPC
        private static readonly object _sceneSyncEventsLock = new object();
        private static readonly Queue<string> _sceneSyncEvents = new Queue<string>();

        // FPS tracking (placeholder for now, can be hooked later)
        private static float _fps = 60.0f;

        public static void Initialize()
        {
            // Reset state
            _sceneSyncSessionId = 0;
            _sceneSyncActive = false;
        }

        public static void OnUpdate()
        {
            UpdateSceneSync();
        }

        public static string StartSync(string args, string[] parts)
        {
            try
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

                var activeScene = GetActiveSceneName();
                return $"SCENE_SYNC_STARTED|sid={_sceneSyncSessionId}|active={SanitizePipeValue(activeScene)}";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"StartSync error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        public static string StopSync(string args, string[] parts)
        {
            try
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
            catch (Exception ex)
            {
                MelonLogger.Error($"StopSync error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        public static string PollSync(string args, string[] parts)
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
                    if (string.IsNullOrEmpty(next))
                        continue;
                    batch.Add(next);
                    take++;
                }
            }

            var emitted = 0;
            while (emitted < batch.Count)
            {
                var next = batch[emitted];
                if (string.IsNullOrEmpty(next))
                    continue;

                // Prevent pipe buffer overflow (approx 60KB limit safety)
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
                // Throttle updates based on FPS
                var minTicks = Stopwatch.Frequency / 6; // ~10 FPS limit for scan
                if (_fps > 0)
                {
                    if (_fps < 10) minTicks = Stopwatch.Frequency;
                    else if (_fps < 20) minTicks = Stopwatch.Frequency / 2;
                    else if (_fps < 35) minTicks = Stopwatch.Frequency / 3;
                }
                
                if (now - _sceneSyncLastScanTicks < minTicks)
                    return;
            }
            _sceneSyncLastScanTicks = now;

            try
            {
                var goType = ReflectionHelper.FindType("UnityEngine.GameObject");
                if (goType == null) return;

                // Check if scene changed
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
                        
                        // Queue all existing for destroy
                        foreach (var id in _sceneSyncObjects.Keys)
                            _sceneSyncPendingDestroy.Enqueue(id);
                        
                        _sceneSyncObjects.Clear();
                        _sceneSyncAwaitingDestroyDrain = _sceneSyncPendingDestroy.Count > 0;
                        _sceneSyncScanInProgress = false;
                        _sceneSyncScanStack.Clear();
                        _sceneSyncScanVisited.Clear();
                        _sceneSyncScanCurrent.Clear();
                        _sceneSyncNextScanStartTicks = 0;
                    }
                }

                // Process pending destroys (limited burst)
                var destroyBurst = 0;
                while (destroyBurst < 64 && _sceneSyncPendingDestroy.Count > 0)
                {
                    if (!_sceneSyncActive) return;
                    var id = _sceneSyncPendingDestroy.Dequeue();
                    EnqueueSceneSyncEvent("e=DESTROY;id=" + id);
                    destroyBurst++;
                }

                if (_sceneSyncAwaitingDestroyDrain)
                {
                    if (_sceneSyncPendingDestroy.Count > 0) return;
                    _sceneSyncAwaitingDestroyDrain = false;
                    BeginSceneSyncScan(forceCreate: true);
                    return;
                }

                // Start or continue scan
                if (!_sceneSyncScanInProgress)
                {
                    if (force || _sceneSyncNextScanStartTicks == 0 || now >= _sceneSyncNextScanStartTicks)
                        BeginSceneSyncScan(forceCreate: false);
                    return;
                }

                // Process scan stack
                var maxPerTick = 260;
                if (_fps > 0)
                {
                    if (_fps < 8) maxPerTick = 20;
                    else if (_fps < 15) maxPerTick = 60;
                    else if (_fps < 25) maxPerTick = 120;
                    else if (_fps < 40) maxPerTick = 220;
                    else maxPerTick = 360;
                }

                var startWork = Stopwatch.GetTimestamp();
                var processed = 0;
                while (processed < maxPerTick && _sceneSyncScanStack.Count > 0)
                {
                    if (!_sceneSyncActive) return;
                    if ((Stopwatch.GetTimestamp() - startWork) > (Stopwatch.Frequency / 220)) // Time budget
                        break;

                    var go = _sceneSyncScanStack.Pop();
                    if (go == null) continue;

                    // Skip if we've already visited this Unity Instance ID in this scan
                    if (ReflectionHelper.TryGetUnityInstanceId(go, out var unityId) && unityId != 0)
                    {
                        if (!_sceneSyncScanVisited.Add(unityId))
                            continue;
                    }

                    // Track with ObjectManager
                    var id = ObjectManager.TrackObject(go);
                    if (id == 0) continue;

                    _sceneSyncScanCurrent.Add(id);

                    // Gather data
                    var name = ReflectionHelper.GetUnityObjectName(go) ?? "";
                    var active = ReflectionHelper.GetActiveInHierarchy(go);

                    var parentId = 0;
                    string localPos = "", localRot = "", localScale = "";

                    if (ReflectionHelper.TryGetTransform(go, out var tr) && tr != null)
                    {
                        parentId = GetTransformParentId(tr);
                        var t = tr.GetType();
                        
                        // Use helper to get vector strings
                        localPos = ReflectionHelper.FormatVector3(ReflectionHelper.GetProperty(t, "localPosition")?.GetValue(tr, null));
                        localRot = ReflectionHelper.FormatVector3(ReflectionHelper.GetProperty(t, "localEulerAngles")?.GetValue(tr, null));
                        localScale = ReflectionHelper.FormatVector3(ReflectionHelper.GetProperty(t, "localScale")?.GetValue(tr, null));
                    }

                    var isNew = !_sceneSyncObjects.TryGetValue(id, out var prev);
                    var wantFull = isNew || (prev.LastFullTicks == 0 || (now - prev.LastFullTicks) > (Stopwatch.Frequency * 2));
                    
                    var boundsCenter = isNew ? "" : prev.BoundsCenter;
                    var boundsSize = isNew ? "" : prev.BoundsSize;
                    var colliderCenter = isNew ? "" : prev.ColliderCenter;
                    var colliderSize = isNew ? "" : prev.ColliderSize;
                    var comps = isNew ? "" : prev.Components;

                    if (wantFull)
                    {
                        ReflectionHelper.TryGetRenderableBounds(go, out boundsCenter, out boundsSize);
                        ReflectionHelper.TryGetColliderBounds(go, out colliderCenter, out colliderSize);
                        var compNames = ReflectionHelper.GetComponentTypeNames(go);
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
                        EnqueueSceneSyncEvent(BuildUpsertEvent("CREATE", id, entry));
                    }
                    else
                    {
                        // Diff check
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
                            EnqueueSceneSyncEvent(BuildUpsertEvent("UPDATE", id, entry));
                        }
                        else
                        {
                            _sceneSyncObjects[id] = entry; // Update timestamps
                        }
                    }

                    // Push children
                    ReflectionHelper.PushGameObjectChildren(go, _sceneSyncScanStack);
                    
                    processed++;
                    _sceneSyncScanProcessed++;
                    if (_sceneSyncScanProcessed >= _sceneSyncScanMaxObjects)
                        _sceneSyncScanStack.Clear(); // Force end of scan
                }

                // End of scan cycle?
                if (_sceneSyncScanStack.Count == 0)
                {
                    // Cleanup removed objects
                    if (_sceneSyncObjects.Count > 0)
                    {
                        var removed = new List<int>();
                        foreach (var kv in _sceneSyncObjects)
                        {
                            if (!_sceneSyncScanCurrent.Contains(kv.Key))
                                removed.Add(kv.Key);
                        }
                        foreach (var id in removed)
                        {
                            _sceneSyncObjects.Remove(id);
                            _sceneSyncPendingDestroy.Enqueue(id);
                        }
                    }

                    _sceneSyncScanInProgress = false;
                    _sceneSyncScanForceCreate = false;
                    _sceneSyncScanVisited.Clear();
                    _sceneSyncScanCurrent.Clear();

                    var next = Stopwatch.Frequency / 2; // Default 0.5s delay
                    _sceneSyncNextScanStartTicks = Stopwatch.GetTimestamp() + next;
                }
            }
            catch (Exception)
            {
                // Silently fail to keep loop running
            }
        }

        private static void BeginSceneSyncScan(bool forceCreate)
        {
            if (!_sceneSyncActive) return;

            try
            {
                var goType = ReflectionHelper.FindType("UnityEngine.GameObject");
                if (goType == null) return;

                var roots = ReflectionHelper.GetLoadedSceneRootGameObjects(goType, _sceneSyncScanMaxObjects);
                if (roots == null || roots.Count == 0)
                    roots = ReflectionHelper.FindSceneGameObjects(goType, _sceneSyncScanMaxObjects);
                
                if (roots == null || roots.Count == 0) return;

                _sceneSyncScanStack.Clear();
                _sceneSyncScanVisited.Clear();
                _sceneSyncScanCurrent.Clear();
                _sceneSyncScanProcessed = 0;
                _sceneSyncScanForceCreate = forceCreate;
                _sceneSyncScanInProgress = true;

                foreach (var r in roots)
                {
                    if (r != null) _sceneSyncScanStack.Push(r);
                }
            }
            catch { }
        }

        private static void EnqueueSceneSyncEvent(string evt)
        {
            if (string.IsNullOrEmpty(evt)) return;
            lock (_sceneSyncEventsLock)
            {
                if (_sceneSyncEvents.Count > 12000) _sceneSyncEvents.Clear();
                _sceneSyncEvents.Enqueue(evt);
            }
        }

        private static string BuildUpsertEvent(string kind, int id, SceneSyncEntry entry)
        {
            var evt = new StringBuilder(256);
            evt.Append("e="); evt.Append(kind);
            evt.Append(";id="); evt.Append(id);
            evt.Append(";parent="); evt.Append(entry.ParentId);
            evt.Append(";name="); evt.Append(SanitizePipeValue(entry.Name));
            evt.Append(";active="); evt.Append(entry.Active ? "1" : "0");
            
            if (!string.IsNullOrEmpty(entry.LocalPos)) { evt.Append(";lp="); evt.Append(entry.LocalPos); }
            if (!string.IsNullOrEmpty(entry.LocalRot)) { evt.Append(";lr="); evt.Append(entry.LocalRot); }
            if (!string.IsNullOrEmpty(entry.LocalScale)) { evt.Append(";ls="); evt.Append(entry.LocalScale); }
            if (!string.IsNullOrEmpty(entry.BoundsCenter)) { evt.Append(";bc="); evt.Append(entry.BoundsCenter); }
            if (!string.IsNullOrEmpty(entry.BoundsSize)) { evt.Append(";bs="); evt.Append(entry.BoundsSize); }
            if (!string.IsNullOrEmpty(entry.ColliderCenter)) { evt.Append(";cc="); evt.Append(entry.ColliderCenter); }
            if (!string.IsNullOrEmpty(entry.ColliderSize)) { evt.Append(";cs="); evt.Append(entry.ColliderSize); }
            if (!string.IsNullOrEmpty(entry.Components)) { evt.Append(";comps="); evt.Append(entry.Components); }
            
            return evt.ToString();
        }

        private static int GetTransformParentId(object transform)
        {
            if (transform == null) return 0;
            try
            {
                var parentProp = ReflectionHelper.GetProperty(transform.GetType(), "parent");
                var parent = parentProp?.GetValue(transform, null);
                if (parent == null) return 0;
                
                var parentGo = ReflectionHelper.TryGetTransformGameObject(parent);
                if (parentGo == null) return 0;
                
                return ObjectManager.TrackObject(parentGo);
            }
            catch { return 0; }
        }

        private static string GetSceneSignatureFast()
        {
            try
            {
                var sceneManager = ReflectionHelper.FindType("UnityEngine.SceneManagement.SceneManager");
                if (sceneManager == null) return "";

                var countProp = ReflectionHelper.GetProperty(sceneManager, "sceneCount");
                var sceneCount = 0;
                try { sceneCount = countProp != null ? Convert.ToInt32(countProp.GetValue(null, null)) : 0; } catch { }

                var getActive = ReflectionHelper.GetMethod(sceneManager, "GetActiveScene");
                if (getActive == null) return sceneCount.ToString();

                var scene = getActive.Invoke(null, null);
                if (scene == null) return sceneCount.ToString();

                var st = scene.GetType();
                var nameProp = ReflectionHelper.GetProperty(st, "name");
                var buildIndexProp = ReflectionHelper.GetProperty(st, "buildIndex");

                var name = "";
                try { name = nameProp?.GetValue(scene, null)?.ToString() ?? ""; } catch { }
                var build = -1;
                try { build = buildIndexProp != null ? Convert.ToInt32(buildIndexProp.GetValue(scene, null)) : -1; } catch { }

                return name + "|" + build + "|" + sceneCount;
            }
            catch { return ""; }
        }

        private static string GetActiveSceneName()
        {
            try
            {
                var sceneManager = ReflectionHelper.FindType("UnityEngine.SceneManagement.SceneManager");
                if (sceneManager != null)
                {
                    var getActive = ReflectionHelper.GetMethod(sceneManager, "GetActiveScene");
                    var scene = getActive?.Invoke(null, null);
                    if (scene != null)
                    {
                        var nameProp = ReflectionHelper.GetProperty(scene.GetType(), "name");
                        return nameProp?.GetValue(scene, null)?.ToString() ?? "";
                    }
                }
            }
            catch { }
            return "";
        }

        private static string SanitizePipeValue(string val)
        {
            if (string.IsNullOrEmpty(val)) return "";
            return val.Replace("|", "&#124;").Replace(";", "&#59;").Replace("=", "&#61;").Replace("\n", "\\n").Replace("\r", "");
        }

        public static string GetSceneInfo(string args, string[] parts)
        {
            try
            {
                var sceneManager = ReflectionHelper.FindType("UnityEngine.SceneManagement.SceneManager");
                if (sceneManager == null) return "ERROR|SceneManager not found";

                var getActive = ReflectionHelper.GetMethod(sceneManager, "GetActiveScene");
                if (getActive == null) return "ERROR|GetActiveScene not found";

                var scene = getActive.Invoke(null, null);
                if (scene == null) return "ERROR|Active scene unavailable";

                var sceneType = scene.GetType();
                var nameProp = ReflectionHelper.GetProperty(sceneType, "name");
                var buildIndexProp = ReflectionHelper.GetProperty(sceneType, "buildIndex");
                var isLoadedProp = ReflectionHelper.GetProperty(sceneType, "isLoaded");

                var activeName = nameProp?.GetValue(scene, null)?.ToString() ?? "Unknown";
                var activeBuild = buildIndexProp != null ? (int)buildIndexProp.GetValue(scene, null) : -1;
                var activeLoaded = isLoadedProp != null ? (bool)isLoadedProp.GetValue(scene, null) : true;

                var countProp = ReflectionHelper.GetProperty(sceneManager, "sceneCount");
                var sceneCount = countProp != null ? (int)countProp.GetValue(null, null) : 1;

                var getSceneAt = ReflectionHelper.GetMethod(sceneManager, "GetSceneAt", new[] { typeof(int) });

                var sb = new StringBuilder();
                sb.Append("SCENE");
                sb.Append("|active="); sb.Append(activeName.Replace("|", " "));
                sb.Append("|activeBuildIndex="); sb.Append(activeBuild);
                sb.Append("|activeLoaded="); sb.Append(activeLoaded ? "1" : "0");
                sb.Append("|sceneCount="); sb.Append(sceneCount);

                if (getSceneAt != null)
                {
                    var names = new List<string>();
                    for (var i = 0; i < sceneCount; i++)
                    {
                        try
                        {
                            var s = getSceneAt.Invoke(null, new object[] { i });
                            if (s == null) continue;
                            var n = nameProp?.GetValue(s, null)?.ToString() ?? $"Scene{i}";
                            names.Add(n);
                        }
                        catch { }
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

        public static string GetObjectInfo(string instanceId)
        {
            try
            {
                var obj = ObjectManager.GetObject(instanceId);
                if (obj == null) return $"ERROR|Instance not found: {instanceId}";

                var sb = new StringBuilder();
                sb.Append("OBJECT_INFO");
                sb.Append("|id="); sb.Append(instanceId);
                sb.Append("|type="); sb.Append(obj.GetType().FullName ?? obj.GetType().Name);
                sb.Append("|name="); sb.Append((ReflectionHelper.GetUnityObjectName(obj) ?? "").Replace("|", " "));

                var go = ReflectionHelper.TryGetTransformGameObject(obj) ?? obj; // Try to treat as Component first, then GameObject
                // Actually if obj is Component, we want its GameObject. If obj is GameObject, we use it.
                // If obj is just a random object, we might fail.
                
                // Better check:
                object gameObject = null;
                if (obj.GetType().Name == "GameObject" || obj.GetType().FullName == "UnityEngine.GameObject")
                    gameObject = obj;
                else
                {
                    ReflectionHelper.TryGetTransform(obj, out var tr); // Check if it has transform (is Component)
                    if (tr != null)
                        gameObject = ReflectionHelper.TryGetTransformGameObject(tr);
                    else
                    {
                        // Maybe it is a component directly
                        var goProp = ReflectionHelper.GetProperty(obj.GetType(), "gameObject");
                        if (goProp != null) gameObject = goProp.GetValue(obj, null);
                    }
                }

                if (gameObject != null)
                {
                    var goType = gameObject.GetType();
                    var tagProp = ReflectionHelper.GetProperty(goType, "tag");
                    var layerProp = ReflectionHelper.GetProperty(goType, "layer");
                    
                    var tag = tagProp?.GetValue(gameObject, null)?.ToString();
                    if (!string.IsNullOrEmpty(tag))
                    {
                        sb.Append("|tag="); sb.Append(tag.Replace("|", " "));
                    }
                    
                    if (layerProp != null)
                    {
                        try
                        {
                            var layerVal = layerProp.GetValue(gameObject, null);
                            if (layerVal != null)
                            {
                                sb.Append("|layer="); sb.Append(Convert.ToInt32(layerVal));
                            }
                        }
                        catch { }
                    }

                    if (ReflectionHelper.GetActiveInHierarchy(gameObject))
                    {
                        sb.Append("|active=1");
                    }
                    else
                    {
                        sb.Append("|active=0");
                    }

                    if (ReflectionHelper.TryGetTransform(gameObject, out var tr))
                    {
                        var posProp = ReflectionHelper.GetProperty(tr.GetType(), "position");
                        if (posProp != null)
                        {
                            var pos = posProp.GetValue(tr, null);
                            sb.Append("|pos="); sb.Append(ReflectionHelper.FormatVector3(pos));
                        }
                    }

                    var componentNames = ReflectionHelper.GetComponentTypeNames(gameObject);
                    if (componentNames != null && componentNames.Count > 0)
                    {
                        sb.Append("|components=");
                        sb.Append(string.Join(",", componentNames.Take(50).ToArray()));
                    }
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        public static string GetMeshInfo(string args, string[] parts)
        {
            // MESH|id|maxVerts|maxIndices
            try
            {
                var idText = parts.Length > 1 ? parts[1] : "";
                var maxVerts = 2000;
                var maxIndices = 12000;
                if (parts.Length > 2 && int.TryParse(parts[2], out var mv) && mv > 0) maxVerts = Math.Min(mv, 20000);
                if (parts.Length > 3 && int.TryParse(parts[3], out var mi) && mi > 0) maxIndices = Math.Min(mi, 120000);

                var obj = ObjectManager.GetObject(idText);
                if (obj == null) return $"ERROR|Instance not found: {idText}";

                // Resolve GameObject
                object go = null;
                var goProp = ReflectionHelper.GetProperty(obj.GetType(), "gameObject");
                if (goProp != null) go = goProp.GetValue(obj, null);
                if (go == null) go = obj; // Assume it might be GameObject

                var meshFilterType = ReflectionHelper.FindType("UnityEngine.MeshFilter");
                var skinnedType = ReflectionHelper.FindType("UnityEngine.SkinnedMeshRenderer");
                
                if (meshFilterType == null && skinnedType == null)
                    return "ERROR|No mesh component types found";

                object meshOwner = null;
                var getComp = ReflectionHelper.GetMethod(go.GetType(), "GetComponent", new[] { typeof(Type) });
                if (getComp == null) return "ERROR|GetComponent not found";

                if (meshFilterType != null)
                    meshOwner = getComp.Invoke(go, new object[] { meshFilterType });
                
                if (meshOwner == null && skinnedType != null)
                    meshOwner = getComp.Invoke(go, new object[] { skinnedType });
                
                if (meshOwner == null)
                    return $"MESH|id={idText}|vcount=0|icount=0";

                var ownerType = meshOwner.GetType();
                var meshProp = ReflectionHelper.GetProperty(ownerType, "sharedMesh") ?? ReflectionHelper.GetProperty(ownerType, "mesh");
                var mesh = meshProp?.GetValue(meshOwner, null);
                
                if (mesh == null) return $"MESH|id={idText}|vcount=0|icount=0";

                object bakedMesh = null;
                bool isSkinned = skinnedType != null && (skinnedType.IsAssignableFrom(ownerType) || ownerType.FullName.Contains("SkinnedMeshRenderer"));
                
                if (isSkinned)
                {
                    var meshType = ReflectionHelper.FindType("UnityEngine.Mesh");
                    if (meshType != null)
                    {
                        try { bakedMesh = Activator.CreateInstance(meshType); } catch { bakedMesh = null; }
                        
                        if (bakedMesh != null)
                        {
                            var bakeMethod = ReflectionHelper.GetMethod(ownerType, "BakeMesh", new[] { meshType });
                            if (bakeMethod != null)
                            {
                                bakeMethod.Invoke(meshOwner, new object[] { bakedMesh });
                                mesh = bakedMesh;
                            }
                        }
                    }
                }

                try
                {
                    var mt = mesh.GetType();
                    var verticesProp = ReflectionHelper.GetProperty(mt, "vertices");
                    var trisProp = ReflectionHelper.GetProperty(mt, "triangles");
                    
                    if (verticesProp == null || trisProp == null) return "ERROR|Mesh data unavailable";

                    var verticesArr = verticesProp.GetValue(mesh, null) as Array;
                    var indicesArr = trisProp.GetValue(mesh, null) as Array;
                    
                    if (verticesArr == null || indicesArr == null) return "ERROR|Mesh arrays null";

                    var vcount = verticesArr.Length;
                    var icount = indicesArr.Length;

                    if (vcount > maxVerts || icount > maxIndices)
                        return $"MESH|id={idText}|tooLarge=1|vcount={vcount}|icount={icount}";

                    var verts = new float[vcount * 3];
                    var xField = ReflectionHelper.FindField(verticesArr.GetValue(0).GetType(), "x");
                    var yField = ReflectionHelper.FindField(verticesArr.GetValue(0).GetType(), "y");
                    var zField = ReflectionHelper.FindField(verticesArr.GetValue(0).GetType(), "z");

                    for (var i = 0; i < vcount; i++)
                    {
                        var v = verticesArr.GetValue(i);
                        verts[i * 3] = (float)xField.GetValue(v);
                        verts[i * 3 + 1] = (float)yField.GetValue(v);
                        verts[i * 3 + 2] = (float)zField.GetValue(v);
                    }

                    var indices = new int[icount];
                    for (var i = 0; i < icount; i++)
                    {
                        indices[i] = Convert.ToInt32(indicesArr.GetValue(i));
                    }

                    var vb = new byte[verts.Length * 4];
                    Buffer.BlockCopy(verts, 0, vb, 0, vb.Length);
                    
                    var ib = new byte[indices.Length * 4];
                    Buffer.BlockCopy(indices, 0, ib, 0, ib.Length);

                    return $"MESH|id={idText}|vcount={vcount}|icount={icount}|vb64={Convert.ToBase64String(vb)}|ib64={Convert.ToBase64String(ib)}";
                }
                finally
                {
                    if (bakedMesh != null)
                    {
                        try
                        {
                            var objType = ReflectionHelper.FindType("UnityEngine.Object");
                            var destroy = ReflectionHelper.GetMethod(objType, "Destroy", new[] { objType });
                            destroy?.Invoke(null, new object[] { bakedMesh });
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }
    }
}

