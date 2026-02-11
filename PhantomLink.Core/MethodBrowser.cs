using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PhantomLink.Core
{


    public static class MethodBrowser
    {
        public static IEnumerable<MethodInfoWrapper> SearchMethods(
            IEnumerable<Assembly> assemblies, 
            string searchTerm = null,
            string typeFilter = null)
        {
            foreach (var assembly in assemblies)
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (!string.IsNullOrEmpty(typeFilter) && 
                        !type.Name.Contains(typeFilter, StringComparison.OrdinalIgnoreCase) &&
                        !type.FullName.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var method in type.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | 
                        BindingFlags.Static | BindingFlags.Instance))
                    {
                        if (!string.IsNullOrEmpty(searchTerm) &&
                            !method.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                            continue;

                        yield return new MethodInfoWrapper
                        {
                            MethodName = method.Name,
                            DeclaringType = type.FullName,
                            ReturnType = method.ReturnType.Name,
                            ParameterTypes = method.GetParameters().Select(p => p.ParameterType.Name).ToArray(),
                            MethodInfo = method,
                            IsStatic = method.IsStatic,
                            IsPublic = method.IsPublic
                        };
                    }
                }
            }
        }

        public static object InvokeMethod(MethodInfo method, object instance, object[] parameters)
        {
            try
            {
                return method.Invoke(instance, parameters);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to invoke method {method.Name}: {ex.InnerException?.Message ?? ex.Message}");
                return null;
            }
        }

        public static IEnumerable<Assembly> GetUnityAssemblies()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(asm => ShouldIncludeAssembly(asm.GetName().Name));
        }

        private static bool ShouldIncludeAssembly(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            if (name.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Assembly-CSharp-firstpass", StringComparison.OrdinalIgnoreCase))
                return true;

            if (name.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase))
                return true;

            if (name.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }
}
