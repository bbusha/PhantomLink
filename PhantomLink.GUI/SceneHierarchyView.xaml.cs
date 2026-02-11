using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    public partial class SceneHierarchyView : UserControl
    {
        private NodeTag _selected;
        private readonly List<SearchResult> _searchResults = new List<SearchResult>();
        private bool _autoLoadedRoots;
        private bool _suppressTreeSelectionChanged;

        public event Action<int> GameObjectSelected;

        public SceneHierarchyView()
        {
            InitializeComponent();
            _ = RefreshScenesAsync();
        }

        public async Task RevealGameObjectAsync(int id)
        {
            if (id == 0)
                return;

            if (!Dispatcher.CheckAccess())
            {
                var inner = await Dispatcher.InvokeAsync(() => RevealGameObjectAsync(id));
                await inner;
                return;
            }

            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                if (Tree.Items.Count == 0)
                    await LoadRootsAsync();

                var resp = await IPCMeloaderClient.SendCommandAsync($"GET_HIERARCHY_PATH|{id}", 8000);
                if (resp == null || resp.StartsWith("ERROR|", StringComparison.Ordinal) || !resp.StartsWith("PATH|", StringComparison.Ordinal))
                {
                    await ShowGameObjectAsync(id);
                    return;
                }

                var kv = ParseKeyValues(resp);
                if (!kv.TryGetValue("ids", out var idsText) || string.IsNullOrWhiteSpace(idsText))
                {
                    await ShowGameObjectAsync(id);
                    return;
                }

                var ids = idsText.Split(',')
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                if (ids.Count == 0)
                {
                    await ShowGameObjectAsync(id);
                    return;
                }

                TreeViewItem current = null;
                for (var i = 0; i < ids.Count; i++)
                {
                    var stepId = ids[i];
                    var isRoot = i == 0;
                    var isLeaf = i == ids.Count - 1;

                    if (isRoot)
                    {
                        current = FindTreeItemById(Tree.Items, stepId);
                        if (current == null)
                        {
                            await LoadRootsAsync();
                            current = FindTreeItemById(Tree.Items, stepId);
                            if (current == null)
                                break;
                        }
                    }
                    else
                    {
                        if (current == null)
                            break;

                        await EnsureNodePopulatedAsync(current);
                        current.IsExpanded = true;
                        current = FindTreeItemById(current.Items, stepId);
                    }

                    if (current == null)
                        break;

                    if (!isLeaf)
                    {
                        await EnsureNodePopulatedAsync(current);
                        current.IsExpanded = true;
                    }
                }

                if (current != null)
                {
                    _suppressTreeSelectionChanged = true;
                    current.IsSelected = true;
                    current.BringIntoView();
                    _suppressTreeSelectionChanged = false;
                }

                await ShowGameObjectAsync(id);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
                await ShowGameObjectAsync(id);
            }
        }

        private static TreeViewItem FindTreeItemById(ItemCollection items, string id)
        {
            if (items == null || string.IsNullOrWhiteSpace(id))
                return null;

            foreach (var obj in items)
            {
                if (obj is not TreeViewItem item || item.Tag is not NodeTag tag)
                    continue;
                if (tag.Kind != NodeKind.GameObject)
                    continue;
                if (string.Equals(tag.Id, id, StringComparison.Ordinal))
                    return item;
            }

            return null;
        }

        private async Task EnsureNodePopulatedAsync(TreeViewItem item)
        {
            if (item == null)
                return;
            if (item.Items.Count == 1 && item.Items[0] is TreeViewItem placeholder && (placeholder.Header?.ToString() ?? "") == "Loading...")
            {
                item.Items.Clear();
                if (item.Tag is NodeTag tag)
                    await PopulateNodeAsync(item, tag);
            }
        }

        public async Task ShowGameObjectAsync(int id)
        {
            try
            {
                if (id == 0)
                    return;

                var tag = new NodeTag
                {
                    Kind = NodeKind.GameObject,
                    Id = id.ToString(),
                    Name = ""
                };

                await Dispatcher.InvokeAsync(() =>
                {
                    _selected = tag;
                    SelectedIdText.Text = tag.Id ?? string.Empty;
                    SelectedTypeText.Text = string.Empty;
                    NameBox.Text = string.Empty;
                    ActiveBox.IsChecked = false;
                    TagBox.Text = string.Empty;
                    LayerBox.Text = string.Empty;
                    ParentIdBox.Text = string.Empty;
                    SiblingIndexBox.Text = string.Empty;
                    PosXBox.Text = string.Empty;
                    PosYBox.Text = string.Empty;
                    PosZBox.Text = string.Empty;
                    RotXBox.Text = string.Empty;
                    RotYBox.Text = string.Empty;
                    RotZBox.Text = string.Empty;
                    ScaleXBox.Text = string.Empty;
                    ScaleYBox.Text = string.Empty;
                    ScaleZBox.Text = string.Empty;
                    ComponentEnabledBox.IsChecked = false;

                    NameBox.IsEnabled = true;
                    ActiveBox.IsEnabled = true;
                    TagBox.IsEnabled = true;
                    LayerBox.IsEnabled = true;
                    PosXBox.IsEnabled = true;
                    PosYBox.IsEnabled = true;
                    PosZBox.IsEnabled = true;
                    RotXBox.IsEnabled = true;
                    RotYBox.IsEnabled = true;
                    RotZBox.IsEnabled = true;
                    ScaleXBox.IsEnabled = true;
                    ScaleYBox.IsEnabled = true;
                    ScaleZBox.IsEnabled = true;
                    AddComponentBox.IsEnabled = true;
                    ComponentEnabledBox.IsEnabled = false;
                    ParentIdBox.IsEnabled = true;
                    SiblingIndexBox.IsEnabled = true;
                    NewNameBox.IsEnabled = true;
                });

                if (!IPCMeloaderClient.IsConnected)
                {
                    await Dispatcher.InvokeAsync(() => StatusText.Text = "Not connected");
                    return;
                }

                var nodeResp = await IPCMeloaderClient.SendCommandAsync($"GET_NODE_INFO|{tag.Id}", 8000);
                if (nodeResp != null && nodeResp.StartsWith("NODE|", StringComparison.Ordinal))
                {
                    var nkv = ParseKeyValues(nodeResp);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (nkv.TryGetValue("parentId", out var pid))
                            ParentIdBox.Text = pid;
                        if (nkv.TryGetValue("siblingIndex", out var si))
                            SiblingIndexBox.Text = si;
                    });
                }

                var infoResponse = await IPCMeloaderClient.SendCommandAsync($"GET_OBJECT_INFO||{tag.Id}", 8000);
                if (infoResponse != null && infoResponse.StartsWith("OBJECT_INFO|", StringComparison.Ordinal))
                {
                    var info = ParseKeyValues(infoResponse);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (info.TryGetValue("type", out var runtimeType))
                            SelectedTypeText.Text = runtimeType;
                        if (info.TryGetValue("name", out var name))
                            NameBox.Text = name;
                        if (info.TryGetValue("active", out var active))
                            ActiveBox.IsChecked = active == "1";
                        if (info.TryGetValue("tag", out var tagValue))
                            TagBox.Text = tagValue;
                        if (info.TryGetValue("layer", out var layerValue))
                            LayerBox.Text = layerValue;
                    });
                }

                var tResponse = await IPCMeloaderClient.SendCommandAsync($"GET_TRANSFORM|{tag.Id}", 8000);
                if (tResponse != null && tResponse.StartsWith("TRANSFORM|", StringComparison.Ordinal))
                {
                    var kv = ParseKeyValues(tResponse);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (kv.TryGetValue("localPos", out var lp))
                            SetVector3Boxes(lp, PosXBox, PosYBox, PosZBox);
                        if (kv.TryGetValue("localRot", out var lr))
                            SetVector3Boxes(lr, RotXBox, RotYBox, RotZBox);
                        if (kv.TryGetValue("localScale", out var ls))
                            SetVector3Boxes(ls, ScaleXBox, ScaleYBox, ScaleZBox);
                    });
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => StatusText.Text = $"Error: {ex.Message}");
            }
        }

        private async void RefreshScenes_Click(object sender, RoutedEventArgs e)
        {
            await RefreshScenesAsync();
        }

        private async Task RefreshScenesAsync()
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var response = await IPCMeloaderClient.SendCommandAsync("GET_SCENE_INFO", 8000);
                if (response == null)
                {
                    StatusText.Text = "No response";
                    return;
                }

                if (response.StartsWith("ERROR|", StringComparison.Ordinal))
                {
                    StatusText.Text = response;
                    return;
                }

                if (!response.StartsWith("SCENE|", StringComparison.Ordinal))
                {
                    StatusText.Text = response;
                    return;
                }

                var info = ParseKeyValues(response);
                var active = info.TryGetValue("active", out var a) ? a : string.Empty;
                var scenes = new List<string>();
                if (info.TryGetValue("scenes", out var list) && !string.IsNullOrWhiteSpace(list))
                {
                    scenes.AddRange(list.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)));
                }

                SceneCombo.ItemsSource = scenes;
                if (!string.IsNullOrWhiteSpace(active) && scenes.Any(s => string.Equals(s, active, StringComparison.OrdinalIgnoreCase)))
                    SceneCombo.SelectedItem = scenes.First(s => string.Equals(s, active, StringComparison.OrdinalIgnoreCase));
                else if (scenes.Count > 0)
                    SceneCombo.SelectedIndex = 0;

                StatusText.Text = $"Scenes: {scenes.Count}";

                if (!_autoLoadedRoots && scenes.Count > 0)
                {
                    _autoLoadedRoots = true;
                    await LoadRootsAsync();
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void LoadRoots_Click(object sender, RoutedEventArgs e)
        {
            await LoadRootsAsync();
        }

        private async Task LoadRootsAsync()
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                Tree.Items.Clear();

                var sceneName = SceneCombo.SelectedItem as string;
                var cmd = string.IsNullOrWhiteSpace(sceneName) ? "GET_SCENE_ROOTS" : $"GET_SCENE_ROOTS|{sceneName}";
                StatusText.Text = "Loading roots...";
                var response = await IPCMeloaderClient.SendCommandAsync(cmd, 8000);
                if (response == null)
                {
                    StatusText.Text = "No response";
                    return;
                }

                if (response.StartsWith("ERROR|", StringComparison.Ordinal))
                {
                    StatusText.Text = response;
                    return;
                }

                if (!response.StartsWith("ROOTS|", StringComparison.Ordinal))
                {
                    StatusText.Text = response;
                    return;
                }

                var nodes = ParseNodes(response);
                foreach (var n in nodes)
                {
                    var item = CreateGameObjectNode(n);
                    Tree.Items.Add(item);
                }

                StatusText.Text = $"Roots: {nodes.Count}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private TreeViewItem CreateGameObjectNode(Node n)
        {
            var header = n.Name;
            if (string.IsNullOrWhiteSpace(header))
                header = $"(id={n.Id})";
            if (n.Active.HasValue)
                header = n.Active.Value ? header : $"{header} (inactive)";

            var item = new TreeViewItem
            {
                Header = header,
                Tag = new NodeTag { Kind = NodeKind.GameObject, Id = n.Id, Name = n.Name }
            };

            item.Items.Add(new TreeViewItem { Header = "Loading..." });
            item.Expanded += OnExpanded;
            return item;
        }

        private TreeViewItem CreateComponentsNode(string id)
        {
            var item = new TreeViewItem
            {
                Header = "Components",
                Tag = new NodeTag { Kind = NodeKind.Components, Id = id }
            };
            item.Items.Add(new TreeViewItem { Header = "Loading..." });
            item.Expanded += OnExpanded;
            return item;
        }

        private async void OnExpanded(object sender, RoutedEventArgs e)
        {
            if (sender is not TreeViewItem item)
                return;

            if (item.Items.Count != 1 || item.Items[0] is not TreeViewItem placeholder || (placeholder.Header?.ToString() ?? "") != "Loading...")
                return;

            item.Items.Clear();

            if (item.Tag is not NodeTag tag)
                return;

            if (!IPCMeloaderClient.IsConnected)
            {
                item.Items.Add(new TreeViewItem { Header = "Not connected" });
                return;
            }

            await PopulateNodeAsync(item, tag);
        }

        private async Task PopulateNodeAsync(TreeViewItem item, NodeTag tag)
        {
            if (tag.Kind == NodeKind.GameObject)
            {
                item.Items.Add(CreateComponentsNode(tag.Id));
                var children = await FetchChildrenAsync(tag.Id);
                foreach (var c in children)
                    item.Items.Add(CreateGameObjectNode(c));
            }
            else if (tag.Kind == NodeKind.Components)
            {
                var comps = await FetchComponentsAsync(tag.Id);
                foreach (var c in comps)
                {
                    var compItem = new TreeViewItem
                    {
                        Header = c.TypeName,
                        Tag = new NodeTag { Kind = NodeKind.Component, Id = c.Id, Name = c.TypeName }
                    };
                    item.Items.Add(compItem);
                }
            }
        }

        private async Task<List<Node>> FetchChildrenAsync(string id)
        {
            var response = await IPCMeloaderClient.SendCommandAsync($"GET_CHILDREN|{id}", 8000);
            if (response == null || response.StartsWith("ERROR|", StringComparison.Ordinal) || !response.StartsWith("CHILDREN|", StringComparison.Ordinal))
                return new List<Node>();
            return ParseNodes(response);
        }

        private async Task<List<ComponentNode>> FetchComponentsAsync(string id)
        {
            var response = await IPCMeloaderClient.SendCommandAsync($"GET_COMPONENTS|{id}", 8000);
            if (response == null || response.StartsWith("ERROR|", StringComparison.Ordinal) || !response.StartsWith("COMPONENTS|", StringComparison.Ordinal))
                return new List<ComponentNode>();

            var parts = response.Split('|');
            var list = new List<ComponentNode>();
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                if (string.IsNullOrWhiteSpace(p))
                    continue;

                if (p.StartsWith("count=", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (p.StartsWith("id=", StringComparison.OrdinalIgnoreCase) && !p.Contains(";"))
                    continue;

                if (!p.StartsWith("id=", StringComparison.OrdinalIgnoreCase) || !p.Contains(";"))
                    continue;

                var idVal = string.Empty;
                var typeVal = string.Empty;
                var kvs = p.Split(';');
                foreach (var kv in kvs)
                {
                    var eq = kv.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    var key = kv.Substring(0, eq);
                    var val = kv.Substring(eq + 1);
                    if (key.Equals("id", StringComparison.OrdinalIgnoreCase))
                        idVal = val;
                    else if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
                        typeVal = val;
                }

                if (string.IsNullOrWhiteSpace(idVal))
                    continue;

                list.Add(new ComponentNode { Id = idVal, TypeName = typeVal });
            }

            return list;
        }

        private List<Node> ParseNodes(string response)
        {
            var parts = response.Split('|');
            var list = new List<Node>();
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                if (string.IsNullOrWhiteSpace(p))
                    continue;

                if (p.StartsWith("scene=", StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith("count=", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!p.StartsWith("id=", StringComparison.OrdinalIgnoreCase) || !p.Contains(";"))
                    continue;

                var id = string.Empty;
                var name = string.Empty;
                bool? active = null;

                var kvs = p.Split(';');
                foreach (var kv in kvs)
                {
                    var eq = kv.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    var key = kv.Substring(0, eq);
                    var val = kv.Substring(eq + 1);
                    if (key.Equals("id", StringComparison.OrdinalIgnoreCase))
                        id = val;
                    else if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                        name = val;
                    else if (key.Equals("active", StringComparison.OrdinalIgnoreCase))
                        active = val == "1";
                }

                if (string.IsNullOrWhiteSpace(id))
                    continue;

                list.Add(new Node { Id = id, Name = name, Active = active });
            }
            return list;
        }

        private Dictionary<string, string> ParseKeyValues(string response)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var parts = response.Split('|');
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                var eq = p.IndexOf('=');
                if (eq <= 0)
                    continue;
                dict[p.Substring(0, eq)] = p.Substring(eq + 1);
            }
            return dict;
        }

        private async void InspectSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = Tree.SelectedItem as TreeViewItem;
            if (selected?.Tag is not NodeTag tag)
                return;

            if (!IPCMeloaderClient.IsConnected)
            {
                StatusText.Text = "Not connected";
                return;
            }

            var response = await IPCMeloaderClient.SendCommandAsync($"GET_OBJECT_INFO||{tag.Id}", 8000);
            if (response == null || !response.StartsWith("OBJECT_INFO|", StringComparison.Ordinal))
            {
                StatusText.Text = response ?? "No response";
                return;
            }

            var info = ParseKeyValues(response);
            if (!info.TryGetValue("type", out var runtimeType) || string.IsNullOrWhiteSpace(runtimeType))
            {
                StatusText.Text = "Missing runtime type";
                return;
            }

            var win = new TypeInspectorWindow(runtimeType, tag.Id)
            {
                Owner = Window.GetWindow(this)
            };
            win.Show();
        }

        private async void CopySelectedPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = Tree.SelectedItem as TreeViewItem;
                if (selected?.Tag is not NodeTag tag)
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var resp = await IPCMeloaderClient.SendCommandAsync($"GET_HIERARCHY_PATH|{tag.Id}", 8000);
                if (resp == null || !resp.StartsWith("PATH|", StringComparison.Ordinal))
                {
                    StatusText.Text = resp ?? "No response";
                    return;
                }

                var kv = ParseKeyValues(resp);
                if (!kv.TryGetValue("names", out var names) || string.IsNullOrWhiteSpace(names))
                {
                    StatusText.Text = "Missing path";
                    return;
                }

                Clipboard.SetText(names);
                StatusText.Text = "Copied";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void PinSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = Tree.SelectedItem as TreeViewItem;
                if (selected?.Tag is not NodeTag tag)
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var resp = await IPCMeloaderClient.SendCommandAsync($"PIN_OBJECT|{tag.Id}", 8000);
                StatusText.Text = resp ?? "No response";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void UnpinSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = Tree.SelectedItem as TreeViewItem;
                if (selected?.Tag is not NodeTag tag)
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var resp = await IPCMeloaderClient.SendCommandAsync($"UNPIN_OBJECT|{tag.Id}", 8000);
                StatusText.Text = resp ?? "No response";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void DumpSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = Tree.SelectedItem as TreeViewItem;
                if (selected?.Tag is not NodeTag tag)
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                StatusText.Text = "Dumping...";
                var resp = await IPCMeloaderClient.SendCommandAsync($"DUMP_JSON|{tag.Id}|depth=3|maxChars=200000", 12000);
                if (resp == null || resp.StartsWith("ERROR|", StringComparison.Ordinal))
                {
                    StatusText.Text = resp ?? "No response";
                    return;
                }

                var b64Key = "|b64=";
                var idx = resp.IndexOf(b64Key, StringComparison.Ordinal);
                if (idx < 0)
                {
                    StatusText.Text = "Missing b64 payload";
                    return;
                }
                var b64 = resp.Substring(idx + b64Key.Length);
                var json = DecodeBase64Utf8(b64);
                Clipboard.SetText(json);
                StatusText.Text = "Dump copied to clipboard";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private static string DecodeBase64Utf8(string b64)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(b64))
                    return "";
                var bytes = Convert.FromBase64String(b64);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return "";
            }
        }

        private async void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            try
            {
                if (_suppressTreeSelectionChanged)
                    return;
                if (e.NewValue is not TreeViewItem item || item.Tag is not NodeTag tag)
                    return;

                _selected = tag;
                SelectedIdText.Text = tag.Id ?? string.Empty;
                SelectedTypeText.Text = string.Empty;
                NameBox.Text = string.Empty;
                ActiveBox.IsChecked = false;
                TagBox.Text = string.Empty;
                LayerBox.Text = string.Empty;
                ParentIdBox.Text = string.Empty;
                SiblingIndexBox.Text = string.Empty;
                PosXBox.Text = string.Empty;
                PosYBox.Text = string.Empty;
                PosZBox.Text = string.Empty;
                RotXBox.Text = string.Empty;
                RotYBox.Text = string.Empty;
                RotZBox.Text = string.Empty;
                ScaleXBox.Text = string.Empty;
                ScaleYBox.Text = string.Empty;
                ScaleZBox.Text = string.Empty;
                ComponentEnabledBox.IsChecked = false;

                var allow = tag.Kind == NodeKind.GameObject;
                var allowComponent = tag.Kind == NodeKind.Component;
                NameBox.IsEnabled = allow;
                ActiveBox.IsEnabled = allow;
                TagBox.IsEnabled = allow;
                LayerBox.IsEnabled = allow;
                PosXBox.IsEnabled = allow;
                PosYBox.IsEnabled = allow;
                PosZBox.IsEnabled = allow;
                RotXBox.IsEnabled = allow;
                RotYBox.IsEnabled = allow;
                RotZBox.IsEnabled = allow;
                ScaleXBox.IsEnabled = allow;
                ScaleYBox.IsEnabled = allow;
                ScaleZBox.IsEnabled = allow;
                AddComponentBox.IsEnabled = allow;
                ComponentEnabledBox.IsEnabled = allowComponent;
                ParentIdBox.IsEnabled = allow;
                SiblingIndexBox.IsEnabled = allow;
                NewNameBox.IsEnabled = allow;

                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                if (allow && int.TryParse(tag.Id, out var selectedId) && selectedId != 0)
                    GameObjectSelected?.Invoke(selectedId);

                if (allow)
                {
                    var nodeResp = await IPCMeloaderClient.SendCommandAsync($"GET_NODE_INFO|{tag.Id}", 8000);
                    if (nodeResp != null && nodeResp.StartsWith("NODE|", StringComparison.Ordinal))
                    {
                        var nkv = ParseKeyValues(nodeResp);
                        if (nkv.TryGetValue("parentId", out var pid))
                            ParentIdBox.Text = pid;
                        if (nkv.TryGetValue("siblingIndex", out var si))
                            SiblingIndexBox.Text = si;
                    }
                }

                var infoResponse = await IPCMeloaderClient.SendCommandAsync($"GET_OBJECT_INFO||{tag.Id}", 8000);
                if (infoResponse != null && infoResponse.StartsWith("OBJECT_INFO|", StringComparison.Ordinal))
                {
                    var info = ParseKeyValues(infoResponse);
                    if (info.TryGetValue("type", out var runtimeType))
                        SelectedTypeText.Text = runtimeType;
                    if (allow && info.TryGetValue("name", out var name))
                        NameBox.Text = name;
                    if (allow && info.TryGetValue("active", out var active))
                        ActiveBox.IsChecked = active == "1";
                    if (allow && info.TryGetValue("tag", out var tagValue))
                        TagBox.Text = tagValue;
                    if (allow && info.TryGetValue("layer", out var layerValue))
                        LayerBox.Text = layerValue;
                }

                if (allowComponent)
                {
                    var enabledResp = await IPCMeloaderClient.SendCommandAsync($"GET_ENABLED|{tag.Id}", 8000);
                    if (enabledResp != null && enabledResp.StartsWith("ENABLED|", StringComparison.Ordinal))
                    {
                        var kv = ParseKeyValues(enabledResp);
                        if (kv.TryGetValue("value", out var v))
                            ComponentEnabledBox.IsChecked = v == "1";
                    }
                    return;
                }

                if (!allow)
                    return;

                var tResponse = await IPCMeloaderClient.SendCommandAsync($"GET_TRANSFORM|{tag.Id}", 8000);
                if (tResponse != null && tResponse.StartsWith("TRANSFORM|", StringComparison.Ordinal))
                {
                    var kv = ParseKeyValues(tResponse);
                    if (kv.TryGetValue("localPos", out var lp))
                        SetVector3Boxes(lp, PosXBox, PosYBox, PosZBox);
                    if (kv.TryGetValue("localRot", out var lr))
                        SetVector3Boxes(lr, RotXBox, RotYBox, RotZBox);
                    if (kv.TryGetValue("localScale", out var ls))
                        SetVector3Boxes(ls, ScaleXBox, ScaleYBox, ScaleZBox);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void ApplyParent_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selected == null || _selected.Kind != NodeKind.GameObject || string.IsNullOrWhiteSpace(_selected.Id))
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var parentId = ParentIdBox.Text?.Trim() ?? "";
                var response = await IPCMeloaderClient.SendCommandAsync($"SET_PARENT|{_selected.Id}|{parentId}", 12000);
                StatusText.Text = response ?? "No response";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void Unparent_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selected == null || _selected.Kind != NodeKind.GameObject || string.IsNullOrWhiteSpace(_selected.Id))
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var response = await IPCMeloaderClient.SendCommandAsync($"SET_PARENT|{_selected.Id}|null", 12000);
                StatusText.Text = response ?? "No response";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void ApplySibling_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selected == null || _selected.Kind != NodeKind.GameObject || string.IsNullOrWhiteSpace(_selected.Id))
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var index = SiblingIndexBox.Text?.Trim() ?? "";
                var response = await IPCMeloaderClient.SendCommandAsync($"SET_SIBLING_INDEX|{_selected.Id}|{index}", 8000);
                StatusText.Text = response ?? "No response";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void CreateChild_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selected == null || _selected.Kind != NodeKind.GameObject || string.IsNullOrWhiteSpace(_selected.Id))
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var name = NewNameBox.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(name))
                    name = "New GameObject";

                var response = await IPCMeloaderClient.SendCommandAsync($"CREATE_GAMEOBJECT|{name}|{_selected.Id}", 12000);
                StatusText.Text = response ?? "No response";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void CreateRoot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var name = NewNameBox.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(name))
                    name = "New GameObject";

                var response = await IPCMeloaderClient.SendCommandAsync($"CREATE_GAMEOBJECT|{name}|null", 12000);
                StatusText.Text = response ?? "No response";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private void SetVector3Boxes(string value, TextBox x, TextBox y, TextBox z)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            var parts = value.Split(',');
            if (parts.Length >= 3)
            {
                x.Text = parts[0].Trim();
                y.Text = parts[1].Trim();
                z.Text = parts[2].Trim();
            }
        }

        private async void ApplyNameActive_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selected == null || _selected.Kind != NodeKind.GameObject || string.IsNullOrWhiteSpace(_selected.Id))
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var name = NameBox.Text ?? string.Empty;
                var active = ActiveBox.IsChecked == true ? "1" : "0";

                var r1 = await IPCMeloaderClient.SendCommandAsync($"SET_NAME|{_selected.Id}|{name}", 8000);
                var r2 = await IPCMeloaderClient.SendCommandAsync($"SET_ACTIVE|{_selected.Id}|{active}", 8000);
                StatusText.Text = r2 ?? r1 ?? "OK";

                if (Tree.SelectedItem is TreeViewItem item)
                {
                    var header = name;
                    if (active != "1")
                        header = $"{header} (inactive)";
                    item.Header = header;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void ApplyTagLayer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selected == null || _selected.Kind != NodeKind.GameObject || string.IsNullOrWhiteSpace(_selected.Id))
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var tag = TagBox.Text ?? string.Empty;
                var layer = LayerBox.Text ?? string.Empty;

                var r1 = await IPCMeloaderClient.SendCommandAsync($"SET_TAG|{_selected.Id}|{tag}", 8000);
                var r2 = await IPCMeloaderClient.SendCommandAsync($"SET_LAYER|{_selected.Id}|{layer}", 8000);
                StatusText.Text = r2 ?? r1 ?? "OK";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void ApplyTransform_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selected == null || _selected.Kind != NodeKind.GameObject || string.IsNullOrWhiteSpace(_selected.Id))
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var pos = BuildVector3(PosXBox, PosYBox, PosZBox);
                var rot = BuildVector3(RotXBox, RotYBox, RotZBox);
                var scale = BuildVector3(ScaleXBox, ScaleYBox, ScaleZBox);

                var response = await IPCMeloaderClient.SendCommandAsync($"SET_TRANSFORM_LOCAL|{_selected.Id}|{pos}|{rot}|{scale}", 8000);
                StatusText.Text = response ?? "No response";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void AddComponent_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selected == null || _selected.Kind != NodeKind.GameObject || string.IsNullOrWhiteSpace(_selected.Id))
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var typeName = AddComponentBox.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    StatusText.Text = "Missing component type";
                    return;
                }

                var response = await IPCMeloaderClient.SendCommandAsync($"ADD_COMPONENT|{_selected.Id}|{typeName}", 8000);
                StatusText.Text = response ?? "No response";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void ApplyEnabled_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selected == null || _selected.Kind != NodeKind.Component || string.IsNullOrWhiteSpace(_selected.Id))
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var enabled = ComponentEnabledBox.IsChecked == true ? "1" : "0";
                var response = await IPCMeloaderClient.SendCommandAsync($"SET_ENABLED|{_selected.Id}|{enabled}", 8000);
                StatusText.Text = response ?? "No response";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selected == null || string.IsNullOrWhiteSpace(_selected.Id))
                    return;
                if (_selected.Kind != NodeKind.GameObject && _selected.Kind != NodeKind.Component)
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var response = await IPCMeloaderClient.SendCommandAsync($"DESTROY|{_selected.Id}", 8000);
                StatusText.Text = response ?? "No response";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void Duplicate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selected == null || _selected.Kind != NodeKind.GameObject || string.IsNullOrWhiteSpace(_selected.Id))
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var response = await IPCMeloaderClient.SendCommandAsync($"DUPLICATE|{_selected.Id}", 8000);
                StatusText.Text = response ?? "No response";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void Destroy_Click(object sender, RoutedEventArgs e)
        {
            await Dispatcher.InvokeAsync(() => RemoveSelected_Click(sender, e));
        }

        private async void Despawn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selected == null || string.IsNullOrWhiteSpace(_selected.Id))
                    return;
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var response = await IPCMeloaderClient.SendCommandAsync($"DESPAWN|{_selected.Id}", 12000);
                StatusText.Text = response ?? "No response";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private string BuildVector3(TextBox x, TextBox y, TextBox z)
        {
            var xs = x.Text?.Trim();
            var ys = y.Text?.Trim();
            var zs = z.Text?.Trim();
            if (string.IsNullOrWhiteSpace(xs) || string.IsNullOrWhiteSpace(ys) || string.IsNullOrWhiteSpace(zs))
                return "null";
            return $"{xs},{ys},{zs}";
        }

        private async void Find_Click(object sender, RoutedEventArgs e)
        {
            await FindAsync();
        }

        private async Task FindAsync()
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var term = SearchBox.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(term))
                {
                    StatusText.Text = "Enter a name or id";
                    return;
                }

                StatusText.Text = "Searching...";
                var response = await IPCMeloaderClient.SendCommandAsync($"FIND_OBJECTS|UnityEngine.GameObject|{term}", 12000);
                if (response == null)
                {
                    StatusText.Text = "No response";
                    return;
                }

                if (!response.StartsWith("OBJECTS|", StringComparison.Ordinal))
                {
                    StatusText.Text = response;
                    return;
                }

                _searchResults.Clear();
                var parts = response.Split('|');
                for (var i = 1; i < parts.Length; i++)
                {
                    var p = parts[i];
                    if (string.IsNullOrWhiteSpace(p))
                        continue;
                    if (p.StartsWith("type=", StringComparison.OrdinalIgnoreCase) || p.StartsWith("count=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!p.StartsWith("id=", StringComparison.OrdinalIgnoreCase) || !p.Contains(";"))
                        continue;

                    var id = string.Empty;
                    var name = string.Empty;
                    var kvs = p.Split(';');
                    foreach (var kv in kvs)
                    {
                        var eq = kv.IndexOf('=');
                        if (eq <= 0)
                            continue;
                        var key = kv.Substring(0, eq);
                        var val = kv.Substring(eq + 1);
                        if (key.Equals("id", StringComparison.OrdinalIgnoreCase))
                            id = val;
                        else if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                            name = val;
                    }

                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    _searchResults.Add(new SearchResult
                    {
                        Id = id,
                        Name = name ?? string.Empty,
                        DisplayText = $"{name} (id={id})"
                    });

                    if (_searchResults.Count >= 200)
                        break;
                }

                ResultsList.ItemsSource = null;
                ResultsList.ItemsSource = _searchResults;
                SearchCountText.Text = $"Results: {_searchResults.Count}";
                StatusText.Text = "Ready";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            _searchResults.Clear();
            ResultsList.ItemsSource = null;
            SearchCountText.Text = string.Empty;
        }

        private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultsList.SelectedItem is SearchResult r)
                StatusText.Text = $"Selected result: {r.Id}";
        }

        private async void Jump_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ResultsList.SelectedItem is not SearchResult r)
                {
                    StatusText.Text = "Select a result";
                    return;
                }

                await JumpToAsync(r.Id);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async Task JumpToAsync(string id)
        {
            if (!IPCMeloaderClient.IsConnected)
            {
                StatusText.Text = "Not connected";
                return;
            }

            if (Tree.Items.Count == 0)
                await LoadRootsAsync();

            StatusText.Text = "Resolving path...";
            var response = await IPCMeloaderClient.SendCommandAsync($"GET_HIERARCHY_PATH|{id}", 12000);
            if (response == null)
            {
                StatusText.Text = "No response";
                return;
            }

            if (!response.StartsWith("PATH|", StringComparison.Ordinal))
            {
                StatusText.Text = response;
                return;
            }

            var kv = ParseKeyValues(response);
            if (!kv.TryGetValue("ids", out var idsString) || string.IsNullOrWhiteSpace(idsString))
            {
                StatusText.Text = "No ids in path";
                return;
            }

            var ids = idsString.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (ids.Count == 0)
            {
                StatusText.Text = "Empty path";
                return;
            }

            TreeViewItem current = null;
            for (var i = 0; i < ids.Count; i++)
            {
                var step = ids[i];
                if (current == null)
                {
                    current = FindChildItemById(Tree, step);
                }
                else
                {
                    await EnsureLoadedAsync(current);
                    current = FindChildItemById(current, step);
                }

                if (current == null)
                {
                    StatusText.Text = "Path not found in current tree";
                    return;
                }

                current.IsExpanded = true;
            }

            if (current != null)
            {
                current.IsSelected = true;
                current.BringIntoView();
                StatusText.Text = "Jumped";
            }
        }

        private TreeViewItem FindChildItemById(ItemsControl parent, string id)
        {
            if (parent == null)
                return null;
            for (var i = 0; i < parent.Items.Count; i++)
            {
                if (parent.Items[i] is TreeViewItem item && item.Tag is NodeTag tag && tag.Kind == NodeKind.GameObject && tag.Id == id)
                    return item;
            }
            return null;
        }

        private async Task EnsureLoadedAsync(TreeViewItem item)
        {
            if (item == null || item.Tag is not NodeTag tag)
                return;
            if (item.Items.Count == 1 && item.Items[0] is TreeViewItem placeholder && (placeholder.Header?.ToString() ?? "") == "Loading...")
            {
                item.Items.Clear();
                await PopulateNodeAsync(item, tag);
            }
        }

        private class SearchResult
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string DisplayText { get; set; }
        }

        private enum NodeKind
        {
            GameObject,
            Components,
            Component
        }

        private class NodeTag
        {
            public NodeKind Kind { get; set; }
            public string Id { get; set; }
            public string Name { get; set; }
        }

        private class Node
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public bool? Active { get; set; }
        }

        private class ComponentNode
        {
            public string Id { get; set; }
            public string TypeName { get; set; }
        }
    }
}
