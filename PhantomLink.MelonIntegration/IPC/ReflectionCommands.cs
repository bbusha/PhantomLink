using System;
using System.Linq;
using System.Reflection;
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using PhantomLink.MelonIntegration.Core;

namespace PhantomLink.MelonIntegration.IPC
{
    public static class ReflectionCommands
    {
        public static void Register()
        {
            IpcRouter.RegisterHandler("GET_ASSEMBLIES", GetAssemblies);
            IpcRouter.RegisterHandler("GET_TYPES", GetTypes);
            IpcRouter.RegisterHandler("INVOKE", InvokeStatic);
            IpcRouter.RegisterHandler("GET_FIELD", GetFieldStatic);
            IpcRouter.RegisterHandler("SET_FIELD", SetFieldStatic);
        }

        private static string GetAssemblies(string args, string[] parts)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            return $"ASSEMBLIES|{assemblies.Length}";
        }

        private static string GetTypes(string args, string[] parts)
        {
            string assemblyName = parts.Length > 1 ? parts[1] : null;
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

        private static string InvokeStatic(string args, string[] parts)
        {
            try
            {
                if (parts.Length < 4) return "ERROR|Invalid invoke format";
                string typeName = parts[1];
                string methodName = parts[2];
                string paramString = parts[3];

                var type = ReflectionHelper.FindType(typeName);
                if (type == null) return $"ERROR|Type not found: {typeName}";

                var method = ReflectionHelper.FindMethod(type, methodName);
                if (method == null) return $"ERROR|Method not found: {methodName}";

                object[] parameters = null;
                if (!string.IsNullOrEmpty(paramString) && paramString != "null")
                {
                    var methodParams = method.GetParameters();
                    
                    if (paramString.Trim().StartsWith("["))
                    {
                        // JSON Array parsing
                        try 
                        {
                            var jArray = JArray.Parse(paramString);
                            if (jArray.Count != methodParams.Length)
                                return $"ERROR|Parameter count mismatch. Expected {methodParams.Length}, got {jArray.Count}";
                                
                            parameters = new object[methodParams.Length];
                            for (int i = 0; i < methodParams.Length; i++)
                            {
                                parameters[i] = jArray[i].ToObject(methodParams[i].ParameterType);
                            }
                        }
                        catch (Exception ex)
                        {
                            return $"ERROR|JSON parameter parsing failed: {ex.Message}";
                        }
                    }
                    else
                    {
                        // Legacy comma splitting
                        var paramParts = paramString.Split(',');
                        
                        if (paramParts.Length != methodParams.Length)
                             return $"ERROR|Parameter count mismatch. Expected {methodParams.Length}, got {paramParts.Length}";

                        parameters = new object[methodParams.Length];
                        for (int i = 0; i < methodParams.Length; i++)
                        {
                            parameters[i] = ReflectionHelper.ParseValue(paramParts[i], methodParams[i].ParameterType);
                        }
                    }
                }

                var result = method.Invoke(null, parameters);
                return $"SUCCESS|{ReflectionHelper.FormatValue(result)}";
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Invoke error: {ex}");
                return $"ERROR|Invoke failed: {ex.InnerException?.Message ?? ex.Message}";
            }
        }

        private static string GetFieldStatic(string args, string[] parts)
        {
            if (parts.Length < 3) return "ERROR|Invalid format";
            string typeName = parts[1];
            string fieldName = parts[2];

            var type = ReflectionHelper.FindType(typeName);
            if (type == null) return $"ERROR|Type not found: {typeName}";

            var field = ReflectionHelper.FindField(type, fieldName);
            if (field == null) return $"ERROR|Field not found: {fieldName}";

            try
            {
                var value = field.GetValue(null);
                return $"SUCCESS|{ReflectionHelper.FormatValue(value)}";
            }
            catch (Exception ex)
            {
                return $"ERROR|Get field failed: {ex.Message}";
            }
        }

        private static string SetFieldStatic(string args, string[] parts)
        {
            if (parts.Length < 4) return "ERROR|Invalid format";
            string typeName = parts[1];
            string fieldName = parts[2];
            string valueStr = parts[3];

            var type = ReflectionHelper.FindType(typeName);
            if (type == null) return $"ERROR|Type not found: {typeName}";

            var field = ReflectionHelper.FindField(type, fieldName);
            if (field == null) return $"ERROR|Field not found: {fieldName}";

            try
            {
                object val = ReflectionHelper.ParseValue(valueStr, field.FieldType);
                field.SetValue(null, val);
                return "SUCCESS|Value set";
            }
            catch (Exception ex)
            {
                return $"ERROR|Set field failed: {ex.Message}";
            }
        }
    }
}
