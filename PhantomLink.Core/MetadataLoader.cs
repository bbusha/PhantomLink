using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace PhantomLink.Core
{
    /// <summary>
    /// Metadata loader for external tool - loads assemblies from game directories
    /// Follows architecture rules: Il2Cpp from MelonLoader/Il2CppAssemblies, Mono from GameName_Data/Managed
    /// </summary>
    public static class MetadataLoader
    {
        private static List<Assembly> _loadedAssemblies = new List<Assembly>();
        private static List<Type> _allTypesCache;
        private static readonly object _typeCacheLock = new object();
        private static readonly object _assemblyResolveLock = new object();
        private static HashSet<string> _currentlyResolving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string _gameDirectory;
        private static string _metadataAssemblyDirectory;
        private static bool _assemblyResolverRegistered;
        private static bool _isIL2CPP = false;
        private static bool _isMono = false;
        public static event Action<string, int> MetadataLoaded;

        /// <summary>
        /// Initialize metadata loader with game directory
        /// </summary>
        public static void Initialize(string gameDirectory)
        {
            _gameDirectory = gameDirectory ?? throw new ArgumentNullException(nameof(gameDirectory));
            
            if (!Directory.Exists(_gameDirectory))
            {
                throw new DirectoryNotFoundException($"Game directory not found: {_gameDirectory}");
            }

            _isIL2CPP = false;
            _isMono = false;
            DetectGameType();
            Logger.LogInfo($"MetadataLoader initialized for {(_isIL2CPP ? "IL2CPP" : "Mono")} game at: {_gameDirectory}");
        }

        /// <summary>
        /// Load all available assemblies from the game
        /// </summary>
        public static async Task LoadAllAssembliesAsync()
        {
            if (string.IsNullOrEmpty(_gameDirectory))
            {
                throw new InvalidOperationException("MetadataLoader not initialized - call Initialize() first");
            }

            try
            {
                var start = DateTime.UtcNow;
                Logger.LogInfo($"Loading game assemblies from: {_gameDirectory}");
                
                string[] assemblyPaths;
                string metadataAssemblyDirectory;
                
                if (_isIL2CPP)
                {
                    // Load from MelonLoader/Il2CppAssemblies for IL2CPP games
                    metadataAssemblyDirectory = Path.Combine(_gameDirectory, "MelonLoader", "Il2CppAssemblies");
                    if (!Directory.Exists(metadataAssemblyDirectory))
                    {
                        throw new DirectoryNotFoundException($"IL2CPP assemblies directory not found: {metadataAssemblyDirectory}");
                    }
                    assemblyPaths = await Task.Run(() => Directory.GetFiles(metadataAssemblyDirectory, "*.dll"));
                }
                else
                {
                    // Load from *_Data/Managed for Mono games
                    metadataAssemblyDirectory = FindMonoManagedPath(_gameDirectory);
                    if (metadataAssemblyDirectory == null || !Directory.Exists(metadataAssemblyDirectory))
                    {
                        throw new DirectoryNotFoundException($"Managed assemblies directory not found under: {_gameDirectory}");
                    }
                    assemblyPaths = await Task.Run(() => Directory.GetFiles(metadataAssemblyDirectory, "*.dll"));
                }

                _metadataAssemblyDirectory = metadataAssemblyDirectory;
                EnsureAssemblyResolverRegistered();

                assemblyPaths = assemblyPaths
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                _loadedAssemblies.Clear();
                
                await Task.Run(() =>
                {
                    foreach (string assemblyPath in assemblyPaths)
                    {
                        try
                        {
                            // Load assembly for metadata using standard loading
                            Assembly assembly = Assembly.LoadFrom(assemblyPath);
                            _loadedAssemblies.Add(assembly);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning($"Failed to load assembly {Path.GetFileName(assemblyPath)}: {ex.Message}");
                        }
                    }
                });

                RebuildTypeCache();

                var elapsedMs = (int)(DateTime.UtcNow - start).TotalMilliseconds;
                Logger.LogInfo($"Successfully loaded {_loadedAssemblies.Count} assemblies in {elapsedMs}ms");
                MetadataLoaded?.Invoke(_gameDirectory, _loadedAssemblies.Count);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to load assemblies: {ex.Message}");
                throw;
            }
        }

        public static async Task<bool> TryInitializeAndLoadAsync(string gameDirectory)
        {
            try
            {
                Initialize(gameDirectory);
                await LoadAllAssembliesAsync();
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"MetadataLoader failed for '{gameDirectory}': {ex.Message}");
                return false;
            }
        }

        private static bool ShouldLoadAssembly(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName))
                return false;
            return true;
        }

        public static string TryDetectGameDirectoryFromToolLocation(string toolBaseDirectory)
        {
            if (string.IsNullOrWhiteSpace(toolBaseDirectory))
                return null;

            try
            {
                var start = toolBaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var candidates = new List<string>();

                candidates.Add(start);

                var parent = Directory.GetParent(start)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent))
                    candidates.Add(parent);

                var parent2 = !string.IsNullOrWhiteSpace(parent) ? Directory.GetParent(parent)?.FullName : null;
                if (!string.IsNullOrWhiteSpace(parent2))
                    candidates.Add(parent2);

                foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (LooksLikeGameRoot(candidate))
                        return candidate;
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool LooksLikeGameRoot(string directory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                    return false;

                if (Directory.Exists(Path.Combine(directory, "MelonLoader")))
                    return true;

                if (Directory.EnumerateDirectories(directory, "*_Data").Any())
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static string FindMonoManagedPath(string gameDirectory)
        {
            try
            {
                var dataDirs = Directory.GetDirectories(gameDirectory, "*_Data");
                foreach (var dataDir in dataDirs)
                {
                    var managed = Path.Combine(dataDir, "Managed");
                    if (Directory.Exists(managed))
                        return managed;
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>
        /// Get all loaded assemblies
        /// </summary>
        public static IReadOnlyList<Assembly> GetLoadedAssemblies()
        {
            return _loadedAssemblies.AsReadOnly();
        }

        public static IReadOnlyList<Assembly> GetRuntimeAssemblies()
        {
            return _loadedAssemblies
                .Where(a =>
                {
                    var n = a?.GetName()?.Name;
                    if (string.IsNullOrWhiteSpace(n))
                        return false;
                    if (n.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("Assembly-CSharp-firstpass", StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (n.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase) ||
                        n.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase))
                        return true;
                    return !IsFrameworkAssemblyName(n);
                })
                .ToList()
                .AsReadOnly();
        }

        private static bool IsFrameworkAssemblyName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return true;

            if (name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("netstandard", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("System", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("System.Core", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("System.Xml", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("System.Runtime.Serialization", StringComparison.OrdinalIgnoreCase))
                return true;

            if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase))
                return true;

            if (name.StartsWith("Il2Cppmscorlib", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Il2CppSystem", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// Get all types from all loaded assemblies
        /// </summary>
        public static IEnumerable<Type> GetAllTypes()
        {
            var cached = _allTypesCache;
            if (cached != null)
                return cached;

            lock (_typeCacheLock)
            {
                if (_allTypesCache == null)
                    RebuildTypeCache();
                return _allTypesCache ?? Enumerable.Empty<Type>();
            }
        }

        /// <summary>
        /// Find type by full name across all loaded assemblies
        /// </summary>
        public static Type FindType(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return null;
            return GetAllTypes().FirstOrDefault(t => string.Equals(t.FullName, fullName, StringComparison.Ordinal));
        }

        /// <summary>
        /// Find types by name filter across all loaded assemblies
        /// </summary>
        public static IEnumerable<Type> FindTypes(string nameFilter)
        {
            if (string.IsNullOrWhiteSpace(nameFilter))
                return Enumerable.Empty<Type>();
            return GetAllTypes().Where(t => t != null && t.Name != null && t.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Get all methods from a specific type
        /// </summary>
        public static IEnumerable<MethodInfo> GetMethods(Type type)
        {
            if (type == null) return Enumerable.Empty<MethodInfo>();
            
            try
            {
                return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Failed to get methods from type {type.FullName}: {ex.Message}");
                return Enumerable.Empty<MethodInfo>();
            }
        }

        /// <summary>
        /// Get all fields from a specific type
        /// </summary>
        public static IEnumerable<FieldInfo> GetFields(Type type)
        {
            if (type == null) return Enumerable.Empty<FieldInfo>();
            
            try
            {
                return type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Failed to get fields from type {type.FullName}: {ex.Message}");
                return Enumerable.Empty<FieldInfo>();
            }
        }

        /// <summary>
        /// Get all properties from a specific type
        /// </summary>
        public static IEnumerable<PropertyInfo> GetProperties(Type type)
        {
            if (type == null) return Enumerable.Empty<PropertyInfo>();
            
            try
            {
                return type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Failed to get properties from type {type.FullName}: {ex.Message}");
                return Enumerable.Empty<PropertyInfo>();
            }
        }

        /// <summary>
        /// Clear all loaded assemblies and reset the loader
        /// </summary>
        public static void Clear()
        {
            _loadedAssemblies.Clear();
            lock (_typeCacheLock)
            {
                _allTypesCache = null;
            }
            _gameDirectory = null;
            _isIL2CPP = false;
            _isMono = false;
            Logger.LogInfo("MetadataLoader cleared");
        }

        private static void RebuildTypeCache()
        {
            var allTypes = new List<Type>(capacity: 8192);
            foreach (var assembly in _loadedAssemblies)
            {
                try
                {
                    allTypes.AddRange(assembly.GetTypes());
                }
                catch (ReflectionTypeLoadException ex)
                {
                    LogTypeLoadFailure(assembly, ex);
                    allTypes.AddRange(ex.Types.Where(t => t != null));
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"Failed to get types from assembly {assembly.GetName().Name}: {ex.Message}");
                }
            }

            lock (_typeCacheLock)
            {
                _allTypesCache = allTypes;
            }
        }

        private static void LogTypeLoadFailure(Assembly assembly, ReflectionTypeLoadException ex)
        {
            try
            {
                var asmName = assembly?.GetName()?.Name ?? "Unknown";
                var loaded = 0;
                if (ex?.Types != null)
                    loaded = ex.Types.Count(t => t != null);
                var failed = ex?.LoaderExceptions?.Length ?? 0;

                string details = null;
                if (ex?.LoaderExceptions != null && ex.LoaderExceptions.Length > 0)
                {
                    var messages = ex.LoaderExceptions
                        .Where(e => e != null)
                        .Select(e => TrimForLog(e.Message, 180))
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .Distinct()
                        .Take(3)
                        .ToArray();

                    if (messages.Length > 0)
                        details = string.Join(" | ", messages);
                }

                if (!string.IsNullOrEmpty(details))
                    Logger.LogWarning($"Could not load some types from assembly {asmName} (loaded {loaded}, failed {failed}): {details}");
                else
                    Logger.LogWarning($"Could not load some types from assembly {asmName} (loaded {loaded}, failed {failed})");
            }
            catch
            {
            }
        }

        private static string TrimForLog(string message, int maxLen)
        {
            if (string.IsNullOrEmpty(message))
                return message;
            var oneLine = message.Replace("\r", " ").Replace("\n", " ");
            if (oneLine.Length <= maxLen)
                return oneLine;
            return oneLine.Substring(0, maxLen) + "...";
        }

        /// <summary>
        /// Detect if game is IL2CPP or Mono
        /// </summary>
        private static void DetectGameType()
        {
            // Check for IL2CPP
            string il2cppPath = Path.Combine(_gameDirectory, "MelonLoader", "Il2CppAssemblies");
            if (Directory.Exists(il2cppPath) && Directory.GetFiles(il2cppPath, "*.dll").Length > 0)
            {
                _isIL2CPP = true;
                return;
            }

            // Check for Mono
            var managedPath = FindMonoManagedPath(_gameDirectory);
            if (!string.IsNullOrEmpty(managedPath) && Directory.GetFiles(managedPath, "*.dll").Length > 0)
            {
                _isMono = true;
                return;
            }

            throw new InvalidOperationException("Could not detect game type - neither IL2CPP nor Mono assemblies found");
        }

        private static void EnsureAssemblyResolverRegistered()
        {
            if (_assemblyResolverRegistered)
                return;

            _assemblyResolverRegistered = true;
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
        }

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var metadataDir = _metadataAssemblyDirectory;
                if (string.IsNullOrWhiteSpace(metadataDir) || !Directory.Exists(metadataDir))
                    return null;

                var requested = new AssemblyName(args.Name);
                var simpleName = requested.Name;
                if (string.IsNullOrWhiteSpace(simpleName))
                    return null;

                var alreadyLoaded = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a =>
                    {
                        try
                        {
                            return string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase);
                        }
                        catch
                        {
                            return false;
                        }
                    });
                if (alreadyLoaded != null)
                    return alreadyLoaded;

                lock (_assemblyResolveLock)
                {
                    if (_currentlyResolving.Contains(simpleName))
                        return null;
                    _currentlyResolving.Add(simpleName);
                }

                try
                {
                    var candidatePath = Path.Combine(metadataDir, simpleName + ".dll");
                    if (!File.Exists(candidatePath))
                        return null;

                    return Assembly.LoadFrom(candidatePath);
                }
                finally
                {
                    lock (_assemblyResolveLock)
                    {
                        _currentlyResolving.Remove(simpleName);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Check if game is IL2CPP
        /// </summary>
        public static bool IsIL2CPP => _isIL2CPP;

        /// <summary>
        /// Check if game is Mono
        /// </summary>
        public static bool IsMono => _isMono;

        /// <summary>
        /// Get game directory
        /// </summary>
        public static string GameDirectory => _gameDirectory;
    }
}
