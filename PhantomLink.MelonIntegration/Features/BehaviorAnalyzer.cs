using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;
using PhantomLink.MelonIntegration.Core;

namespace PhantomLink.MelonIntegration.Features
{
    public static class BehaviorAnalyzer
    {
        // Configuration
        private const int MAX_TRACKED_OBJECTS = 50;
        private const int MAX_SAMPLES = 60; // 1 second at 60fps
        private const float CORRELATION_THRESHOLD = 0.7f;

        // State
        private static bool _isActive;
        private static int _scanFrameCounter;
        
        // Structs
        public struct Vector3Struct 
        { 
            public float x, y, z;
            public static Vector3Struct Zero => new Vector3Struct();
            public static float Distance(Vector3Struct a, Vector3Struct b) 
            {
                float dx = a.x - b.x;
                float dy = a.y - b.y;
                float dz = a.z - b.z;
                return (float)Math.Sqrt(dx*dx + dy*dy + dz*dz);
            }
        }

        // Tracking
        private class TrackedObject
        {
            public int InstanceId;
            public WeakReference Reference; // GameObject
            public Vector3Struct LastPosition;
            public Vector3Struct PreviousPosition; // For delta
            public float MovementScore; // Correlation with input
            public Dictionary<string, TrackedField> Fields = new Dictionary<string, TrackedField>();
            public Dictionary<string, float> RoleScores = new Dictionary<string, float>();
            
            public object GetTarget() => Reference?.Target;
        }

        private class TrackedField
        {
            public string Name;
            public WeakReference ComponentRef; // Component instance
            public MemberInfo Member; // FieldInfo or PropertyInfo
            public float LastValue;
            public List<float> History = new List<float>(); // For numeric correlation
            public float ChangeFrequency;
            public bool IsNumeric;
            
            public object GetComponent() => ComponentRef?.Target;
        }

        private static Dictionary<int, TrackedObject> _trackedObjects = new Dictionary<int, TrackedObject>();
        
        // Inferred Roles (The output)
        private static Dictionary<string, int> _roleAssignments = new Dictionary<string, int>(); // Role -> InstanceID
        private static Dictionary<string, TrackedField> _fieldAssignments = new Dictionary<string, TrackedField>(); // Role.Field -> TrackedField

        public static void Start()
        {
            _isActive = true;
            _trackedObjects.Clear();
            _roleAssignments.Clear();
            _fieldAssignments.Clear();
            MelonLogger.Msg("Behavior Analyzer Started");
        }

        public static void Stop()
        {
            _isActive = false;
            _trackedObjects.Clear();
            _roleAssignments.Clear();
            _fieldAssignments.Clear();
            MelonLogger.Msg("Behavior Analyzer Stopped");
        }

        public static void OnUpdate()
        {
            if (!_isActive) return;

            try
            {
                // A. ENTRY: Enumerate & Maintain Candidates
                if (_scanFrameCounter++ % 60 == 0) // Every 1 sec
                {
                    ScanCandidates();
                }

                // B. SAMPLER: Per-frame snapshot
                SampleState();

                // C. EVENT ENGINE: Input & Correlation
                ProcessEvents();

                // D. ROLE INFERENCE
                InferRoles();
                
                // E. FIELD INFERENCE
                InferFields();
            }
            catch (Exception ex)
            {
                // Throttle logging?
                if (_scanFrameCounter % 300 == 0)
                    MelonLogger.Error($"BehaviorAnalyzer Error: {ex.Message}");
            }
        }

        private static void ScanCandidates()
        {
            // Find likely candidates (Roots, or objects with specific components)
            // Limit to avoid lag
            var goType = ReflectionHelper.FindType("UnityEngine.GameObject");
            if (goType == null) return;

            // 1. Get Scene Roots
            var roots = ReflectionHelper.GetLoadedSceneRootGameObjects(goType, MAX_TRACKED_OBJECTS);
            
            foreach (var root in roots)
            {
                ProcessCandidate(root);
            }
        }

        private static void ProcessCandidate(object go)
        {
            if (go == null) return;
            if (!ReflectionHelper.TryGetUnityInstanceId(go, out int id)) return;

            if (!_trackedObjects.ContainsKey(id))
            {
                var tracked = new TrackedObject
                {
                    InstanceId = id,
                    Reference = new WeakReference(go),
                    LastPosition = Vector3Struct.Zero,
                    PreviousPosition = Vector3Struct.Zero
                };
                
                // Initialize Fields (only numeric for now)
                ScanFields(tracked, go);
                
                _trackedObjects[id] = tracked;
            }
        }

        private static void ScanFields(TrackedObject tracked, object go)
        {
            // Get all components
            var getComps = ReflectionHelper.GetMethod(go.GetType(), "GetComponents", new[] { typeof(Type) });
            var compType = ReflectionHelper.FindType("UnityEngine.Component");
            
            if (getComps != null && compType != null)
            {
                var components = getComps.Invoke(go, new object[] { compType }) as Array;
                if (components != null)
                {
                    foreach (var comp in components)
                    {
                        if (comp == null) continue;
                        var type = comp.GetType();
                        
                        // Fields
                        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (IsNumeric(f.FieldType))
                            {
                                tracked.Fields[$"{type.Name}.{f.Name}"] = new TrackedField 
                                { 
                                    Name = f.Name, 
                                    Member = f, 
                                    ComponentRef = new WeakReference(comp),
                                    IsNumeric = true 
                                };
                            }
                        }
                        
                        // Properties
                        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (p.CanRead && IsNumeric(p.PropertyType))
                            {
                                tracked.Fields[$"{type.Name}.{p.Name}"] = new TrackedField 
                                { 
                                    Name = p.Name, 
                                    Member = p, 
                                    ComponentRef = new WeakReference(comp),
                                    IsNumeric = true 
                                };
                            }
                        }
                    }
                }
            }
        }

        private static bool IsNumeric(Type t)
        {
            return t == typeof(int) || t == typeof(float) || t == typeof(double);
        }

        private static void SampleState()
        {
            foreach (var kvp in _trackedObjects)
            {
                var tracked = kvp.Value;
                var target = tracked.GetTarget();
                if (target == null) continue; // Object died

                // Update Positions
                tracked.PreviousPosition = tracked.LastPosition;

                if (ReflectionHelper.TryGetTransform(target, out var tr))
                {
                    var posProp = ReflectionHelper.GetProperty(tr.GetType(), "position");
                    if (posProp != null)
                    {
                        var posObj = posProp.GetValue(tr, null);
                        if (posObj != null) tracked.LastPosition = ParseVector3(posObj);
                    }
                }

                // Fields
                foreach (var field in tracked.Fields.Values)
                {
                    var comp = field.GetComponent();
                    if (comp == null) continue;

                    try
                    {
                        float val = 0;
                        if (field.Member is FieldInfo fi)
                            val = Convert.ToSingle(fi.GetValue(comp));
                        else if (field.Member is PropertyInfo pi)
                            val = Convert.ToSingle(pi.GetValue(comp, null));
                        
                        // Detect change
                        if (Math.Abs(val - field.LastValue) > 0.001f)
                        {
                            field.LastValue = val;
                            field.ChangeFrequency += 1.0f;
                            // Add to history
                            field.History.Add(val);
                            if (field.History.Count > MAX_SAMPLES) field.History.RemoveAt(0);
                        }
                    }
                    catch { }
                }
            }
        }
        
        // Helper to parse generic Vector3 from reflection
        private static Vector3Struct ParseVector3(object v)
        {
            // Assumes standard Unity fields
            var t = v.GetType();
            float x = 0, y = 0, z = 0;
            try { x = (float)ReflectionHelper.FindField(t, "x").GetValue(v); } catch {}
            try { y = (float)ReflectionHelper.FindField(t, "y").GetValue(v); } catch {}
            try { z = (float)ReflectionHelper.FindField(t, "z").GetValue(v); } catch {}
            return new Vector3Struct { x = x, y = y, z = z };
        }

        private static void ProcessEvents()
        {
            // Check Input
            bool inputActive = false;
            var inputType = ReflectionHelper.FindType("UnityEngine.Input");
            if (inputType != null)
            {
                var getAxis = ReflectionHelper.GetMethod(inputType, "GetAxis", new[] { typeof(string) });
                if (getAxis != null)
                {
                    try
                    {
                        float h = (float)getAxis.Invoke(null, new object[] { "Horizontal" });
                        float v = (float)getAxis.Invoke(null, new object[] { "Vertical" });
                        inputActive = (Math.Abs(h) > 0.1f || Math.Abs(v) > 0.1f);
                    }
                    catch { }
                }
            }

            // Correlate
            foreach (var kvp in _trackedObjects)
            {
                var tracked = kvp.Value;
                
                float delta = Vector3Struct.Distance(tracked.LastPosition, tracked.PreviousPosition);
                bool moved = delta > 0.01f;

                if (inputActive && moved)
                {
                    tracked.MovementScore += 1.0f;
                }
                else if (!inputActive && moved)
                {
                    tracked.MovementScore -= 0.5f; // Moving without input? Maybe NPC
                }
                else if (inputActive && !moved)
                {
                    tracked.MovementScore -= 0.1f; // Input but no move
                }
                
                // Cap score
                if (tracked.MovementScore < 0) tracked.MovementScore = 0;
                if (tracked.MovementScore > 100) tracked.MovementScore = 100;
            }
        }

        private static void InferRoles()
        {
            // Find max score
            TrackedObject bestPlayer = null;
            float maxScore = 0;

            foreach (var kvp in _trackedObjects)
            {
                if (kvp.Value.MovementScore > maxScore)
                {
                    maxScore = kvp.Value.MovementScore;
                    bestPlayer = kvp.Value;
                }
            }

            if (bestPlayer != null && maxScore > 5) // Threshold
            {
                _roleAssignments["Player"] = bestPlayer.InstanceId;
            }
        }
        
        private static void InferFields()
        {
            if (!_roleAssignments.TryGetValue("Player", out int playerId)) return;
            if (!_trackedObjects.TryGetValue(playerId, out var player)) return;

            // Input check for Ammo (Fire1)
            bool fireActive = false;
            var inputType = ReflectionHelper.FindType("UnityEngine.Input");
            if (inputType != null)
            {
                var getButton = ReflectionHelper.GetMethod(inputType, "GetButton", new[] { typeof(string) });
                if (getButton != null)
                {
                    try { fireActive = (bool)getButton.Invoke(null, new object[] { "Fire1" }); } catch { }
                }
            }

            foreach (var field in player.Fields.Values)
            {
                // Simple heuristic logic
                
                // 1. Ammo: Decreases when firing
                if (fireActive)
                {
                    // Check if value decreased in last frame
                    if (field.History.Count >= 2)
                    {
                        float current = field.History[field.History.Count - 1];
                        float prev = field.History[field.History.Count - 2];
                        if (current < prev)
                        {
                            // Boost score for "Ammo"
                            if (!player.RoleScores.ContainsKey(field.Name + "_Ammo")) player.RoleScores[field.Name + "_Ammo"] = 0;
                            player.RoleScores[field.Name + "_Ammo"] += 5.0f;
                        }
                    }
                }

                // 2. Health: High value, stable, drops occasionally (not implemented fully without damage event)
                // Fallback: Name heuristics if behavior fails?
                // The user asked to REPLACE search engines, so we should rely on behavior OR behavior+name.
                // Let's stick to name heuristics inside Analyzer as a baseline, then boost with behavior.
                
                float nameScoreHealth = 0;
                if (field.Name.ToLower().Contains("health") || field.Name.ToLower().Contains("hp")) nameScoreHealth = 10;
                
                float nameScoreAmmo = 0;
                if (field.Name.ToLower().Contains("ammo") || field.Name.ToLower().Contains("clip")) nameScoreAmmo = 10;
                
                // Final Assignments
                float currentAmmoScore = (player.RoleScores.ContainsKey(field.Name + "_Ammo") ? player.RoleScores[field.Name + "_Ammo"] : 0) + nameScoreAmmo;
                
                if (currentAmmoScore > 10 && currentAmmoScore > (player.RoleScores.ContainsKey("Best_Ammo") ? player.RoleScores["Best_Ammo"] : 0))
                {
                    _fieldAssignments["Player.Ammo"] = field;
                    player.RoleScores["Best_Ammo"] = currentAmmoScore;
                }
                
                if (nameScoreHealth > 0 && !_fieldAssignments.ContainsKey("Player.Health")) // Just take first good health candidate for now
                {
                    _fieldAssignments["Player.Health"] = field;
                }
            }
        }

        public class BoundField
        {
            public object Target;
            public MemberInfo Member;
            public bool IsValid => Target != null;
        }

        public static BoundField GetRoleField(string role)
        {
            if (_fieldAssignments.TryGetValue(role, out var field))
            {
                var comp = field.GetComponent();
                if (comp != null)
                {
                    return new BoundField { Target = comp, Member = field.Member };
                }
            }
            return null;
        }

        public static object GetRoleObject(string role)
        {
            if (_roleAssignments.TryGetValue(role, out int id))
            {
                if (_trackedObjects.TryGetValue(id, out var tracked))
                    return tracked.GetTarget();
            }
            return null;
        }

        public static string GetInferredRoles()
        {
             return string.Join(";", _roleAssignments.Select(kv => $"{kv.Key}={kv.Value}").ToArray());
        }
    }
}
