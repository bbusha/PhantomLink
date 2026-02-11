using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using MelonLoader;
using PhantomLink.MelonIntegration.Core;

namespace PhantomLink.MelonIntegration.Features
{
    public static class CheatManager
    {
        private static bool _godModeEnabled;
        private static bool _infiniteHealthEnabled;
        private static bool _infiniteAmmoEnabled;
        private static bool _infiniteStaminaEnabled;
        private static bool _noClipEnabled;

        private class BoundMember
        {
            public WeakReference TargetRef;
            public MemberInfo Member;
            public bool IsStatic;
            
            public bool IsValid => (IsStatic || (TargetRef != null && TargetRef.IsAlive));
            
            public object GetTarget() => IsStatic ? null : TargetRef?.Target;
        }

        private static BoundMember _healthMember;
        private static BoundMember _ammoMember;
        private static BoundMember _staminaMember;
        private static BoundMember _moneyMember;
        
        private static WeakReference _playerRef;
        private static List<WeakReference> _disabledColliders = new List<WeakReference>();
        
        private static long _lastScanTicks;

        private static long _lastLogTicks;
        private static void LogErrorThrottled(string msg)
        {
            if (System.Diagnostics.Stopwatch.GetTimestamp() - _lastLogTicks > System.Diagnostics.Stopwatch.Frequency * 2) // 2 sec throttle
            {
                _lastLogTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                MelonLogger.Error(msg);
            }
        }

        public static string GetHealthValue()
        {
            if (_healthMember == null || !_healthMember.IsValid) ScanForMembers();
            return GetValueString(_healthMember);
        }

        public static string GetAmmoValue()
        {
            if (_ammoMember == null || !_ammoMember.IsValid) ScanForMembers();
            return GetValueString(_ammoMember);
        }

        public static string GetMoneyValue()
        {
            if (_moneyMember == null || !_moneyMember.IsValid) ScanForMembers();
            return GetValueString(_moneyMember);
        }

        public static string GetStaminaValue()
        {
            if (_staminaMember == null || !_staminaMember.IsValid) ScanForMembers();
            return GetValueString(_staminaMember);
        }

        private static string GetValueString(BoundMember bound)
        {
            if (bound == null || !bound.IsValid) return "0";
            try
            {
                var target = bound.GetTarget();
                if (!bound.IsStatic && target == null) return "0";

                if (bound.Member is FieldInfo fi)
                    return Convert.ToString(fi.GetValue(target));
                else if (bound.Member is PropertyInfo pi)
                    return Convert.ToString(pi.GetValue(target, null));
            }
            catch { }
            return "0";
        }

        public static void OnUpdate()
        {
            if (!_godModeEnabled && !_infiniteHealthEnabled && !_infiniteAmmoEnabled && !_infiniteStaminaEnabled && !_noClipEnabled)
                return;

            try
            {
                if (System.Diagnostics.Stopwatch.GetTimestamp() - _lastScanTicks > System.Diagnostics.Stopwatch.Frequency)
                {
                    _lastScanTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    ScanForMembers();
                }

                if (_godModeEnabled || _infiniteHealthEnabled)
                    ApplyValue(_healthMember, 9999f);
                
                if (_infiniteAmmoEnabled)
                    ApplyValue(_ammoMember, 999f);
                    
                if (_infiniteStaminaEnabled)
                    ApplyValue(_staminaMember, 999f);
                    
                if (_noClipEnabled)
                    ApplyNoClip();
            }
            catch (Exception ex)
            {
                LogErrorThrottled($"CheatManager OnUpdate error: {ex.Message}");
            }
        }

        public static string SetCheat(string args, string[] parts)
        {
            // SET_CHEAT|name|value (0/1)
            try
            {
                if (parts.Length < 3) return "ERROR|Invalid format";
                var name = parts[1].ToUpper();
                var val = parts[2] == "1" || parts[2].Equals("true", StringComparison.OrdinalIgnoreCase);

                switch (name)
                {
                    case "GODMODE": _godModeEnabled = val; break;
                    case "INFINITE_HEALTH": _infiniteHealthEnabled = val; break;
                    case "INFINITE_AMMO": _infiniteAmmoEnabled = val; break;
                    case "INFINITE_STAMINA": _infiniteStaminaEnabled = val; break;
                    case "NOCLIP": 
                        _noClipEnabled = val;
                        if (!val) RestoreColliders();
                        break;
                    default: return "ERROR|Unknown cheat";
                }
                
                if (val) _lastScanTicks = 0; // Force scan

                MelonLogger.Msg($"Cheat {name} set to {val}");
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"SetCheat error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        private static void ApplyValue(BoundMember bound, float value)
        {
            if (bound == null || !bound.IsValid) return;

            try
            {
                var target = bound.GetTarget();
                if (!bound.IsStatic && target == null) return;

                if (bound.Member is FieldInfo fi)
                {
                    var current = Convert.ToSingle(fi.GetValue(target));
                    if (current < value)
                        fi.SetValue(target, Convert.ChangeType(value, fi.FieldType));
                }
                else if (bound.Member is PropertyInfo pi)
                {
                    if (pi.CanRead && pi.CanWrite)
                    {
                        var current = Convert.ToSingle(pi.GetValue(target, null));
                        if (current < value)
                            pi.SetValue(target, Convert.ChangeType(value, pi.PropertyType), null);
                    }
                }
            }
            catch (Exception ex)
            {
                // Only log if it's not a common "object destroyed" error
                if (!(ex is TargetInvocationException))
                    LogErrorThrottled($"ApplyValue error for {bound.Member.Name}: {ex.Message}");
            }
        }
        
        private static void ApplyNoClip()
        {
             // Try to get player from Analyzer, fallback to existing ref
             var player = BehaviorAnalyzer.GetRoleObject("Player");
             if (player != null)
             {
                 if (_playerRef == null || _playerRef.Target != player)
                     _playerRef = new WeakReference(player);
             }
             
             if (_playerRef == null || !_playerRef.IsAlive) return;
             player = _playerRef.Target;
             if (player == null) return;
             
             // Disable CharacterController
             var cc = GetComponent(player, "UnityEngine.CharacterController");
             if (cc != null && GetEnabled(cc))
             {
                 SetEnabled(cc, false);
                 if (!_disabledColliders.Any(x => x.Target == cc))
                    _disabledColliders.Add(new WeakReference(cc));
             }
             
             // Disable Colliders (3D)
             var colliders = GetComponents(player, "UnityEngine.Collider");
             if (colliders != null)
             {
                 foreach (var c in colliders)
                 {
                     if (c == null) continue;
                     if (GetEnabled(c))
                     {
                         SetEnabled(c, false);
                         if (!_disabledColliders.Any(x => x.Target == c))
                            _disabledColliders.Add(new WeakReference(c));
                     }
                 }
             }

             // Disable Colliders (2D)
             var colliders2D = GetComponents(player, "UnityEngine.Collider2D");
             if (colliders2D != null)
             {
                 foreach (var c in colliders2D)
                 {
                     if (c == null) continue;
                     if (GetEnabled(c))
                     {
                         SetEnabled(c, false);
                         if (!_disabledColliders.Any(x => x.Target == c))
                            _disabledColliders.Add(new WeakReference(c));
                     }
                 }
             }
             
             // Disable Rigidbody physics (3D)
             var rb = GetComponent(player, "UnityEngine.Rigidbody");
             if (rb != null)
             {
                 var detect = ReflectionHelper.GetProperty(rb.GetType(), "detectCollisions");
                 if (detect != null) detect.SetValue(rb, false, null);
                 var kin = ReflectionHelper.GetProperty(rb.GetType(), "isKinematic");
                 if (kin != null) kin.SetValue(rb, true, null);
             }

             // Disable Rigidbody physics (2D)
             var rb2d = GetComponent(player, "UnityEngine.Rigidbody2D");
             if (rb2d != null)
             {
                 var sim = ReflectionHelper.GetProperty(rb2d.GetType(), "simulated");
                 if (sim != null) sim.SetValue(rb2d, false, null); // Simulating false disables physics
                 var kin = ReflectionHelper.GetProperty(rb2d.GetType(), "isKinematic");
                 if (kin != null) kin.SetValue(rb2d, true, null);
             }
        }

        private static void RestoreColliders()
        {
            foreach (var w in _disabledColliders)
            {
                if (w.IsAlive && w.Target != null)
                {
                    SetEnabled(w.Target, true);
                }
            }
            _disabledColliders.Clear();
            
            if (_playerRef != null && _playerRef.IsAlive)
            {
                var player = _playerRef.Target;
                
                // Restore Rigidbody 3D
                var rb = GetComponent(player, "UnityEngine.Rigidbody");
                if (rb != null)
                {
                    var detect = ReflectionHelper.GetProperty(rb.GetType(), "detectCollisions");
                    if (detect != null) detect.SetValue(rb, true, null);
                    var kin = ReflectionHelper.GetProperty(rb.GetType(), "isKinematic");
                    // We assume false is default for player, but ideally we should have stored it.
                    // For now, setting false is the standard "Walk" mode.
                    if (kin != null) kin.SetValue(rb, false, null); 
                }

                // Restore Rigidbody 2D
                var rb2d = GetComponent(player, "UnityEngine.Rigidbody2D");
                if (rb2d != null)
                {
                    var sim = ReflectionHelper.GetProperty(rb2d.GetType(), "simulated");
                    if (sim != null) sim.SetValue(rb2d, true, null);
                    var kin = ReflectionHelper.GetProperty(rb2d.GetType(), "isKinematic");
                    if (kin != null) kin.SetValue(rb2d, false, null);
                }
            }
        }

        private static void ScanForMembers()
        {
            // Use BehaviorAnalyzer inferred player if available
            var player = BehaviorAnalyzer.GetRoleObject("Player") ?? FindPlayerObject();
            
            if (player != null)
            {
                if (_playerRef == null || _playerRef.Target != player)
                    _playerRef = new WeakReference(player);
                
                // Try BehaviorAnalyzer fields first
                if (_healthMember == null || !_healthMember.IsValid)
                {
                    var field = BehaviorAnalyzer.GetRoleField("Player.Health");
                    if (field != null)
                    {
                        _healthMember = new BoundMember { TargetRef = new WeakReference(field.Target), Member = field.Member, IsStatic = false };
                    }
                    else
                    {
                        // Fallback to name heuristic - Expanded list
                        _healthMember = FindMember(player, new[] { "health", "hp", "life", "currenthp", "currenthealth", "_health", "_hp", "healthpoint", "healthpoints" });
                    }
                }
                
                if (_ammoMember == null || !_ammoMember.IsValid)
                {
                    var field = BehaviorAnalyzer.GetRoleField("Player.Ammo");
                    if (field != null)
                    {
                        _ammoMember = new BoundMember { TargetRef = new WeakReference(field.Target), Member = field.Member, IsStatic = false };
                    }
                    else
                    {
                         _ammoMember = FindMember(player, new[] { "ammo", "ammunition", "bullets", "clip", "magazine", "curammo", "currentammo" });
                    }
                }
                    
                if (_staminaMember == null || !_staminaMember.IsValid)
                    _staminaMember = FindMember(player, new[] { "stamina", "energy", "endurance", "sp", "mana", "sprint", "sprintmeter", "staminameter" });
            }

            // Global Members (Currency, etc)
            if (_moneyMember == null || !_moneyMember.IsValid)
            {
                // 1. Try Player first
                _moneyMember = FindMember(player, new[] { "money", "currency", "gold", "credits", "balance", "cash", "coins", "groupCredits" });
                
                // 2. Try Global Singletons if not found
                if (_moneyMember == null)
                {
                    _moneyMember = ScanGlobalSingletons(new[] { "money", "currency", "gold", "credits", "balance", "cash", "coins", "groupCredits" });
                }
            }
        }

        private static BoundMember ScanGlobalSingletons(string[] names)
        {
            string[] managers = { "GameManager", "LevelManager", "Terminal", "TimeOfDay", "EconomyManager", "MoneyManager", "ScoreManager" };
            
            var goType = ReflectionHelper.FindType("UnityEngine.GameObject");
            if (goType == null) return null;
            var find = ReflectionHelper.GetMethod(goType, "Find", new[] { typeof(string) });
            if (find == null) return null;

            foreach (var managerName in managers)
            {
                try
                {
                    var go = find.Invoke(null, new object[] { managerName });
                    if (go != null)
                    {
                        var member = FindMember(go, names);
                        if (member != null) return member;
                    }
                }
                catch { }
            }
            
            // Try FindObjectOfType for unique scripts like "Terminal"
            var unityObj = ReflectionHelper.FindType("UnityEngine.Object");
            var findType = ReflectionHelper.GetMethod(unityObj, "FindObjectOfType", new[] { typeof(Type) });
            
            // Heuristic: Try to find a type named exactly "Terminal" or "GameManager"
            if (findType != null)
            {
                foreach (var typeName in managers)
                {
                    // This is tricky because we don't know the namespace. 
                    // But we can try to find the type by name if it's unique enough or we iterate all types (slow).
                    // For now, let's rely on GameObject.Find which covers singletons.
                }
            }

            return null;
        }

        public static object FindPlayerObject()
        {
            // 1. Behavior Analyzer (Dynamic)
            var behaviorPlayer = BehaviorAnalyzer.GetRoleObject("Player");
            if (behaviorPlayer != null) return behaviorPlayer;

            var goType = ReflectionHelper.FindType("UnityEngine.GameObject");
            if (goType == null) return null;

            // 2. Standard Tag "Player"
            var findWithTag = ReflectionHelper.GetMethod(goType, "FindWithTag", new[] { typeof(string) });
            if (findWithTag != null)
            {
                try 
                { 
                    var result = findWithTag.Invoke(null, new object[] { "Player" }); 
                    if (result != null) return result;
                } 
                catch { }
            }
            
            // 3. Common Names
            var find = ReflectionHelper.GetMethod(goType, "Find", new[] { typeof(string) });
            if (find != null)
            {
                string[] names = { "Player", "LocalPlayer", "FirstPersonController", "FPSController", "PlayerController", "Char", "Character" };
                foreach (var name in names)
                {
                    try 
                    { 
                        var result = find.Invoke(null, new object[] { name });
                        if (result != null) return result;
                    } 
                    catch { }
                }
            }

            // 4. Camera Root (often the player)
            var cam = CameraManager.GetMainCamera();
            if (cam != null)
            {
                var trans = CameraManager.GetTransform(cam);
                if (trans != null)
                {
                    var rootProp = ReflectionHelper.GetProperty(trans.GetType(), "root");
                    var root = rootProp?.GetValue(trans, null);
                    if (root != null)
                    {
                        var rootGoProp = ReflectionHelper.GetProperty(root.GetType(), "gameObject");
                        var rootGo = rootGoProp?.GetValue(root, null);
                        if (rootGo != null) return rootGo;
                    }
                }
            }

            // 5. Component Search (CharacterController)
            var ccType = ReflectionHelper.FindType("UnityEngine.CharacterController");
            if (ccType != null)
            {
                 var findObj = ReflectionHelper.GetMethod(ReflectionHelper.FindType("UnityEngine.Object"), "FindObjectOfType", new[] { typeof(Type) });
                 if (findObj != null)
                 {
                     try
                     {
                         var cc = findObj.Invoke(null, new object[] { ccType });
                         if (cc != null)
                         {
                             var goProp = ReflectionHelper.GetProperty(ccType, "gameObject");
                             return goProp?.GetValue(cc, null);
                         }
                     }
                     catch { }
                 }
            }

            return null;
        }

        private static BoundMember FindMember(object target, string[] names)
        {
            if (target == null) return null;
            
            if (ReflectionHelper.TryGetTransform(target, out var tr)) 
            {
                var go = ReflectionHelper.TryGetTransformGameObject(target) ?? target;
                var comps = ReflectionHelper.FindType("UnityEngine.Component");
                var getComps = ReflectionHelper.GetMethod(go.GetType(), "GetComponents", new[] { typeof(Type) });
                
                if (getComps != null)
                {
                    var components = getComps.Invoke(go, new object[] { comps }) as Array;
                    if (components != null)
                    {
                        foreach (var comp in components)
                        {
                            if (comp == null) continue;
                            var member = FindMemberOnType(comp, names);
                            if (member != null) return member;
                        }
                    }
                }
                
                // ALSO Check children (Breadth-First for immediate children)
                var transformType = tr.GetType();
                var childCountProp = ReflectionHelper.GetProperty(transformType, "childCount");
                var getChildMethod = ReflectionHelper.GetMethod(transformType, "GetChild", new[] { typeof(int) });
                
                if (childCountProp != null && getChildMethod != null)
                {
                    int count = (int)childCountProp.GetValue(tr, null);
                    for (int i = 0; i < count; i++)
                    {
                        var childTr = getChildMethod.Invoke(tr, new object[] { i });
                        if (childTr != null)
                        {
                            // Check components on child
                            var childGoProp = ReflectionHelper.GetProperty(transformType, "gameObject");
                            var childGo = childGoProp?.GetValue(childTr, null);
                            if (childGo != null)
                            {
                                var childComps = getComps.Invoke(childGo, new object[] { comps }) as Array;
                                if (childComps != null)
                                {
                                    foreach (var comp in childComps)
                                    {
                                        if (comp == null) continue;
                                        var member = FindMemberOnType(comp, names);
                                        if (member != null) return member;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            
            return FindMemberOnType(target, names);
        }

        private static BoundMember FindMemberOnType(object target, string[] names)
        {
            var type = target.GetType();
            foreach (var name in names)
            {
                var f = ReflectionHelper.FindField(type, name);
                if (f != null && IsNumeric(f.FieldType))
                    return new BoundMember { TargetRef = new WeakReference(target), Member = f, IsStatic = false };
                    
                var p = ReflectionHelper.FindProperty(type, name);
                if (p != null && IsNumeric(p.PropertyType) && p.CanWrite)
                    return new BoundMember { TargetRef = new WeakReference(target), Member = p, IsStatic = false };
            }
            return null;
        }

        private static bool IsNumeric(Type t)
        {
            return t == typeof(int) || t == typeof(float) || t == typeof(double) || t == typeof(long);
        }
        
        // Helpers
        private static object GetComponent(object go, string typeName)
        {
            if (go == null) return null;
            var type = ReflectionHelper.FindType(typeName);
            if (type == null) return null;
            
            var goObj = ReflectionHelper.TryGetTransformGameObject(go) ?? go;
            
            var getComp = ReflectionHelper.GetMethod(goObj.GetType(), "GetComponent", new[] { typeof(Type) });
            return getComp?.Invoke(goObj, new object[] { type });
        }
        
        private static Array GetComponents(object go, string typeName)
        {
             if (go == null) return null;
             var type = ReflectionHelper.FindType(typeName);
             if (type == null) return null;
             
             var goObj = ReflectionHelper.TryGetTransformGameObject(go) ?? go;

             var getComps = ReflectionHelper.GetMethod(goObj.GetType(), "GetComponents", new[] { typeof(Type) });
             return getComps?.Invoke(goObj, new object[] { type }) as Array;
        }
        
        private static void SetEnabled(object comp, bool val)
        {
            if (comp == null) return;
            var prop = ReflectionHelper.GetProperty(comp.GetType(), "enabled");
            prop?.SetValue(comp, val, null);
        }
        
        private static bool GetEnabled(object comp)
        {
             if (comp == null) return false;
             var prop = ReflectionHelper.GetProperty(comp.GetType(), "enabled");
             return (bool)(prop?.GetValue(comp, null) ?? false);
        }
    }
}
