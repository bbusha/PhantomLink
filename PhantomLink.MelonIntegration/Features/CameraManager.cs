using System;
using System.Collections.Generic;
using System.Reflection;
using PhantomLink.MelonIntegration.Core;

using MelonLoader;

namespace PhantomLink.MelonIntegration.Features
{
    public static class CameraManager
    {
        private static bool _freeCamEnabled;
        private static bool _fullBrightEnabled;
        
        private static float _flySpeed = 10f;
        private static float _lookSpeed = 3f;
        private static float _fastMult = 5f;

        // State
        private static object _mainCamera;
        private static bool _hasStoredState;
        private static object _originalParent;
        private static object _originalLocalPos;
        private static object _originalLocalRot;
        
        // Fullbright State
        private static bool _ambientStored;
        private static object _originalAmbientLight;
        private static object _originalAmbientMode;
        private static bool _originalFog;
        private static float _originalAmbientIntensity;
        private static object _fullbrightLight;
        private static int _lightUpdateFrame = 0;

        // Cache
        private static Type _inputType;
        private static MethodInfo _getKeyString;
        private static MethodInfo _getAxis;
        private static PropertyInfo _deltaTime;

        public static void OnUpdate()
        {
            try
            {
                if (_freeCamEnabled)
                {
                    UpdateFreeCam();
                }

                // Continuously apply fullbright to fight game logic resetting it
                if (_fullBrightEnabled)
                {
                    ApplyFullBright();
                }
            }
            catch (Exception ex)
            {
                // Throttle error logging
                if (_lightUpdateFrame % 60 == 0)
                    MelonLogger.Error($"CameraManager OnUpdate error: {ex.Message}");
            }
        }

        public static string ToggleFreeCam(string args, string[] parts)
        {
            try
            {
                _freeCamEnabled = !_freeCamEnabled;
                
                if (_freeCamEnabled)
                {
                    // Enable
                    if (_mainCamera == null) _mainCamera = GetMainCamera();
                    if (_mainCamera != null)
                    {
                        var trans = GetTransform(_mainCamera);
                        if (trans != null)
                        {
                            var parentProp = ReflectionHelper.GetProperty(trans.GetType(), "parent");
                            var localPosProp = ReflectionHelper.GetProperty(trans.GetType(), "localPosition");
                            var localRotProp = ReflectionHelper.GetProperty(trans.GetType(), "localRotation");
                            
                            _originalParent = parentProp?.GetValue(trans, null);
                            _originalLocalPos = localPosProp?.GetValue(trans, null);
                            _originalLocalRot = localRotProp?.GetValue(trans, null);
                            
                            // Detach
                            parentProp?.SetValue(trans, null, null);
                            _hasStoredState = true;
                        }
                    }
                }
                else
                {
                    // Disable & Restore
                    if (_hasStoredState && _mainCamera != null)
                    {
                        var trans = GetTransform(_mainCamera);
                        if (trans != null)
                        {
                            var parentProp = ReflectionHelper.GetProperty(trans.GetType(), "parent");
                            var localPosProp = ReflectionHelper.GetProperty(trans.GetType(), "localPosition");
                            var localRotProp = ReflectionHelper.GetProperty(trans.GetType(), "localRotation");
                            
                            if (parentProp != null) parentProp.SetValue(trans, _originalParent, null);
                            if (localPosProp != null && _originalLocalPos != null) localPosProp.SetValue(trans, _originalLocalPos, null);
                            if (localRotProp != null && _originalLocalRot != null) localRotProp.SetValue(trans, _originalLocalRot, null);
                        }
                    }
                    _hasStoredState = false;
                    _originalParent = null;
                    _originalLocalPos = null;
                    _originalLocalRot = null;
                }
                
                return $"SUCCESS|FREECAM|{_freeCamEnabled}";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"ToggleFreeCam error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        public static string ToggleFullBright(string args, string[] parts)
        {
            try
            {
                _fullBrightEnabled = !_fullBrightEnabled;
                
                if (_fullBrightEnabled)
                {
                    StoreAmbient();
                    ApplyFullBright();
                }
                else
                {
                    RestoreAmbient();
                }

                return $"SUCCESS|FULLBRIGHT|{_fullBrightEnabled}";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"ToggleFullBright error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        public static string TeleportPlayerToCamera(string args, string[] parts)
        {
            try
            {
                if (_mainCamera == null) _mainCamera = GetMainCamera();
                if (_mainCamera == null) return "ERROR|No Camera";

                // Try BehaviorAnalyzer first, then fallback to legacy search
                var player = BehaviorAnalyzer.GetRoleObject("Player") ?? FindPlayer();
                
                if (player == null) return "ERROR|Player not found";

                var camTrans = GetTransform(_mainCamera);
                var playerTrans = GetTransform(player); // Might be GameObject or Component

                if (camTrans == null || playerTrans == null) return "ERROR|Transforms not found";

                var posProp = ReflectionHelper.GetProperty(camTrans.GetType(), "position");
                var rotProp = ReflectionHelper.GetProperty(camTrans.GetType(), "rotation");
                
                if (posProp == null) return "ERROR|No position property";

                var camPos = posProp.GetValue(camTrans, null);
                var camRot = rotProp?.GetValue(camTrans, null);

                posProp.SetValue(playerTrans, camPos, null);
                // Optional: Set rotation too
                // if (camRot != null && rotProp != null) rotProp.SetValue(playerTrans, camRot, null);

                // FIX: If FreeCam is active and camera was world-space (no parent),
                // update the stored return position to the current location.
                // Otherwise, restoring will snap camera back to where it started.
                if (_freeCamEnabled && _originalParent == null)
                {
                    _originalLocalPos = camPos;
                    if (camRot != null) _originalLocalRot = camRot;
                }

                return "SUCCESS|TELEPORTED";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"TeleportPlayerToCamera error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        private static void UpdateFreeCam()
        {
            if (_mainCamera == null) _mainCamera = GetMainCamera();
            if (_mainCamera == null) return;

            var trans = GetTransform(_mainCamera);
            if (trans == null) return;

            // Store initial state - Handled by ToggleFreeCam now
            /*
            if (!_hasStoredState)
            {
                var posProp = ReflectionHelper.GetProperty(trans.GetType(), "position");
                var rotProp = ReflectionHelper.GetProperty(trans.GetType(), "rotation");
                if (posProp != null) _storedPosition = posProp.GetValue(trans, null);
                if (rotProp != null) _storedRotation = rotProp.GetValue(trans, null);
                _hasStoredState = true;
            }
            */

            // Input
            EnsureInputCache();
            if (_inputType == null) return;

            float dt = GetDeltaTime();
            float speed = _flySpeed * dt;
            
            if (GetKey("left shift") || GetKey("right shift")) speed *= _fastMult;

            // Simple movement logic using Translate/Rotate if available, or math
            // Using Translate is easiest for local movement
            var translate = ReflectionHelper.GetMethod(trans.GetType(), "Translate", new[] { typeof(float), typeof(float), typeof(float) });
            var rotate = ReflectionHelper.GetMethod(trans.GetType(), "Rotate", new[] { typeof(float), typeof(float), typeof(float) });

            if (translate != null)
            {
                float x = 0, y = 0, z = 0;
                if (GetKey("w")) z += speed;
                if (GetKey("s")) z -= speed;
                if (GetKey("a")) x -= speed;
                if (GetKey("d")) x += speed;
                if (GetKey("e")) y += speed;
                if (GetKey("q")) y -= speed;

                if (x != 0 || y != 0 || z != 0)
                    translate.Invoke(trans, new object[] { x, y, z });
            }

            // Mouse Rotation
            // We need to use Input.GetAxis("Mouse X")
            if (GetMouseButton(1)) // Right click to look
            {
                float mouseX = GetAxis("Mouse X") * _lookSpeed;
                float mouseY = GetAxis("Mouse Y") * _lookSpeed;

                if (rotate != null)
                {
                    // Rotate(0, mouseX, 0, Space.World) - tricky with reflection to specify Space
                    // Simple Rotate(x, y, z) is usually local. 
                    // To do proper camera look:
                    // transform.Rotate(Vector3.up, mouseX, Space.World);
                    // transform.Rotate(Vector3.right, -mouseY, Space.Self);
                    
                    // Simplified:
                    // Yaw (World Up) is hard without Vector3.up.
                    // Local Pitch is easy.
                    
                    // Let's try to access eulerAngles
                    var eulerProp = ReflectionHelper.GetProperty(trans.GetType(), "eulerAngles");
                    if (eulerProp != null)
                    {
                        var euler = eulerProp.GetValue(trans, null);
                        var vecType = euler.GetType();
                        var xField = (MemberInfo)ReflectionHelper.FindField(vecType, "x") ?? (MemberInfo)ReflectionHelper.GetProperty(vecType, "x");
                        var yField = (MemberInfo)ReflectionHelper.FindField(vecType, "y") ?? (MemberInfo)ReflectionHelper.GetProperty(vecType, "y");
                        var zField = (MemberInfo)ReflectionHelper.FindField(vecType, "z") ?? (MemberInfo)ReflectionHelper.GetProperty(vecType, "z"); // Z is usually 0 for camera

                        float currentX = Convert.ToSingle(GetValue(xField, euler));
                        float currentY = Convert.ToSingle(GetValue(yField, euler));

                        // Pitch (X) - inverted usually
                        float newX = currentX - mouseY;
                        float newY = currentY + mouseX;

                        // Create new Vector3
                        var newEuler = Activator.CreateInstance(vecType);
                        SetValue(xField, newEuler, newX);
                        SetValue(yField, newEuler, newY);
                        SetValue(zField, newEuler, 0f);

                        eulerProp.SetValue(trans, newEuler, null);
                    }
                }
            }
        }

        private static void ApplyFullBright()
        {
            _lightUpdateFrame++;
            
            // 0. Update Camera Ref
            if (_mainCamera == null) _mainCamera = GetMainCamera();

            var renderSettings = ReflectionHelper.FindType("UnityEngine.RenderSettings");
            var colorType = ReflectionHelper.FindType("UnityEngine.Color");
            
            if (renderSettings != null && colorType != null)
            {
                // 1. Ambient Light Color -> White
                var ambientLightProp = ReflectionHelper.GetProperty(renderSettings, "ambientLight");
                if (ambientLightProp != null)
                {
                    var white = Activator.CreateInstance(colorType, new object[] { 1f, 1f, 1f, 1f });
                    ambientLightProp.SetValue(null, white, null);
                }

                // 2. Ambient Intensity -> 1.0
                var ambientIntProp = ReflectionHelper.GetProperty(renderSettings, "ambientIntensity");
                if (ambientIntProp != null)
                {
                    ambientIntProp.SetValue(null, 1.0f, null);
                }

                // 3. Disable Fog
                var fogProp = ReflectionHelper.GetProperty(renderSettings, "fog");
                if (fogProp != null)
                {
                    fogProp.SetValue(null, false, null);
                }
            }

            // 4. Manage Phantom Headlamp
            UpdatePhantomLight();

            // 5. Boost Scene Lights (Periodic)
            if (_lightUpdateFrame % 60 == 0)
            {
                BoostSceneLights();
            }
        }

        private static void UpdatePhantomLight()
        {
            if (_fullbrightLight == null)
            {
                var goType = ReflectionHelper.FindType("UnityEngine.GameObject");
                var lightType = ReflectionHelper.FindType("UnityEngine.Light");
                
                if (goType != null && lightType != null)
                {
                    // Create GameObject
                    _fullbrightLight = Activator.CreateInstance(goType, new object[] { "PhantomFullbright" });
                    var unityObjType = ReflectionHelper.FindType("UnityEngine.Object");
                    if (unityObjType != null)
                    {
                        var dontDestroy = ReflectionHelper.GetMethod(unityObjType, "DontDestroyOnLoad", new[] { unityObjType });
                        dontDestroy?.Invoke(null, new object[] { _fullbrightLight });
                    }

                    // Add Light Component
                    var addComp = ReflectionHelper.GetMethod(goType, "AddComponent", new[] { typeof(Type) });
                    addComp?.Invoke(_fullbrightLight, new object[] { lightType });
                }
            }

            if (_fullbrightLight != null)
            {
                var lightType = ReflectionHelper.FindType("UnityEngine.Light");
                var goType = ReflectionHelper.FindType("UnityEngine.GameObject");
                
                // Get Light Component
                var getComp = ReflectionHelper.GetMethod(goType, "GetComponent", new[] { typeof(Type) });
                var lightComp = getComp?.Invoke(_fullbrightLight, new object[] { lightType });

                if (lightComp != null)
                {
                    // Ensure Directional (1)
                    var typeProp = ReflectionHelper.GetProperty(lightType, "type");
                    try { typeProp?.SetValue(lightComp, 1, null); } catch { }

                    // High Intensity
                    var intProp = ReflectionHelper.GetProperty(lightType, "intensity");
                    intProp?.SetValue(lightComp, 2.0f, null);

                    // High Range (irrelevant for Directional, but good for Spot/Point)
                    var rangeProp = ReflectionHelper.GetProperty(lightType, "range");
                    rangeProp?.SetValue(lightComp, 10000f, null);
                    
                    // White Color
                    var colorProp = ReflectionHelper.GetProperty(lightType, "color");
                    if (colorProp != null)
                    {
                         var cType = ReflectionHelper.FindType("UnityEngine.Color");
                         if (cType != null)
                            colorProp.SetValue(lightComp, Activator.CreateInstance(cType, 1f, 1f, 1f, 1f), null);
                    }
                }

                // Parent to Camera for "Headlamp" effect
                if (_mainCamera != null)
                {
                    var lightTrans = GetTransform(_fullbrightLight);
                    var camTrans = GetTransform(_mainCamera);
                    if (lightTrans != null && camTrans != null)
                    {
                        var parentProp = ReflectionHelper.GetProperty(lightTrans.GetType(), "parent");
                        var currentParent = parentProp?.GetValue(lightTrans, null);
                        
                        if (currentParent != camTrans)
                        {
                            parentProp?.SetValue(lightTrans, camTrans, null);
                            
                            // Zero local position/rotation
                            var localPosProp = ReflectionHelper.GetProperty(lightTrans.GetType(), "localPosition");
                            var localRotProp = ReflectionHelper.GetProperty(lightTrans.GetType(), "localRotation");
                            
                            var vec3Type = ReflectionHelper.FindType("UnityEngine.Vector3");
                            var quatType = ReflectionHelper.FindType("UnityEngine.Quaternion");
                            
                            if (vec3Type != null)
                                localPosProp?.SetValue(lightTrans, Activator.CreateInstance(vec3Type), null);
                                
                            if (quatType != null)
                            {
                                var identity = ReflectionHelper.GetProperty(quatType, "identity")?.GetValue(null, null);
                                if (identity != null) localRotProp?.SetValue(lightTrans, identity, null);
                            }
                        }
                    }
                }
            }
        }

        private static void BoostSceneLights()
        {
            var lightType = ReflectionHelper.FindType("UnityEngine.Light");
            var unityObjType = ReflectionHelper.FindType("UnityEngine.Object");
            if (lightType == null || unityObjType == null) return;

            var findObjs = ReflectionHelper.GetMethod(unityObjType, "FindObjectsOfType", new[] { typeof(Type) });
            if (findObjs == null) return;

            try
            {
                var lights = findObjs.Invoke(null, new object[] { lightType }) as Array;
                if (lights == null) return;

                foreach (var light in lights)
                {
                    if (light == null) continue;
                    
                    // Boost Range
                    var rangeProp = ReflectionHelper.GetProperty(lightType, "range");
                    if (rangeProp != null)
                    {
                        float r = (float)rangeProp.GetValue(light, null);
                        if (r < 50f) rangeProp.SetValue(light, 50f, null);
                    }
                    
                    // Boost Intensity
                    var intProp = ReflectionHelper.GetProperty(lightType, "intensity");
                    if (intProp != null)
                    {
                        float i = (float)intProp.GetValue(light, null);
                        if (i < 1.0f) intProp.SetValue(light, 1.0f, null);
                    }

                    // Disable Shadows
                    var shadowsProp = ReflectionHelper.GetProperty(lightType, "shadows");
                    if (shadowsProp != null)
                    {
                        shadowsProp.SetValue(light, 0, null); // LightShadows.None
                    }
                }
            }
            catch { }
        }

        private static void StoreAmbient()
        {
            if (_ambientStored) return;
            var renderSettings = ReflectionHelper.FindType("UnityEngine.RenderSettings");
            if (renderSettings == null) return;

            var ambientLightProp = ReflectionHelper.GetProperty(renderSettings, "ambientLight");
            var ambientIntProp = ReflectionHelper.GetProperty(renderSettings, "ambientIntensity");
            var fogProp = ReflectionHelper.GetProperty(renderSettings, "fog");

            if (ambientLightProp != null) _originalAmbientLight = ambientLightProp.GetValue(null, null);
            if (ambientIntProp != null) _originalAmbientIntensity = (float)ambientIntProp.GetValue(null, null);
            if (fogProp != null) _originalFog = (bool)fogProp.GetValue(null, null);
            
            _ambientStored = true;
        }

        private static void RestoreAmbient()
        {
            if (!_ambientStored) return;
            var renderSettings = ReflectionHelper.FindType("UnityEngine.RenderSettings");
            if (renderSettings == null) return;

            var ambientLightProp = ReflectionHelper.GetProperty(renderSettings, "ambientLight");
            var ambientIntProp = ReflectionHelper.GetProperty(renderSettings, "ambientIntensity");
            var fogProp = ReflectionHelper.GetProperty(renderSettings, "fog");

            if (ambientLightProp != null && _originalAmbientLight != null)
                ambientLightProp.SetValue(null, _originalAmbientLight, null);
                
            if (ambientIntProp != null)
                ambientIntProp.SetValue(null, _originalAmbientIntensity, null);

            if (fogProp != null)
                fogProp.SetValue(null, _originalFog, null);

            // Destroy Light
            if (_fullbrightLight != null)
            {
                var unityObjType = ReflectionHelper.FindType("UnityEngine.Object");
                if (unityObjType != null)
                {
                    var destroy = ReflectionHelper.GetMethod(unityObjType, "Destroy", new[] { unityObjType });
                    destroy?.Invoke(null, new object[] { _fullbrightLight });
                }
                _fullbrightLight = null;
            }

            _ambientStored = false;
        }

        // Helpers

        public static object GetMainCamera()
        {
            var camType = ReflectionHelper.FindType("UnityEngine.Camera");
            if (camType == null) return null;
            
            // 1. Camera.main
            var mainProp = ReflectionHelper.GetProperty(camType, "main");
            var mainCam = mainProp?.GetValue(null, null);
            if (mainCam != null) return mainCam;

            // 2. Find by Tag "MainCamera"
            var goType = ReflectionHelper.FindType("UnityEngine.GameObject");
            if (goType != null)
            {
                var findTag = ReflectionHelper.GetMethod(goType, "FindGameObjectWithTag", new[] { typeof(string) });
                if (findTag != null)
                {
                    try 
                    { 
                        var go = findTag.Invoke(null, new object[] { "MainCamera" });
                        if (go != null)
                        {
                            var getComp = ReflectionHelper.GetMethod(go.GetType(), "GetComponent", new[] { typeof(Type) });
                            return getComp?.Invoke(go, new object[] { camType });
                        }
                    } 
                    catch { }
                }
            }

            // 3. Find any Camera and pick the "best" one (enabled, highest depth)
            var unityObjType = ReflectionHelper.FindType("UnityEngine.Object");
            var findObjs = ReflectionHelper.GetMethod(unityObjType, "FindObjectsOfType", new[] { typeof(Type) });
            if (findObjs != null)
            {
                try
                {
                    var cameras = findObjs.Invoke(null, new object[] { camType }) as Array;
                    if (cameras != null && cameras.Length > 0)
                    {
                        object bestCam = null;
                        float maxDepth = -9999f;

                        foreach (var cam in cameras)
                        {
                            if (cam == null) continue;
                            
                            // Check enabled
                            var enabledProp = ReflectionHelper.GetProperty(camType, "enabled");
                            if (enabledProp != null && !(bool)enabledProp.GetValue(cam, null)) continue;

                            // Check depth
                            var depthProp = ReflectionHelper.GetProperty(camType, "depth");
                            if (depthProp != null)
                            {
                                float depth = (float)depthProp.GetValue(cam, null);
                                if (depth > maxDepth)
                                {
                                    maxDepth = depth;
                                    bestCam = cam;
                                }
                            }
                            else
                            {
                                return cam; // No depth? Just take first.
                            }
                        }
                        return bestCam;
                    }
                }
                catch { }
            }

            return null;
        }

        public static object GetTransform(object obj)
        {
            if (obj == null) return null;
            var type = obj.GetType();
            if (type.Name == "Transform" || type.Name.Contains("Transform")) return obj; // Already transform?
            
            var transProp = ReflectionHelper.GetProperty(type, "transform");
            return transProp?.GetValue(obj, null);
        }

        private static object FindPlayer()
        {
            return CheatManager.FindPlayerObject();
        }

        private static void EnsureInputCache()
        {
            if (_inputType != null) return;
            _inputType = ReflectionHelper.FindType("UnityEngine.Input");
            if (_inputType != null)
            {
                _getKeyString = ReflectionHelper.GetMethod(_inputType, "GetKey", new[] { typeof(string) });
                _getAxis = ReflectionHelper.GetMethod(_inputType, "GetAxis", new[] { typeof(string) });
                var mouseButton = ReflectionHelper.GetMethod(_inputType, "GetMouseButton", new[] { typeof(int) });
            }
            
            var timeType = ReflectionHelper.FindType("UnityEngine.Time");
            if (timeType != null)
                _deltaTime = ReflectionHelper.GetProperty(timeType, "deltaTime");
        }

        private static bool GetKey(string key)
        {
            if (_getKeyString == null) return false;
            try { return (bool)_getKeyString.Invoke(null, new object[] { key }); } catch { return false; }
        }

        private static float GetAxis(string axis)
        {
            if (_getAxis == null) return 0f;
            try { return (float)_getAxis.Invoke(null, new object[] { axis }); } catch { return 0f; }
        }

        private static bool GetMouseButton(int button)
        {
            if (_inputType == null) return false;
            var mb = ReflectionHelper.GetMethod(_inputType, "GetMouseButton", new[] { typeof(int) });
            if (mb == null) return false;
            try { return (bool)mb.Invoke(null, new object[] { button }); } catch { return false; }
        }

        private static float GetDeltaTime()
        {
            if (_deltaTime == null) return 0.016f;
            try { return (float)_deltaTime.GetValue(null, null); } catch { return 0.016f; }
        }
        
        private static object GetValue(MemberInfo member, object target)
        {
            if (member is FieldInfo f) return f.GetValue(target);
            if (member is PropertyInfo p) return p.GetValue(target, null);
            return null;
        }
        
        private static void SetValue(MemberInfo member, object target, object value)
        {
            if (member is FieldInfo f) f.SetValue(target, value);
            if (member is PropertyInfo p) p.SetValue(target, value, null);
        }
    }
}
