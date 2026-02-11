using System;
using System.Reflection;
using System.Diagnostics;
using PhantomLink.MelonIntegration.Core;

namespace PhantomLink.MelonIntegration.Features
{
    public static class OverlayManager
    {
        private static bool _fpsEnabled;
        private static bool _collidersEnabled;
        
        // FPS State
        private static double _fps;
        private static long _lastFpsTicks;
        private static Stopwatch _fpsStopwatch = Stopwatch.StartNew();

        // Collider Cache
        private static Array _colliderCache;
        private static long _lastColliderScan;

        public static void OnGUI()
        {
            if (_fpsEnabled) DrawFPS();
            if (_collidersEnabled) DrawColliders();
        }

        public static string ToggleFPS(string args, string[] parts)
        {
            _fpsEnabled = !_fpsEnabled;
            return $"SUCCESS|FPS_DISPLAY|{_fpsEnabled}";
        }

        public static string ToggleColliders(string args, string[] parts)
        {
            _collidersEnabled = !_collidersEnabled;
            return $"SUCCESS|COLLIDERS|{_collidersEnabled}";
        }

        private static void DrawFPS()
        {
            try
            {
                UpdateFPS();
                
                var gui = ReflectionHelper.FindType("UnityEngine.GUI");
                var rectType = ReflectionHelper.FindType("UnityEngine.Rect");
                
                if (gui == null || rectType == null) return;

                var label = ReflectionHelper.GetMethod(gui, "Label", new[] { rectType, typeof(string) });
                if (label == null) return;

                var rect = Activator.CreateInstance(rectType, new object[] { 10f, 10f, 400f, 40f });
                var mem = (Process.GetCurrentProcess().PrivateMemorySize64 / 1024d / 1024d).ToString("F0");
                label.Invoke(null, new object[] { rect, $"FPS: {_fps:F1} | MEM: {mem} MB" });
            }
            catch { }
        }

        private static void UpdateFPS()
        {
            var ticks = _fpsStopwatch.ElapsedTicks;
            if (_lastFpsTicks == 0)
            {
                _lastFpsTicks = ticks;
                return;
            }
            var dt = (ticks - _lastFpsTicks) / (double)Stopwatch.Frequency;
            _lastFpsTicks = ticks;
            if (dt <= 0) return;
            var current = 1.0 / dt;
            if (_fps <= 0) _fps = current;
            else _fps = (_fps * 0.9) + (current * 0.1);
        }

        private static void DrawColliders()
        {
            try
            {
                var camera = GetMainCamera();
                if (camera == null) return;

                var colliderType = ReflectionHelper.FindType("UnityEngine.Collider");
                var gui = ReflectionHelper.FindType("UnityEngine.GUI");
                var rectType = ReflectionHelper.FindType("UnityEngine.Rect");
                var texture2d = ReflectionHelper.FindType("UnityEngine.Texture2D");
                var texture = ReflectionHelper.FindType("UnityEngine.Texture");
                var vector3 = ReflectionHelper.FindType("UnityEngine.Vector3");
                var screen = ReflectionHelper.FindType("UnityEngine.Screen");

                if (colliderType == null || gui == null || rectType == null) return;

                var now = Stopwatch.GetTimestamp();
                if (_colliderCache == null || _lastColliderScan == 0 || (now - _lastColliderScan) > (Stopwatch.Frequency / 2))
                {
                    var unityObj = ReflectionHelper.FindType("UnityEngine.Object");
                    var find = ReflectionHelper.GetMethod(unityObj, "FindObjectsOfType", new[] { typeof(Type) });
                    if (find != null)
                    {
                        _colliderCache = find.Invoke(null, new object[] { colliderType }) as Array;
                        _lastColliderScan = now;
                    }
                }

                if (_colliderCache == null || _colliderCache.Length == 0) return;

                var drawTexture = ReflectionHelper.GetMethod(gui, "DrawTexture", new[] { rectType, texture });
                var whiteTextureProp = ReflectionHelper.GetProperty(texture2d, "whiteTexture");
                var whiteTexture = whiteTextureProp?.GetValue(null, null);
                
                var worldToScreen = ReflectionHelper.GetMethod(camera.GetType(), "WorldToScreenPoint", new[] { vector3 });
                var screenHeightProp = ReflectionHelper.GetProperty(screen, "height");
                var screenHeight = Convert.ToSingle(screenHeightProp.GetValue(null, null));

                if (drawTexture == null || whiteTexture == null || worldToScreen == null) return;

                var boundsProp = ReflectionHelper.GetProperty(colliderType, "bounds");
                
                int budget = 100; // Limit per frame
                for (int i = 0; i < _colliderCache.Length && budget > 0; i++)
                {
                    var c = _colliderCache.GetValue(i);
                    if (c == null) continue;

                    var bounds = boundsProp.GetValue(c, null);
                    if (bounds == null) continue;

                    var bt = bounds.GetType();
                    var center = ReadVector3(ReflectionHelper.GetProperty(bt, "center").GetValue(bounds, null));
                    var extents = ReadVector3(ReflectionHelper.GetProperty(bt, "extents").GetValue(bounds, null));

                    float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
                    float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
                    bool any = false;

                    // 8 corners
                    for (int xi = 0; xi < 2; xi++)
                    for (int yi = 0; yi < 2; yi++)
                    for (int zi = 0; zi < 2; zi++)
                    {
                        var x = center.X + (xi == 0 ? -extents.X : extents.X);
                        var y = center.Y + (yi == 0 ? -extents.Y : extents.Y);
                        var z = center.Z + (zi == 0 ? -extents.Z : extents.Z);
                        
                        var corner = Activator.CreateInstance(vector3, new object[] { x, y, z });
                        var screenPos = worldToScreen.Invoke(camera, new object[] { corner });
                        var sp = ReadVector3(screenPos);

                        if (sp.Z <= 0) continue;

                        var sx = sp.X;
                        var sy = screenHeight - sp.Y;

                        if (!any) { minX = maxX = sx; minY = maxY = sy; any = true; }
                        else
                        {
                            if (sx < minX) minX = sx; if (sx > maxX) maxX = sx;
                            if (sy < minY) minY = sy; if (sy > maxY) maxY = sy;
                        }
                    }

                    if (!any) continue;
                    
                    var w = maxX - minX;
                    var h = maxY - minY;
                    if (w < 2 || h < 2) continue;

                    DrawBox(drawTexture, rectType, whiteTexture, minX, minY, w, h);
                    budget--;
                }
            }
            catch { }
        }

        private static void DrawBox(MethodInfo draw, Type rectType, object texture, float x, float y, float w, float h)
        {
            var t = 1f;
            draw.Invoke(null, new object[] { Activator.CreateInstance(rectType, new object[] { x, y, w, t }), texture });
            draw.Invoke(null, new object[] { Activator.CreateInstance(rectType, new object[] { x, y + h - t, w, t }), texture });
            draw.Invoke(null, new object[] { Activator.CreateInstance(rectType, new object[] { x, y, t, h }), texture });
            draw.Invoke(null, new object[] { Activator.CreateInstance(rectType, new object[] { x + w - t, y, t, h }), texture });
        }

        private static object GetMainCamera()
        {
            var cam = ReflectionHelper.FindType("UnityEngine.Camera");
            var main = ReflectionHelper.GetProperty(cam, "main");
            return main?.GetValue(null, null);
        }

        private struct Vec3 { public float X, Y, Z; }
        private static Vec3 ReadVector3(object v)
        {
            var t = v.GetType();
            return new Vec3 
            { 
                X = (float)ReflectionHelper.FindField(t, "x").GetValue(v),
                Y = (float)ReflectionHelper.FindField(t, "y").GetValue(v),
                Z = (float)ReflectionHelper.FindField(t, "z").GetValue(v)
            };
        }
    }
}
