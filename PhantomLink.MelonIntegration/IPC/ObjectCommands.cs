using System;
using System.Reflection;
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PhantomLink.MelonIntegration.Core;

namespace PhantomLink.MelonIntegration.IPC
{
    public static class ObjectCommands
    {
        public static void Register()
        {
            IpcRouter.RegisterHandler("GET_FIELD_INSTANCE", GetFieldInstance);
            IpcRouter.RegisterHandler("SET_FIELD_INSTANCE", SetFieldInstance);
            IpcRouter.RegisterHandler("GET_PROPERTY_INSTANCE", GetPropertyInstance);
            IpcRouter.RegisterHandler("SET_PROPERTY_INSTANCE", SetPropertyInstance);
            IpcRouter.RegisterHandler("INVOKE_INSTANCE", InvokeInstance);
            IpcRouter.RegisterHandler("PIN_OBJECT", PinObject);
            IpcRouter.RegisterHandler("UNPIN_OBJECT", UnpinObject);
            IpcRouter.RegisterHandler("SET_TRANSFORM_LOCAL", SetTransformLocal);
            IpcRouter.RegisterHandler("SET_TRANSFORM_WORLD", SetTransformWorld);
            IpcRouter.RegisterHandler("SET_ACTIVE", SetObjectActive);
            IpcRouter.RegisterHandler("SET_NAME", SetObjectName);
            IpcRouter.RegisterHandler("SET_TAG", SetObjectTag);
            IpcRouter.RegisterHandler("SET_LAYER", SetObjectLayer);
            IpcRouter.RegisterHandler("SET_ENABLED", SetEnabled);
            IpcRouter.RegisterHandler("DESTROY", DestroyObject);
            IpcRouter.RegisterHandler("DUPLICATE", DuplicateObject);
            IpcRouter.RegisterHandler("ADD_COMPONENT", AddComponentToObject);
            IpcRouter.RegisterHandler("SET_PARENT", SetParent);
            IpcRouter.RegisterHandler("SET_SIBLING_INDEX", SetSiblingIndex);
            IpcRouter.RegisterHandler("CREATE_GAMEOBJECT", CreateGameObject);
        }

        private static string SetTransformLocal(string args, string[] parts)
        {
            try
            {
                // SET_TRANSFORM_LOCAL|id|pos|rot|scale
                if (parts.Length < 5) return "ERROR|Invalid format";
                var obj = ObjectManager.GetObject(parts[1]);
                if (obj == null) return "ERROR|Object not found";

                ReflectionHelper.TryGetTransform(obj, out var tr);
                if (tr == null) return "ERROR|Transform not found";

                var t = tr.GetType();
                var vec3 = ReflectionHelper.FindType("UnityEngine.Vector3");

                if (parts[2] != "null" && parts[2] != "") 
                    ReflectionHelper.GetProperty(t, "localPosition")?.SetValue(tr, ReflectionHelper.ParseValue(parts[2], vec3), null);
                if (parts[3] != "null" && parts[3] != "") 
                    ReflectionHelper.GetProperty(t, "localEulerAngles")?.SetValue(tr, ReflectionHelper.ParseValue(parts[3], vec3), null);
                if (parts[4] != "null" && parts[4] != "") 
                    ReflectionHelper.GetProperty(t, "localScale")?.SetValue(tr, ReflectionHelper.ParseValue(parts[4], vec3), null);

                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"SetTransformLocal error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetTransformWorld(string args, string[] parts)
        {
            try
            {
                if (parts.Length < 5) return "ERROR|Invalid format";
                var obj = ObjectManager.GetObject(parts[1]);
                if (obj == null) return "ERROR|Object not found";

                ReflectionHelper.TryGetTransform(obj, out var tr);
                if (tr == null) return "ERROR|Transform not found";

                var t = tr.GetType();
                var vec3 = ReflectionHelper.FindType("UnityEngine.Vector3");

                if (parts[2] != "null" && parts[2] != "") 
                    ReflectionHelper.GetProperty(t, "position")?.SetValue(tr, ReflectionHelper.ParseValue(parts[2], vec3), null);
                if (parts[3] != "null" && parts[3] != "") 
                    ReflectionHelper.GetProperty(t, "eulerAngles")?.SetValue(tr, ReflectionHelper.ParseValue(parts[3], vec3), null);
                
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"SetTransformWorld error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetObjectActive(string args, string[] parts)
        {
            try
            {
                if (parts.Length < 3) return "ERROR|Invalid format";
                var obj = ObjectManager.GetObject(parts[1]);
                if (obj == null) return "ERROR|Object not found";

                var go = ReflectionHelper.TryGetTransformGameObject(obj) ?? obj;
                var setActive = ReflectionHelper.GetMethod(go.GetType(), "SetActive", new[] { typeof(bool) });
                if (setActive == null) return "ERROR|SetActive not found";

                bool active = parts[2] == "1" || parts[2].Equals("true", StringComparison.OrdinalIgnoreCase);
                setActive.Invoke(go, new object[] { active });
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"SetObjectActive error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetObjectName(string args, string[] parts)
        {
            try
            {
                if (parts.Length < 3) return "ERROR|Invalid format";
                var obj = ObjectManager.GetObject(parts[1]);
                if (obj == null) return "ERROR|Object not found";

                var go = ReflectionHelper.TryGetTransformGameObject(obj) ?? obj;
                var prop = ReflectionHelper.GetProperty(go.GetType(), "name");
                if (prop != null) prop.SetValue(go, parts[2], null);
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"SetObjectName error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetObjectTag(string args, string[] parts)
        {
            try
            {
                if (parts.Length < 3) return "ERROR|Invalid format";
                var obj = ObjectManager.GetObject(parts[1]);
                if (obj == null) return "ERROR|Object not found";
                var go = ReflectionHelper.TryGetTransformGameObject(obj) ?? obj;
                var prop = ReflectionHelper.GetProperty(go.GetType(), "tag");
                if (prop != null) prop.SetValue(go, parts[2], null);
                return "SUCCESS|OK";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"SetObjectTag error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetObjectLayer(string args, string[] parts)
        {
            try
            {
                if (parts.Length < 3) return "ERROR|Invalid format";
                var obj = ObjectManager.GetObject(parts[1]);
                if (obj == null) return "ERROR|Object not found";
                var go = ReflectionHelper.TryGetTransformGameObject(obj) ?? obj;
                if (int.TryParse(parts[2], out int layer))
                {
                    var prop = ReflectionHelper.GetProperty(go.GetType(), "layer");
                    if (prop != null) prop.SetValue(go, layer, null);
                    return "SUCCESS|OK";
                }
                return "ERROR|Invalid layer";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"SetObjectLayer error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetEnabled(string args, string[] parts)
        {
            try
            {
                if (parts.Length < 3) return "ERROR|Invalid format";
                var obj = ObjectManager.GetObject(parts[1]);
                if (obj == null) return "ERROR|Object not found";
                
                var prop = ReflectionHelper.GetProperty(obj.GetType(), "enabled");
                if (prop != null)
                {
                    bool enabled = parts[2] == "1" || parts[2].Equals("true", StringComparison.OrdinalIgnoreCase);
                    prop.SetValue(obj, enabled, null);
                    return "SUCCESS|OK";
                }
                return "ERROR|Enabled property not found";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"SetEnabled error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DestroyObject(string args, string[] parts)
        {
            try
            {
                if (parts.Length < 2) return "ERROR|Invalid format";
                var obj = ObjectManager.GetObject(parts[1]);
                if (obj == null) return "ERROR|Object not found";

                var unityObjType = ReflectionHelper.FindType("UnityEngine.Object");
                var destroy = ReflectionHelper.GetMethod(unityObjType, "Destroy", new[] { unityObjType });
                
                if (destroy != null)
                {
                    destroy.Invoke(null, new object[] { obj });
                    return "SUCCESS|OK";
                }
                return "ERROR|Destroy method not found";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"DestroyObject error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        private static string DuplicateObject(string args, string[] parts)
        {
            try
            {
                if (parts.Length < 2) return "ERROR|Invalid format";
                var obj = ObjectManager.GetObject(parts[1]);
                if (obj == null) return "ERROR|Object not found";

                var go = ReflectionHelper.TryGetTransformGameObject(obj) ?? obj;
                var unityObjType = ReflectionHelper.FindType("UnityEngine.Object");
                var instantiate = ReflectionHelper.GetMethod(unityObjType, "Instantiate", new[] { unityObjType });

                if (instantiate != null)
                {
                    var clone = instantiate.Invoke(null, new object[] { go });
                    if (clone != null)
                    {
                        int id = ObjectManager.TrackObject(clone);
                        return $"SUCCESS|id={id}";
                    }
                }
                return "ERROR|Instantiate failed";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"DuplicateObject error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        private static string AddComponentToObject(string args, string[] parts)
        {
            try
            {
                if (parts.Length < 3) return "ERROR|Invalid format";
                var obj = ObjectManager.GetObject(parts[1]);
                if (obj == null) return "ERROR|Object not found";
                
                var go = ReflectionHelper.TryGetTransformGameObject(obj) ?? obj;
                var type = ReflectionHelper.FindType(parts[2]);
                if (type == null) return $"ERROR|Type not found: {parts[2]}";

                var addComp = ReflectionHelper.GetMethod(go.GetType(), "AddComponent", new[] { typeof(Type) });
                if (addComp != null)
                {
                    var comp = addComp.Invoke(go, new object[] { type });
                    if (comp != null)
                    {
                        int id = ObjectManager.TrackObject(comp);
                        return $"SUCCESS|id={id}";
                    }
                }
                return "ERROR|AddComponent failed";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"AddComponentToObject error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetParent(string args, string[] parts)
        {
            try
            {
                if (parts.Length < 3) return "ERROR|Invalid format";
                var child = ObjectManager.GetObject(parts[1]);
                if (child == null) return "ERROR|Child not found";
                
                object parent = null;
                if (parts[2] != "null" && parts[2] != "0")
                {
                    parent = ObjectManager.GetObject(parts[2]);
                    if (parent == null) return "ERROR|Parent not found";
                }

                ReflectionHelper.TryGetTransform(child, out var childTr);
                object parentTr = null;
                if (parent != null) ReflectionHelper.TryGetTransform(parent, out parentTr);

                if (childTr != null)
                {
                    var transformType = ReflectionHelper.FindType("UnityEngine.Transform");
                    var setParent = ReflectionHelper.GetMethod(childTr.GetType(), "SetParent", new[] { transformType, typeof(bool) });
                    
                    if (setParent == null) 
                        setParent = ReflectionHelper.GetMethod(childTr.GetType(), "SetParent", new[] { childTr.GetType(), typeof(bool) });

                    if (setParent == null)
                    {
                        var parentProp = ReflectionHelper.GetProperty(childTr.GetType(), "parent");
                        if (parentProp != null)
                        {
                            parentProp.SetValue(childTr, parentTr, null);
                            return "SUCCESS|OK";
                        }
                    }
                    else
                    {
                        setParent.Invoke(childTr, new object[] { parentTr, true });
                        return "SUCCESS|OK";
                    }
                }
                return "ERROR|SetParent failed";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"SetParent error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        private static string SetSiblingIndex(string args, string[] parts)
        {
            try
            {
                if (parts.Length < 3) return "ERROR|Invalid format";
                var obj = ObjectManager.GetObject(parts[1]);
                if (obj == null) return "ERROR|Object not found";

                ReflectionHelper.TryGetTransform(obj, out var tr);
                if (tr != null && int.TryParse(parts[2], out int index))
                {
                    var method = ReflectionHelper.GetMethod(tr.GetType(), "SetSiblingIndex", new[] { typeof(int) });
                    method?.Invoke(tr, new object[] { index });
                    return "SUCCESS|OK";
                }
                return "ERROR|Failed";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"SetSiblingIndex error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        private static string CreateGameObject(string args, string[] parts)
        {
            try
            {
                var name = parts.Length > 1 ? parts[1] : "New GameObject";
                var parentId = parts.Length > 2 ? parts[2] : null;

                var goType = ReflectionHelper.FindType("UnityEngine.GameObject");
                if (goType == null) return "ERROR|GameObject type missing";

                object go = Activator.CreateInstance(goType, new object[] { name }); 
                if (go != null)
                {
                    if (!string.IsNullOrEmpty(parentId))
                    {
                        var parent = ObjectManager.GetObject(parentId);
                        if (parent != null)
                        {
                            ReflectionHelper.TryGetTransform(go, out var childTr);
                            ReflectionHelper.TryGetTransform(parent, out var parentTr);
                            if (childTr != null && parentTr != null)
                            {
                                 var setParent = ReflectionHelper.GetMethod(childTr.GetType(), "SetParent", new[] { parentTr.GetType(), typeof(bool) });
                                 setParent?.Invoke(childTr, new object[] { parentTr, false });
                            }
                        }
                    }
                    
                    int id = ObjectManager.TrackObject(go);
                    return $"SUCCESS|id={id}";
                }
                return "ERROR|Creation failed";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"CreateGameObject error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        private static string GetFieldInstance(string args, string[] parts)
        {
            if (parts.Length < 4) return "ERROR|Invalid format";
            
            if (!int.TryParse(parts[1], out int objId)) return "ERROR|Invalid object ID";
            string typeName = parts[2];
            string fieldName = parts[3];

            object target = ObjectManager.Get(objId);
            if (target == null) return "ERROR|Object not found or collected";

            // If typeName is "*", use the object's type
            Type type = (typeName == "*") ? target.GetType() : ReflectionHelper.FindType(typeName);
            if (type == null) return $"ERROR|Type not found: {typeName}";

            var field = ReflectionHelper.FindField(type, fieldName);
            if (field == null) return $"ERROR|Field not found: {fieldName}";

            try
            {
                var value = field.GetValue(target);
                // If value is a complex object, track it?
                // For now, just format it. 
                // In advanced mode, we might want to return "REF|123456" for objects.
                
                return $"SUCCESS|{ReflectionHelper.FormatValue(value)}";
            }
            catch (Exception ex)
            {
                return $"ERROR|Get field failed: {ex.Message}";
            }
        }

        private static string SetFieldInstance(string args, string[] parts)
        {
            if (parts.Length < 5) return "ERROR|Invalid format";
            
            if (!int.TryParse(parts[1], out int objId)) return "ERROR|Invalid object ID";
            string typeName = parts[2];
            string fieldName = parts[3];
            string valueStr = parts[4];

            object target = ObjectManager.Get(objId);
            if (target == null) return "ERROR|Object not found or collected";

            Type type = (typeName == "*") ? target.GetType() : ReflectionHelper.FindType(typeName);
            if (type == null) return $"ERROR|Type not found: {typeName}";

            var field = ReflectionHelper.FindField(type, fieldName);
            if (field == null) return $"ERROR|Field not found: {fieldName}";

            try
            {
                object val = ReflectionHelper.ParseValue(valueStr, field.FieldType);
                field.SetValue(target, val);
                return "SUCCESS|Value set";
            }
            catch (Exception ex)
            {
                return $"ERROR|Set field failed: {ex.Message}";
            }
        }

        private static string GetPropertyInstance(string args, string[] parts)
        {
            if (parts.Length < 4) return "ERROR|Invalid format";
            
            if (!int.TryParse(parts[1], out int objId)) return "ERROR|Invalid object ID";
            string typeName = parts[2];
            string propName = parts[3];

            object target = ObjectManager.Get(objId);
            if (target == null) return "ERROR|Object not found or collected";

            Type type = (typeName == "*") ? target.GetType() : ReflectionHelper.FindType(typeName);
            if (type == null) return $"ERROR|Type not found: {typeName}";

            var prop = ReflectionHelper.FindProperty(type, propName);
            if (prop == null) return $"ERROR|Property not found: {propName}";

            try
            {
                var value = prop.GetValue(target, null);
                return $"SUCCESS|{ReflectionHelper.FormatValue(value)}";
            }
            catch (Exception ex)
            {
                return $"ERROR|Get property failed: {ex.Message}";
            }
        }

        private static string SetPropertyInstance(string args, string[] parts)
        {
            if (parts.Length < 5) return "ERROR|Invalid format";
            
            if (!int.TryParse(parts[1], out int objId)) return "ERROR|Invalid object ID";
            string typeName = parts[2];
            string propName = parts[3];
            string valueStr = parts[4];

            object target = ObjectManager.Get(objId);
            if (target == null) return "ERROR|Object not found or collected";

            Type type = (typeName == "*") ? target.GetType() : ReflectionHelper.FindType(typeName);
            if (type == null) return $"ERROR|Type not found: {typeName}";

            var prop = ReflectionHelper.FindProperty(type, propName);
            if (prop == null) return $"ERROR|Property not found: {propName}";

            if (!prop.CanWrite) return "ERROR|Property is read-only";

            try
            {
                object val = ReflectionHelper.ParseValue(valueStr, prop.PropertyType);
                prop.SetValue(target, val, null);
                return "SUCCESS|Value set";
            }
            catch (Exception ex)
            {
                return $"ERROR|Set property failed: {ex.Message}";
            }
        }

        private static string InvokeInstance(string args, string[] parts)
        {
            try
            {
                if (parts.Length < 5) return "ERROR|Invalid format";
                
                if (!int.TryParse(parts[1], out int objId)) return "ERROR|Invalid object ID";
                string typeName = parts[2];
                string methodName = parts[3];
                string paramString = parts[4];

                object target = ObjectManager.Get(objId);
                if (target == null) return "ERROR|Object not found or collected";

                Type type = (typeName == "*") ? target.GetType() : ReflectionHelper.FindType(typeName);
                if (type == null) return $"ERROR|Type not found: {typeName}";

                var method = ReflectionHelper.FindMethod(type, methodName);
                if (method == null) return $"ERROR|Method not found: {methodName}";

                object[] parameters = null;
                if (!string.IsNullOrEmpty(paramString) && paramString != "null")
                {
                    var methodParams = method.GetParameters();
                    
                    if (paramString.Trim().StartsWith("["))
                    {
                        try 
                        {
                            var jArray = JArray.Parse(paramString);
                            if (jArray.Count != methodParams.Length)
                                return $"ERROR|Parameter count mismatch";
                                
                            parameters = new object[methodParams.Length];
                            for (int i = 0; i < methodParams.Length; i++)
                            {
                                parameters[i] = jArray[i].ToObject(methodParams[i].ParameterType);
                            }
                        }
                        catch (Exception ex)
                        {
                            return $"ERROR|JSON parsing failed: {ex.Message}";
                        }
                    }
                    else
                    {
                        var paramParts = paramString.Split(',');
                        
                        if (paramParts.Length != methodParams.Length)
                             return $"ERROR|Parameter count mismatch";

                        parameters = new object[methodParams.Length];
                        for (int i = 0; i < methodParams.Length; i++)
                        {
                            parameters[i] = ReflectionHelper.ParseValue(paramParts[i], methodParams[i].ParameterType);
                        }
                    }
                }

                var result = method.Invoke(target, parameters);
                return $"SUCCESS|{ReflectionHelper.FormatValue(result)}";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"InvokeInstance error: {ex}");
                return $"ERROR|Invoke failed: {ex.InnerException?.Message ?? ex.Message}";
            }
        }

        private static string PinObject(string args, string[] parts)
        {
            try
            {
                if (parts.Length < 2) return "ERROR|Invalid format";
                if (!int.TryParse(parts[1], out int id)) return "ERROR|Invalid ID";
                
                ObjectManager.Pin(id);
                return "SUCCESS|Object pinned";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"PinObject error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }

        private static string UnpinObject(string args, string[] parts)
        {
            try
            {
                if (parts.Length < 2) return "ERROR|Invalid format";
                if (!int.TryParse(parts[1], out int id)) return "ERROR|Invalid ID";
                
                ObjectManager.Unpin(id);
                return "SUCCESS|Object unpinned";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"UnpinObject error: {ex}");
                return $"ERROR|{ex.Message}";
            }
        }
    }
}
