using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using MelonLoader;
using Newtonsoft.Json;

namespace PhantomLink.MelonIntegration.Core
{
    /// <summary>
    /// Advanced reflection helper with aggressive caching and universal compatibility support
    /// </summary>
    public static class ReflectionHelper
    {
        private static readonly object _typeCacheLock = new object();
        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
        
        // Cache for member lookups to avoid repeated reflection costs
        private static readonly object _memberCacheLock = new object();
        private static readonly Dictionary<string, MemberInfo> _memberCache = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);

        public static bool IsIL2CPP { get; set; }

        /// <summary>
        /// clear caches to free memory
        /// </summary>
        public static void ClearCache()
        {
            lock (_typeCacheLock) _typeCache.Clear();
            lock (_memberCacheLock) _memberCache.Clear();
        }

        /// <summary>
        /// Finds a type by name, searching all assemblies and handling IL2CPP specific naming
        /// </summary>
        public static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

            lock (_typeCacheLock)
            {
                if (_typeCache.TryGetValue(fullName, out var cached))
                    return cached;
            }

            Type type = FindTypeInternal(fullName);

            if (type != null)
            {
                lock (_typeCacheLock)
                {
                    _typeCache[fullName] = type;
                }
            }

            return type;
        }

        private static Type FindTypeInternal(string fullName)
        {
            // 1. Try direct Type.GetType
            try
            {
                var direct = Type.GetType(fullName, false);
                if (direct != null) return direct;
            }
            catch { }

            // 2. IL2CPP specific lookups
            if (IsIL2CPP && fullName.StartsWith("Il2CppSystem.", StringComparison.Ordinal))
            {
                var candidates = new[] { "Il2Cppmscorlib", "Il2CppSystem", "Il2CppInterop.Runtime" };
                foreach (var asmName in candidates)
                {
                    try
                    {
                        var t = Type.GetType($"{fullName}, {asmName}", false);
                        if (t != null) return t;
                    }
                    catch { }
                }
            }

            // 3. Search all loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }

            // 4. Fuzzy search (expensive, last resort)
            // If the user provided a short name "GameObject", try to find "UnityEngine.GameObject"
            if (!fullName.Contains("."))
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        // This is slow, but useful for interactive console
                        foreach (var t in asm.GetTypes())
                        {
                            if (t.Name == fullName) return t;
                        }
                    }
                    catch { }
                }
            }

            return null;
        }

        public static MethodInfo FindMethod(Type type, string methodName, Type[] paramTypes = null)
        {
            if (type == null || string.IsNullOrEmpty(methodName)) return null;

            string key = $"{type.FullName}:{methodName}";
            if (paramTypes != null)
            {
                foreach (var p in paramTypes) key += $":{p.Name}";
            }

            lock (_memberCacheLock)
            {
                if (_memberCache.TryGetValue(key, out var cached) && cached is MethodInfo mi)
                    return mi;
            }

            MethodInfo method = null;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

            if (paramTypes != null)
            {
                method = type.GetMethod(methodName, flags, null, paramTypes, null);
            }
            else
            {
                method = type.GetMethod(methodName, flags);
            }

            if (method != null)
            {
                lock (_memberCacheLock) _memberCache[key] = method;
            }

            return method;
        }

        public static FieldInfo FindField(Type type, string fieldName)
        {
            if (type == null || string.IsNullOrEmpty(fieldName)) return null;
            
            string key = $"F:{type.FullName}:{fieldName}";
            lock (_memberCacheLock)
            {
                if (_memberCache.TryGetValue(key, out var cached) && cached is FieldInfo fi)
                    return fi;
            }

            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            
            // Recursively check base types if not found
            if (field == null && type.BaseType != null)
            {
                field = FindField(type.BaseType, fieldName);
            }

            if (field != null)
            {
                lock (_memberCacheLock) _memberCache[key] = field;
            }

            return field;
        }

        public static PropertyInfo FindProperty(Type type, string propName)
        {
            if (type == null || string.IsNullOrEmpty(propName)) return null;

            string key = $"P:{type.FullName}:{propName}";
            lock (_memberCacheLock)
            {
                if (_memberCache.TryGetValue(key, out var cached) && cached is PropertyInfo pi)
                    return pi;
            }

            var prop = type.GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

            if (prop != null)
            {
                lock (_memberCacheLock) _memberCache[key] = prop;
            }

            return prop;
        }

        /// <summary>
        /// Safe value formatting for IPC transmission
        /// </summary>
        public static string FormatValue(object value)
        {
            if (value == null) return "null";
            if (value is string s) return s;
            
            // Check if it's a Unity Object (via name convention or type check)
            var type = value.GetType();
            if (IsUnityObject(type))
            {
                try
                {
                    var nameProp = type.GetProperty("name");
                    var name = nameProp?.GetValue(value, null) as string ?? "Unnamed";
                    
                    var idMethod = type.GetMethod("GetInstanceID");
                    var id = idMethod?.Invoke(value, null) ?? 0;
                    
                    return $"{name} (InstanceID: {id})";
                }
                catch
                {
                    return value.ToString();
                }
            }
            
            return value.ToString();
        }

        private static bool IsUnityObject(Type type)
        {
            while (type != null)
            {
                if (type.FullName == "UnityEngine.Object" || type.Name == "Object" && type.Namespace == "UnityEngine")
                    return true;
                type = type.BaseType;
            }
            return false;
        }

        public static PropertyInfo GetProperty(Type type, string name) => FindProperty(type, name);
        public static MethodInfo GetMethod(Type type, string name, Type[] paramsTypes = null) => FindMethod(type, name, paramsTypes);

        public static string GetUnityObjectName(object obj)
        {
            if (obj == null) return null;
            try
            {
                var prop = FindProperty(obj.GetType(), "name");
                return prop?.GetValue(obj, null) as string;
            }
            catch { return null; }
        }

        public static bool TryGetUnityInstanceId(object obj, out int id)
        {
            id = 0;
            if (obj == null) return false;
            try
            {
                var m = FindMethod(obj.GetType(), "GetInstanceID");
                if (m != null)
                {
                    var res = m.Invoke(obj, null);
                    if (res is int i) { id = i; return true; }
                    id = Convert.ToInt32(res);
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static bool GetActiveInHierarchy(object go)
        {
            if (go == null) return false;
            try
            {
                var prop = FindProperty(go.GetType(), "activeInHierarchy");
                if (prop != null) return (bool)prop.GetValue(go, null);
            }
            catch { }
            return false;
        }

        public static bool TryGetTransform(object go, out object transform)
        {
            transform = null;
            if (go == null) return false;
            try
            {
                var prop = FindProperty(go.GetType(), "transform");
                if (prop != null)
                {
                    transform = prop.GetValue(go, null);
                    return transform != null;
                }
            }
            catch { }
            return false;
        }

        public static object TryGetTransformGameObject(object transform)
        {
            if (transform == null) return null;
            try
            {
                var prop = FindProperty(transform.GetType(), "gameObject");
                return prop?.GetValue(transform, null);
            }
            catch { return null; }
        }

        public static string FormatVector3(object vector3)
        {
            if (vector3 == null) return "";
            try
            {
                var t = vector3.GetType();
                var x = (float)FindField(t, "x").GetValue(vector3);
                var y = (float)FindField(t, "y").GetValue(vector3);
                var z = (float)FindField(t, "z").GetValue(vector3);
                return $"{x:F3},{y:F3},{z:F3}";
            }
            catch { return vector3.ToString(); }
        }

        public static List<object> GetLoadedSceneRootGameObjects(Type goType, int limit)
        {
            var list = new List<object>();
            try
            {
                var sceneManager = FindType("UnityEngine.SceneManagement.SceneManager");
                if (sceneManager == null) return list;

                var countProp = FindProperty(sceneManager, "sceneCount");
                var getSceneAt = FindMethod(sceneManager, "GetSceneAt", new[] { typeof(int) });
                
                if (countProp == null || getSceneAt == null) return list;

                var count = (int)countProp.GetValue(null, null);
                for (int i = 0; i < count; i++)
                {
                    var scene = getSceneAt.Invoke(null, new object[] { i });
                    if (scene == null) continue;

                    var getRoots = FindMethod(scene.GetType(), "GetRootGameObjects");
                    if (getRoots == null) continue;

                    var roots = (Array)getRoots.Invoke(scene, null);
                    if (roots == null) continue;

                    foreach (var root in roots)
                    {
                        if (root != null) list.Add(root);
                        if (list.Count >= limit) return list;
                    }
                }
            }
            catch { }
            return list;
        }

        public static List<object> FindSceneGameObjects(Type goType, int limit)
        {
            // Fallback for older Unity versions or if SceneManager fails
            var list = new List<object>();
            try
            {
                var objects = FindType("UnityEngine.Object").GetMethod("FindObjectsOfType", new[] { typeof(Type) })
                    .Invoke(null, new object[] { goType }) as Array;
                
                if (objects != null)
                {
                    foreach (var obj in objects)
                    {
                        if (obj == null) continue;
                        
                        // Check if it's a root
                        if (TryGetTransform(obj, out var tr))
                        {
                            var parent = FindProperty(tr.GetType(), "parent").GetValue(tr, null);
                            if (parent == null)
                            {
                                list.Add(obj);
                                if (list.Count >= limit) return list;
                            }
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        public static void PushGameObjectChildren(object go, Stack<object> stack)
        {
            if (go == null) return;
            try
            {
                if (TryGetTransform(go, out var tr))
                {
                    var childCountProp = FindProperty(tr.GetType(), "childCount");
                    var getChildMethod = FindMethod(tr.GetType(), "GetChild", new[] { typeof(int) });

                    if (childCountProp != null && getChildMethod != null)
                    {
                        var count = (int)childCountProp.GetValue(tr, null);
                        for (int i = 0; i < count; i++)
                        {
                            var childTr = getChildMethod.Invoke(tr, new object[] { i });
                            var childGo = TryGetTransformGameObject(childTr);
                            if (childGo != null)
                                stack.Push(childGo);
                        }
                    }
                }
            }
            catch { }
        }

        public static List<string> GetComponentTypeNames(object go)
        {
            var names = new List<string>();
            if (go == null) return names;
            try
            {
                var getComps = FindMethod(go.GetType(), "GetComponents", new[] { typeof(Type) });
                var compType = FindType("UnityEngine.Component");
                
                if (getComps != null && compType != null)
                {
                    var comps = getComps.Invoke(go, new object[] { compType }) as Array;
                    if (comps != null)
                    {
                        foreach (var c in comps)
                        {
                            if (c != null)
                                names.Add(c.GetType().Name);
                        }
                    }
                }
            }
            catch { }
            return names;
        }

        public static bool TryGetRenderableBounds(object go, out string center, out string size)
        {
            center = ""; size = "";
            if (go == null) return false;
            try
            {
                var rendererType = FindType("UnityEngine.Renderer");
                if (rendererType == null) return false;

                var getComp = FindMethod(go.GetType(), "GetComponent", new[] { typeof(Type) });
                var renderer = getComp?.Invoke(go, new object[] { rendererType });
                
                if (renderer != null)
                {
                    var boundsProp = FindProperty(renderer.GetType(), "bounds");
                    var bounds = boundsProp?.GetValue(renderer, null);
                    if (bounds != null)
                    {
                        var centerProp = FindProperty(bounds.GetType(), "center");
                        var sizeProp = FindProperty(bounds.GetType(), "size");
                        center = FormatVector3(centerProp?.GetValue(bounds, null));
                        size = FormatVector3(sizeProp?.GetValue(bounds, null));
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static bool TryGetColliderBounds(object go, out string center, out string size)
        {
            center = ""; size = "";
            if (go == null) return false;
            try
            {
                var colliderType = FindType("UnityEngine.Collider");
                if (colliderType == null) return false;

                var getComp = FindMethod(go.GetType(), "GetComponent", new[] { typeof(Type) });
                var collider = getComp?.Invoke(go, new object[] { colliderType });

                if (collider != null)
                {
                    var boundsProp = FindProperty(collider.GetType(), "bounds");
                    var bounds = boundsProp?.GetValue(collider, null);
                    if (bounds != null)
                    {
                        var centerProp = FindProperty(bounds.GetType(), "center");
                        var sizeProp = FindProperty(bounds.GetType(), "size");
                        center = FormatVector3(centerProp?.GetValue(bounds, null));
                        size = FormatVector3(sizeProp?.GetValue(bounds, null));
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static object ParseValue(string value, Type targetType)
        {
            if (string.IsNullOrEmpty(value) || value == "null") return null;
            if (targetType == typeof(string)) return value;
            
            try 
            {
                if (targetType == typeof(int)) return int.Parse(value);
                if (targetType == typeof(float)) return float.Parse(value);
                if (targetType == typeof(double)) return double.Parse(value);
                if (targetType == typeof(bool)) return bool.Parse(value);
                if (targetType.IsEnum) return Enum.Parse(targetType, value, true);
                
                // Try JSON for complex types
                if (value.StartsWith("{") || value.StartsWith("[") || value.StartsWith("\""))
                {
                    return JsonConvert.DeserializeObject(value, targetType);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Failed to parse value '{value}' as {targetType.Name}: {ex.Message}");
            }
            
            try
            {
                return Convert.ChangeType(value, targetType);
            }
            catch
            {
                return null;
            }
        }
    }
}
