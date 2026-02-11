using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Collections.Concurrent;

namespace PhantomLink.Core
{
    public class TypeInfoWrapper
    {
        public string TypeName { get; set; }
        public string FullName { get; set; }
        public string Namespace { get; set; }
        public bool IsClass { get; set; }
        public bool IsInterface { get; set; }
        public bool IsEnum { get; set; }
        public bool IsValueType { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsSealed { get; set; }
        public bool IsPublic { get; set; }
        public Type Type { get; set; }
        public int MethodCount { get; set; }
        public int FieldCount { get; set; }
        public int PropertyCount { get; set; }
    }

    public class ClassInfoWrapper
    {
        public string ClassName { get; set; }
        public string FullName { get; set; }
        public string BaseType { get; set; }
        public string[] Interfaces { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsSealed { get; set; }
        public bool IsPublic { get; set; }
        public Type ClassType { get; set; }
        public int MethodCount { get; set; }
        public int FieldCount { get; set; }
        public int PropertyCount { get; set; }
    }

    public class MethodInfoWrapper
    {
        public string MethodName { get; set; }
        public string DeclaringType { get; set; }
        public string ReturnType { get; set; }
        public string[] ParameterTypes { get; set; }
        public bool IsStatic { get; set; }
        public bool IsPublic { get; set; }
        public bool IsVirtual { get; set; }
        public bool IsAbstract { get; set; }
        public MethodInfo MethodInfo { get; set; }
    }

    public class FieldInfoWrapper
    {
        public string FieldName { get; set; }
        public string DeclaringType { get; set; }
        public string FieldType { get; set; }
        public bool IsStatic { get; set; }
        public bool IsPublic { get; set; }
        public bool IsLiteral { get; set; }
        public bool IsInitOnly { get; set; }
        public bool CanWrite { get; set; }
        public FieldInfo FieldInfo { get; set; }
        public object Value { get; set; }
    }

    public class PropertyInfoWrapper
    {
        public string PropertyName { get; set; }
        public string DeclaringType { get; set; }
        public string PropertyType { get; set; }
        public bool CanRead { get; set; }
        public bool CanWrite { get; set; }
        public bool IsStatic { get; set; }
        public bool IsPublic { get; set; }
        public PropertyInfo PropertyInfo { get; set; }
    }

    public static class TypeBrowser
    {
        private struct MemberCounts
        {
            public int Methods;
            public int Fields;
            public int Properties;
        }

        private static readonly ConcurrentDictionary<Assembly, Type[]> _assemblyTypesCache = new ConcurrentDictionary<Assembly, Type[]>();
        private static readonly ConcurrentDictionary<Type, MemberCounts> _memberCountsCache = new ConcurrentDictionary<Type, MemberCounts>();
        private static readonly ConcurrentDictionary<Type, string[]> _interfaceNamesCache = new ConcurrentDictionary<Type, string[]>();

        private static Type[] GetTypesCached(Assembly assembly)
        {
            if (assembly == null)
                return Array.Empty<Type>();

            return _assemblyTypesCache.GetOrAdd(assembly, asm =>
            {
                try
                {
                    return asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    return ex.Types.Where(t => t != null).ToArray();
                }
                catch
                {
                    return Array.Empty<Type>();
                }
            });
        }

        private static MemberCounts GetCounts(Type type)
        {
            if (type == null)
                return default;

            return _memberCountsCache.GetOrAdd(type, t =>
            {
                try
                {
                    var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                    var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                    var properties = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                    return new MemberCounts { Methods = methods.Length, Fields = fields.Length, Properties = properties.Length };
                }
                catch
                {
                    return default;
                }
            });
        }

        private static string SafeGetName(Type type)
        {
            try { return type?.Name; } catch { return null; }
        }

        private static string SafeGetFullName(Type type)
        {
            try { return type?.FullName; } catch { return null; }
        }

        private static string SafeGetNamespace(Type type)
        {
            try { return type?.Namespace; } catch { return null; }
        }

        private static Type[] SafeGetTypes(Assembly assembly)
        {
            try { return GetTypesCached(assembly); } catch { return Array.Empty<Type>(); }
        }

        private static PropertyInfo[] SafeGetProperties(Type type)
        {
            try
            {
                return type?.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance) ?? Array.Empty<PropertyInfo>();
            }
            catch
            {
                return Array.Empty<PropertyInfo>();
            }
        }

        private static MethodInfo[] SafeGetMethods(Type type)
        {
            try
            {
                return type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance) ?? Array.Empty<MethodInfo>();
            }
            catch
            {
                return Array.Empty<MethodInfo>();
            }
        }

        private static FieldInfo[] SafeGetFields(Type type)
        {
            try
            {
                return type?.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance) ?? Array.Empty<FieldInfo>();
            }
            catch
            {
                return Array.Empty<FieldInfo>();
            }
        }

        public static IEnumerable<TypeInfoWrapper> SearchTypes(
            IEnumerable<Assembly> assemblies,
            string searchTerm = null,
            string namespaceFilter = null)
        {
            foreach (var assembly in assemblies)
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    string typeName;
                    string fullName;
                    string ns;
                    bool isClass;
                    bool isInterface;
                    bool isEnum;
                    bool isValueType;
                    bool isAbstract;
                    bool isSealed;
                    bool isPublic;
                    try
                    {
                        typeName = SafeGetName(type);
                        fullName = SafeGetFullName(type);
                        ns = SafeGetNamespace(type);
                        isClass = type.IsClass;
                        isInterface = type.IsInterface;
                        isEnum = type.IsEnum;
                        isValueType = type.IsValueType;
                        isAbstract = type.IsAbstract;
                        isSealed = type.IsSealed;
                        isPublic = type.IsPublic;
                    }
                    catch
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(namespaceFilter) && !(ns?.Contains(namespaceFilter, StringComparison.OrdinalIgnoreCase) == true))
                        continue;

                    if (!string.IsNullOrEmpty(searchTerm) &&
                        !((typeName?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) == true) ||
                          (fullName?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) == true)))
                        continue;

                    var counts = GetCounts(type);

                    yield return new TypeInfoWrapper
                    {
                        TypeName = typeName,
                        FullName = fullName,
                        Namespace = ns,
                        IsClass = isClass,
                        IsInterface = isInterface,
                        IsEnum = isEnum,
                        IsValueType = isValueType,
                        IsAbstract = isAbstract,
                        IsSealed = isSealed,
                        IsPublic = isPublic,
                        Type = type,
                        MethodCount = counts.Methods,
                        FieldCount = counts.Fields,
                        PropertyCount = counts.Properties
                    };
                }
            }
        }

        public static IEnumerable<ClassInfoWrapper> SearchClasses(
            IEnumerable<Assembly> assemblies,
            string searchTerm = null,
            string namespaceFilter = null)
        {
            foreach (var assembly in assemblies)
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    string typeName;
                    string fullName;
                    string ns;
                    bool isClass;
                    bool isAbstract;
                    bool isSealed;
                    bool isPublic;
                    Type baseType;
                    try
                    {
                        isClass = type.IsClass;
                        if (!isClass)
                            continue;
                        typeName = SafeGetName(type);
                        fullName = SafeGetFullName(type);
                        ns = SafeGetNamespace(type);
                        isAbstract = type.IsAbstract;
                        isSealed = type.IsSealed;
                        isPublic = type.IsPublic;
                        baseType = type.BaseType;
                    }
                    catch
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(namespaceFilter) &&
                        !(ns?.Contains(namespaceFilter, StringComparison.OrdinalIgnoreCase) == true))
                        continue;

                    if (!string.IsNullOrEmpty(searchTerm) &&
                        !((typeName?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) == true) ||
                          (fullName?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) == true)))
                        continue;

                    var counts = GetCounts(type);
                    var interfaces = _interfaceNamesCache.GetOrAdd(type, t =>
                    {
                        try
                        {
                            return t.GetInterfaces().Select(i => i.FullName).ToArray();
                        }
                        catch
                        {
                            return Array.Empty<string>();
                        }
                    });

                    yield return new ClassInfoWrapper
                    {
                        ClassName = typeName,
                        FullName = fullName,
                        BaseType = SafeGetFullName(baseType),
                        Interfaces = interfaces,
                        IsAbstract = isAbstract,
                        IsSealed = isSealed,
                        IsPublic = isPublic,
                        ClassType = type,
                        MethodCount = counts.Methods,
                        FieldCount = counts.Fields,
                        PropertyCount = counts.Properties
                    };
                }
            }
        }

        public static IEnumerable<PropertyInfoWrapper> SearchProperties(
            IEnumerable<Assembly> assemblies,
            string searchTerm = null,
            string typeFilter = null)
        {
            foreach (var assembly in assemblies)
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    string typeName;
                    string fullName;
                    try
                    {
                        typeName = SafeGetName(type);
                        fullName = SafeGetFullName(type);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(typeFilter) &&
                        !((typeName?.Contains(typeFilter, StringComparison.OrdinalIgnoreCase) == true) ||
                          (fullName?.Contains(typeFilter, StringComparison.OrdinalIgnoreCase) == true)))
                        continue;

                    foreach (var property in SafeGetProperties(type))
                    {
                        if (!string.IsNullOrEmpty(searchTerm) &&
                            !property.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string declaringType;
                        string propertyType;
                        bool canRead;
                        bool canWrite;
                        bool isStatic;
                        bool isPublic;
                        try
                        {
                            declaringType = fullName;
                            propertyType = property.PropertyType?.Name;
                            canRead = property.CanRead;
                            canWrite = property.CanWrite;
                            isStatic = (property.GetGetMethod(true)?.IsStatic == true) ||
                                      (property.GetSetMethod(true)?.IsStatic == true);
                            isPublic = property.GetGetMethod(true)?.IsPublic == true ||
                                      property.GetSetMethod(true)?.IsPublic == true;
                        }
                        catch
                        {
                            continue;
                        }

                        yield return new PropertyInfoWrapper
                        {
                            PropertyName = property.Name,
                            DeclaringType = declaringType,
                            PropertyType = propertyType,
                            CanRead = canRead,
                            CanWrite = canWrite,
                            IsStatic = isStatic,
                            IsPublic = isPublic,
                            PropertyInfo = property
                        };
                    }
                }
            }
        }

        public static IEnumerable<MethodInfoWrapper> SearchMethods(
            IEnumerable<Assembly> assemblies,
            string searchTerm = null,
            string typeFilter = null)
        {
            foreach (var assembly in assemblies)
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    string typeName;
                    string fullName;
                    try
                    {
                        typeName = SafeGetName(type);
                        fullName = SafeGetFullName(type);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(typeFilter) &&
                        !((typeName?.Contains(typeFilter, StringComparison.OrdinalIgnoreCase) == true) ||
                          (fullName?.Contains(typeFilter, StringComparison.OrdinalIgnoreCase) == true)))
                        continue;

                    foreach (var method in SafeGetMethods(type))
                    {
                        if (!string.IsNullOrEmpty(searchTerm) &&
                            !method.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string returnType;
                        string[] parameterTypes;
                        bool isStatic;
                        bool isPublic;
                        bool isVirtual;
                        bool isAbstract;
                        try
                        {
                            returnType = method.ReturnType?.Name;
                            parameterTypes = method.GetParameters().Select(p => p.ParameterType?.Name).ToArray();
                            isStatic = method.IsStatic;
                            isPublic = method.IsPublic;
                            isVirtual = method.IsVirtual;
                            isAbstract = method.IsAbstract;
                        }
                        catch
                        {
                            continue;
                        }

                        yield return new MethodInfoWrapper
                        {
                            MethodName = method.Name,
                            DeclaringType = fullName,
                            ReturnType = returnType,
                            ParameterTypes = parameterTypes,
                            IsStatic = isStatic,
                            IsPublic = isPublic,
                            IsVirtual = isVirtual,
                            IsAbstract = isAbstract,
                            MethodInfo = method
                        };
                    }
                }
            }
        }

        public static IEnumerable<FieldInfoWrapper> SearchFields(
            IEnumerable<Assembly> assemblies,
            string searchTerm = null,
            string typeFilter = null)
        {
            foreach (var assembly in assemblies)
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    string typeName;
                    string fullName;
                    try
                    {
                        typeName = SafeGetName(type);
                        fullName = SafeGetFullName(type);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(typeFilter) &&
                        !((typeName?.Contains(typeFilter, StringComparison.OrdinalIgnoreCase) == true) ||
                          (fullName?.Contains(typeFilter, StringComparison.OrdinalIgnoreCase) == true)))
                        continue;

                    foreach (var field in SafeGetFields(type))
                    {
                        if (!string.IsNullOrEmpty(searchTerm) &&
                            !field.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string fieldType;
                        bool isStatic;
                        bool isPublic;
                        bool isLiteral;
                        bool isInitOnly;
                        bool canWrite;
                        try
                        {
                            fieldType = field.FieldType?.Name;
                            isStatic = field.IsStatic;
                            isPublic = field.IsPublic;
                            isLiteral = field.IsLiteral;
                            isInitOnly = field.IsInitOnly;
                            canWrite = !isInitOnly && !isLiteral;
                        }
                        catch
                        {
                            continue;
                        }

                        yield return new FieldInfoWrapper
                        {
                            FieldName = field.Name,
                            DeclaringType = fullName,
                            FieldType = fieldType,
                            IsStatic = isStatic,
                            IsPublic = isPublic,
                            IsLiteral = isLiteral,
                            IsInitOnly = isInitOnly,
                            CanWrite = canWrite,
                            FieldInfo = field
                        };
                    }
                }
            }
        }

        public static IEnumerable<Assembly> GetLoadedAssemblies()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(asm => !asm.IsDynamic && !string.IsNullOrEmpty(asm.Location))
                .Where(asm => ShouldIncludeAssembly(asm.GetName().Name));
        }

        public static Assembly GetAssemblyByName(string assemblyName)
        {
            if (!ShouldIncludeAssembly(assemblyName))
                return null;

            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(asm => asm.GetName().Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase));
        }

        public static Type GetTypeByName(string typeName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(asm => ShouldIncludeAssembly(asm.GetName().Name))
                .SelectMany(asm => GetTypesCached(asm))
                .FirstOrDefault(type => type.FullName?.Equals(typeName, StringComparison.OrdinalIgnoreCase) == true);
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
