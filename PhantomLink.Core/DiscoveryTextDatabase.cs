using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PhantomLink.Core
{
    public sealed class DiscoveryTextDatabase
    {
        public sealed class RelationshipPair
        {
            public string A { get; set; }
            public string B { get; set; }
        }

        public sealed class Section
        {
            public string Name { get; set; }
            public List<string> CommonFieldNames { get; set; } = new List<string>();
            public List<string> CommonTypes { get; set; } = new List<string>();
            public List<string> CommonBehaviors { get; set; } = new List<string>();
            public List<string> CommonComponents { get; set; } = new List<string>();
            public List<string> CommonMethodPatterns { get; set; } = new List<string>();
            public List<string> CommonUpdatePatterns { get; set; } = new List<string>();
            public List<string> CommonRelationships { get; set; } = new List<string>();
            public List<RelationshipPair> RelationshipPairs { get; set; } = new List<RelationshipPair>();

            public HashSet<string> FieldNameTokens { get; set; } = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> TypeTokens { get; set; } = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> ComponentTokens { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        }

        private readonly Dictionary<string, Section> _sections;

        public string SourcePath { get; }
        public DateTime LoadedUtc { get; }
        public IReadOnlyDictionary<string, Section> Sections => _sections;

        private DiscoveryTextDatabase(string sourcePath, Dictionary<string, Section> sections)
        {
            SourcePath = sourcePath ?? "";
            LoadedUtc = DateTime.UtcNow;
            _sections = sections ?? new Dictionary<string, Section>(StringComparer.OrdinalIgnoreCase);
        }

        public bool TryGetSection(string name, out Section section)
        {
            section = null;
            if (string.IsNullOrWhiteSpace(name))
                return false;
            return _sections.TryGetValue(name.Trim(), out section);
        }

        public static DiscoveryTextDatabase LoadFromBaseDirectory(string fileName = "database.txt")
        {
            return LoadFromFile(FindDefaultDatabasePath(fileName));
        }

        public static DiscoveryTextDatabase LoadFromFile(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return new DiscoveryTextDatabase(path ?? "", new Dictionary<string, Section>(StringComparer.OrdinalIgnoreCase));

                var lines = File.ReadAllLines(path);
                var sections = new Dictionary<string, Section>(StringComparer.OrdinalIgnoreCase);

                Section current = null;
                for (var i = 0; i < lines.Length; i++)
                {
                    var raw = lines[i] ?? "";
                    // Robust trimming: remove any carriage returns, then whitespace
                    var line = raw.Replace("\r", "").Trim();
                    if (line.Length == 0)
                        continue;
                    if (line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal))
                        continue;

                    if (line.StartsWith("SECTION:", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = line.Substring("SECTION:".Length).Trim();
                        if (name.Length == 0)
                            continue;
                        current = new Section { Name = name };
                        sections[name] = current;
                        continue;
                    }

                    if (current == null)
                        continue;

                    var idx = line.IndexOf(':');
                    if (idx <= 0 || idx >= line.Length - 1)
                    {
                        AddBareLine(current, line);
                        continue;
                    }

                    var key = line.Substring(0, idx).Trim();
                    var value = line.Substring(idx + 1).Trim();
                    if (key.Length == 0 || value.Length == 0)
                        continue;

                    if (key.Equals("Common field names", StringComparison.OrdinalIgnoreCase))
                        current.CommonFieldNames.AddRange(SplitCsv(value));
                    else if (key.Equals("Common types", StringComparison.OrdinalIgnoreCase))
                        current.CommonTypes.AddRange(SplitCsv(value));
                    else if (key.Equals("Common behaviors", StringComparison.OrdinalIgnoreCase))
                        current.CommonBehaviors.AddRange(SplitCsv(value));
                    else if (key.Equals("Common components", StringComparison.OrdinalIgnoreCase))
                        current.CommonComponents.AddRange(SplitCsv(value));
                    else if (key.Equals("Common method patterns", StringComparison.OrdinalIgnoreCase))
                        current.CommonMethodPatterns.AddRange(SplitCsv(value));
                    else if (key.Equals("Common update patterns", StringComparison.OrdinalIgnoreCase))
                        current.CommonUpdatePatterns.AddRange(SplitCsv(value));
                    else if (key.Equals("Common relationships", StringComparison.OrdinalIgnoreCase))
                        current.CommonRelationships.AddRange(SplitCsv(value));
                    else
                        AddBareLine(current, value);
                }

                foreach (var sec in sections.Values)
                {
                    NormalizeSection(sec);
                    BuildTokens(sec);
                }

                return new DiscoveryTextDatabase(path, sections);
            }
            catch
            {
                return new DiscoveryTextDatabase(path ?? "", new Dictionary<string, Section>(StringComparer.OrdinalIgnoreCase));
            }
        }

        private static void BuildTokens(Section section)
        {
            section.FieldNameTokens = new HashSet<string>(StringComparer.Ordinal);
            section.TypeTokens = new HashSet<string>(StringComparer.Ordinal);
            section.ComponentTokens = new HashSet<string>(StringComparer.Ordinal);

            AddTokens(section.FieldNameTokens, section.CommonFieldNames);
            AddTokens(section.FieldNameTokens, section.CommonRelationships);
            AddTokens(section.TypeTokens, section.CommonTypes);
            AddTokens(section.ComponentTokens, section.CommonComponents);
            AddTokens(section.ComponentTokens, section.CommonFieldNames);
        }

        private static void NormalizeSection(Section section)
        {
            if (section == null)
                return;

            section.CommonFieldNames = section.CommonFieldNames.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            section.CommonTypes = section.CommonTypes.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            section.CommonBehaviors = section.CommonBehaviors.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            section.CommonComponents = section.CommonComponents.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            section.CommonMethodPatterns = section.CommonMethodPatterns.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            section.CommonUpdatePatterns = section.CommonUpdatePatterns.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            section.CommonRelationships = section.CommonRelationships.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            for (var i = 0; i < section.CommonFieldNames.Count; i++)
            {
                var f = section.CommonFieldNames[i];
                if (string.IsNullOrWhiteSpace(f))
                    continue;
                var t = f.Trim().ToLowerInvariant();
                if (t == "int" || t == "int32" || t == "long" || t == "int64" || t == "float" || t == "single" || t == "double" || t == "bool" || t == "boolean" || t == "list" || t == "array" || t == "dictionary")
                    section.CommonTypes.Add(t == "single" ? "float" : (t == "boolean" ? "bool" : t));
            }
            section.CommonTypes = section.CommonTypes.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (section.RelationshipPairs == null)
                section.RelationshipPairs = new List<RelationshipPair>();
            section.RelationshipPairs = section.RelationshipPairs
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.A) && !string.IsNullOrWhiteSpace(p.B))
                .ToList();
        }

        private static void AddBareLine(Section current, string line)
        {
            if (current == null || string.IsNullOrWhiteSpace(line))
                return;

            if (current.Name != null && current.Name.Equals("RELATIONAL TARGETS", StringComparison.OrdinalIgnoreCase))
            {
                current.CommonRelationships.Add(line);
                if (TryParseRelationshipPair(line, out var pair))
                    current.RelationshipPairs.Add(pair);
                return;
            }

            current.CommonFieldNames.Add(line);
        }

        private static bool TryParseRelationshipPair(string line, out RelationshipPair pair)
        {
            pair = null;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var text = line.Trim();
            var andIdx = text.IndexOf(" and ", StringComparison.OrdinalIgnoreCase);
            if (andIdx > 0)
            {
                var a = text.Substring(0, andIdx).Trim();
                var b = text.Substring(andIdx + " and ".Length).Trim();
                if (a.Length == 0 || b.Length == 0)
                    return false;
                pair = new RelationshipPair { A = a, B = b };
                return true;
            }

            var sep = text.IndexOf("->", StringComparison.OrdinalIgnoreCase);
            if (sep > 0)
            {
                var a = text.Substring(0, sep).Trim();
                var b = text.Substring(sep + 2).Trim();
                if (a.Length == 0 || b.Length == 0)
                    return false;
                pair = new RelationshipPair { A = a, B = b };
                return true;
            }

            return false;
        }

        private static void AddTokens(HashSet<string> into, IEnumerable<string> items)
        {
            if (into == null || items == null)
                return;
            foreach (var it in items)
            {
                foreach (var t in TokenizeToSet(it))
                    into.Add(t);
            }
        }

        private static IEnumerable<string> SplitCsv(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                yield break;
            var parts = value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var p = parts[i]?.Trim();
                if (string.IsNullOrWhiteSpace(p))
                    continue;
                yield return p;
            }
        }

        private static HashSet<string> TokenizeToSet(string text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(text))
                return set;

            var sb = new StringBuilder();
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (char.IsLetterOrDigit(c))
                {
                    if (sb.Length > 0 && char.IsUpper(c) && char.IsLower(sb[sb.Length - 1]))
                    {
                        AddToken(set, sb);
                        sb.Clear();
                    }
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    AddToken(set, sb);
                    sb.Clear();
                }
            }
            AddToken(set, sb);
            return set;
        }

        private static void AddToken(HashSet<string> set, StringBuilder sb)
        {
            if (sb == null || sb.Length == 0)
                return;
            if (sb.Length <= 1)
                return;
            set.Add(sb.ToString());
        }

        private static string FindDefaultDatabasePath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "database.txt";

            if (Path.IsPathRooted(fileName))
                return fileName;

            if (fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar))
                return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? "", fileName));

            var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
            var direct = Path.Combine(baseDir, fileName);
            if (File.Exists(direct))
                return direct;

            var sub = Path.Combine(baseDir, "Database", fileName);
            if (File.Exists(sub))
                return sub;

            var cur = baseDir;
            for (var i = 0; i < 6; i++)
            {
                try { cur = Directory.GetParent(cur)?.FullName; } catch { cur = null; }
                if (string.IsNullOrWhiteSpace(cur))
                    break;
                var p = Path.Combine(cur, "Database", fileName);
                if (File.Exists(p))
                    return p;
            }

            return direct;
        }
    }

    public static class DiscoveryTextDatabaseProvider
    {
        private static readonly object Sync = new object();
        private static DiscoveryTextDatabase _db;
        private static DateTime _lastWriteUtc;
        private static DateTime _nextCheckUtc = DateTime.MinValue;

        public static DiscoveryTextDatabase Get(string fileName = "database.txt")
        {
            lock (Sync)
            {
                var now = DateTime.UtcNow;
                if (_db == null)
                {
                    _db = DiscoveryTextDatabase.LoadFromBaseDirectory(fileName);
                    _lastWriteUtc = SafeGetLastWriteUtc(_db.SourcePath);
                    _nextCheckUtc = now.AddSeconds(2);
                    return _db;
                }

                if (now < _nextCheckUtc)
                    return _db;

                _nextCheckUtc = now.AddSeconds(2);

                var path = _db.SourcePath;
                if (string.IsNullOrWhiteSpace(path))
                    return _db;

                var lw = SafeGetLastWriteUtc(path);
                if (lw != DateTime.MinValue && lw != _lastWriteUtc)
                {
                    _db = DiscoveryTextDatabase.LoadFromFile(path);
                    _lastWriteUtc = lw;
                }
                return _db;
            }
        }

        private static DateTime SafeGetLastWriteUtc(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return DateTime.MinValue;
                return File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }
}
