using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    public partial class HierarchyBrowserView : UserControl
    {
        private readonly Dictionary<Assembly, Dictionary<string, List<Type>>> _namespaceIndex = new Dictionary<Assembly, Dictionary<string, List<Type>>>();

        public HierarchyBrowserView()
        {
            InitializeComponent();
            MetadataLoader.MetadataLoaded += (_, __) => Dispatcher.Invoke(LoadTree);
        }

        private void Load_Click(object sender, RoutedEventArgs e)
        {
            LoadTree();
        }

        private void LoadTree()
        {
            try
            {
                Tree.Items.Clear();
                _namespaceIndex.Clear();

                var assemblies = MetadataLoader.GetRuntimeAssemblies();
                if (assemblies == null || assemblies.Count == 0)
                {
                    StatusText.Text = "Metadata not loaded";
                    return;
                }

                var includeUnity = IncludeUnityCheckBox.IsChecked == true;
                var filtered = assemblies
                    .Where(a =>
                    {
                        var n = a.GetName().Name;
                        if (!includeUnity)
                            return !(n.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase));
                        return true;
                    })
                    .ToList();

                foreach (var asm in filtered)
                {
                    var item = new TreeViewItem
                    {
                        Header = asm.GetName().Name,
                        Tag = new NodeTag { Kind = NodeKind.Assembly, Assembly = asm, Path = string.Empty }
                    };
                    item.Items.Add(new TreeViewItem { Header = "Loading..." });
                    item.Expanded += OnNodeExpanded;
                    Tree.Items.Add(item);
                }

                StatusText.Text = $"Assemblies: {filtered.Count}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private void OnNodeExpanded(object sender, RoutedEventArgs e)
        {
            if (sender is not TreeViewItem item)
                return;

            if (item.Items.Count == 1 && item.Items[0] is TreeViewItem child && (child.Header?.ToString() ?? "") == "Loading...")
            {
                item.Items.Clear();
                if (item.Tag is NodeTag tag)
                {
                    if (tag.Kind == NodeKind.Assembly)
                        PopulateAssembly(item, tag.Assembly);
                    else if (tag.Kind == NodeKind.Namespace)
                        PopulateNamespace(item, tag.Assembly, tag.Path);
                    else if (tag.Kind == NodeKind.Type)
                        PopulateType(item, tag.Type);
                }
            }
        }

        private void PopulateAssembly(TreeViewItem item, Assembly asm)
        {
            var index = GetOrBuildIndex(asm);

            var topSegments = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ns in index.Keys)
            {
                var seg = GetFirstSegment(ns);
                if (!string.IsNullOrWhiteSpace(seg))
                    topSegments.Add(seg);
            }

            if (index.ContainsKey(string.Empty))
            {
                var node = new TreeViewItem
                {
                    Header = "(global)",
                    Tag = new NodeTag { Kind = NodeKind.Namespace, Assembly = asm, Path = string.Empty }
                };
                node.Items.Add(new TreeViewItem { Header = "Loading..." });
                node.Expanded += OnNodeExpanded;
                item.Items.Add(node);
            }

            foreach (var seg in topSegments)
            {
                var node = new TreeViewItem
                {
                    Header = seg,
                    Tag = new NodeTag { Kind = NodeKind.Namespace, Assembly = asm, Path = seg }
                };
                node.Items.Add(new TreeViewItem { Header = "Loading..." });
                node.Expanded += OnNodeExpanded;
                item.Items.Add(node);
            }
        }

        private void PopulateNamespace(TreeViewItem item, Assembly asm, string path)
        {
            var index = GetOrBuildIndex(asm);
            var pathPrefix = path + ".";

            var subSegments = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var directTypes = new List<Type>();

            foreach (var kv in index)
            {
                var ns = kv.Key ?? string.Empty;
                if (string.Equals(ns, path, StringComparison.Ordinal))
                {
                    directTypes.AddRange(kv.Value);
                    continue;
                }

                if (!ns.StartsWith(pathPrefix, StringComparison.Ordinal))
                    continue;

                var remainder = ns.Substring(pathPrefix.Length);
                var next = GetFirstSegment(remainder);
                if (!string.IsNullOrWhiteSpace(next))
                    subSegments.Add(next);
            }

            foreach (var seg in subSegments)
            {
                var newPath = pathPrefix + seg;
                var node = new TreeViewItem
                {
                    Header = seg,
                    Tag = new NodeTag { Kind = NodeKind.Namespace, Assembly = asm, Path = newPath }
                };
                node.Items.Add(new TreeViewItem { Header = "Loading..." });
                node.Expanded += OnNodeExpanded;
                item.Items.Add(node);
            }

            foreach (var t in directTypes.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var node = new TreeViewItem
                {
                    Header = t.Name,
                    Tag = new NodeTag { Kind = NodeKind.Type, Type = t }
                };
                node.Items.Add(new TreeViewItem { Header = "Loading..." });
                node.Expanded += OnNodeExpanded;
                item.Items.Add(node);
            }
        }

        private void PopulateType(TreeViewItem item, Type type)
        {
            if (type == null)
                return;

            var fieldsNode = new TreeViewItem { Header = "Fields" };
            var propertiesNode = new TreeViewItem { Header = "Properties" };
            var methodsNode = new TreeViewItem { Header = "Methods" };

            try
            {
                foreach (var f in MetadataLoader.GetFields(type).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Take(500))
                    fieldsNode.Items.Add(new TreeViewItem { Header = $"{f.FieldType.Name} {f.Name}" });
            }
            catch
            {
            }

            try
            {
                foreach (var p in MetadataLoader.GetProperties(type).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(500))
                    propertiesNode.Items.Add(new TreeViewItem { Header = $"{p.PropertyType.Name} {p.Name}" });
            }
            catch
            {
            }

            try
            {
                foreach (var m in MetadataLoader.GetMethods(type).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Take(500))
                    methodsNode.Items.Add(new TreeViewItem { Header = $"{m.ReturnType.Name} {m.Name}" });
            }
            catch
            {
            }

            item.Items.Add(fieldsNode);
            item.Items.Add(propertiesNode);
            item.Items.Add(methodsNode);
        }

        private Dictionary<string, List<Type>> GetOrBuildIndex(Assembly asm)
        {
            if (_namespaceIndex.TryGetValue(asm, out var index))
                return index;

            index = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }
            catch
            {
                types = Array.Empty<Type>();
            }

            foreach (var t in types)
            {
                if (t == null)
                    continue;
                var ns = t.Namespace ?? string.Empty;
                if (!index.TryGetValue(ns, out var list))
                {
                    list = new List<Type>();
                    index[ns] = list;
                }
                list.Add(t);
            }

            _namespaceIndex[asm] = index;
            return index;
        }

        private static string GetFirstSegment(string ns)
        {
            if (string.IsNullOrWhiteSpace(ns))
                return string.Empty;
            var dot = ns.IndexOf('.');
            return dot < 0 ? ns : ns.Substring(0, dot);
        }

        private async void OpenInspector_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var term = FindBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(term))
                {
                    StatusText.Text = "Enter a type name";
                    return;
                }

                var matches = MetadataLoader.FindTypes(term).Take(300).ToList();
                if (matches.Count == 0)
                {
                    StatusText.Text = "No matches";
                    return;
                }

                Type selected;
                if (matches.Count == 1)
                {
                    selected = matches[0];
                }
                else
                {
                    var picker = new TypePickerDialog($"Matches for '{term}':", matches)
                    {
                        Owner = Window.GetWindow(this)
                    };
                    if (picker.ShowDialog() != true || picker.SelectedType == null)
                        return;
                    selected = picker.SelectedType;
                }

                if (selected?.FullName == null)
                    return;

                await Dispatcher.InvokeAsync(() =>
                {
                    var win = new TypeInspectorWindow(selected.FullName)
                    {
                        Owner = Window.GetWindow(this)
                    };
                    win.Show();
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private enum NodeKind
        {
            Assembly,
            Namespace,
            Type
        }

        private class NodeTag
        {
            public NodeKind Kind { get; set; }
            public Assembly Assembly { get; set; }
            public string Path { get; set; }
            public Type Type { get; set; }
        }
    }
}
