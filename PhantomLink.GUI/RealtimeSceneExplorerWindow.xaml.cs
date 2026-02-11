using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    public partial class RealtimeSceneExplorerWindow : Window
    {
        private sealed class SceneNode
        {
            public int Id;
            public int ParentId;
            public string Name;
            public bool Active;
            public string Components;
            public Vector3D LocalPos;
            public Vector3D LocalRot;
            public Vector3D LocalScale;
            public Vector3D BoundsCenter;
            public Vector3D BoundsSize;
            public Vector3D ColliderCenter;
            public Vector3D ColliderSize;
        }

        private sealed class ComponentNode
        {
            public string Id { get; set; }
            public string TypeName { get; set; }
            public override string ToString() => TypeName ?? Id ?? "";
        }

        private sealed class FieldRow
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public string Value { get; set; }
        }

        private sealed class PropertyRow
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public bool CanRead { get; set; }
            public bool CanWrite { get; set; }
            public string Value { get; set; }
        }

        private sealed class CachedMeshEntry
        {
            public MeshGeometry3D Mesh;
            public Material Material;
            public Material BackMaterial;
            public int ApproxBytes;
            public long LastUsedTicks;
        }

        private enum GizmoMode
        {
            Move,
            Rotate,
            Scale
        }

        private enum GizmoAxis
        {
            None,
            X,
            Y,
            Z
        }

        private readonly Dictionary<int, SceneNode> _nodes = new Dictionary<int, SceneNode>();
        private readonly Dictionary<int, GeometryModel3D> _boundsModels = new Dictionary<int, GeometryModel3D>();
        private readonly Dictionary<int, GeometryModel3D> _colliderModels = new Dictionary<int, GeometryModel3D>();
        private readonly Dictionary<int, GeometryModel3D> _meshModels = new Dictionary<int, GeometryModel3D>();
        private readonly HashSet<int> _meshPending = new HashSet<int>();
        private readonly Queue<int> _meshQueue = new Queue<int>();
        private readonly Dictionary<int, CachedMeshEntry> _meshCache = new Dictionary<int, CachedMeshEntry>();
        private readonly LinkedList<int> _meshCacheLru = new LinkedList<int>();
        private readonly Dictionary<int, LinkedListNode<int>> _meshCacheNodes = new Dictionary<int, LinkedListNode<int>>();
        private int _meshCacheBytes = 0;
        private readonly Dictionary<int, Transform3D> _worldTransformCache = new Dictionary<int, Transform3D>();

        private const int MeshCacheMaxEntries = 220;
        private const int MeshCacheMaxBytes = 48 * 1024 * 1024;
        private readonly Dictionary<Model3D, int> _modelToNodeId = new Dictionary<Model3D, int>();
        private readonly Dictionary<Model3D, GizmoAxis> _modelToGizmoAxis = new Dictionary<Model3D, GizmoAxis>();

        private readonly List<ComponentNode> _components = new List<ComponentNode>();
        private readonly List<FieldRow> _fields = new List<FieldRow>();
        private readonly List<PropertyRow> _properties = new List<PropertyRow>();

        private readonly DispatcherTimer _pollTimer;
        private string _sessionIdText = "";
        private long _lastMeshScheduleTicks = 0;
        private long _lastMeshFetchTicks = 0;

        private GizmoMode _mode = GizmoMode.Move;
        private readonly HashSet<Key> _keysDown = new HashSet<Key>();
        private DateTime _lastFrameUtc = DateTime.UtcNow;

        private bool _rightMouseLooking;
        private Point _lastMouse;
        private double _yawDeg = 0;
        private double _pitchDeg = -10;

        private int _selectedId = 0;
        private string _selectedInstanceId = "";
        private string _selectedWorldPos = "";
        private string _selectedWorldRot = "";
        private string _selectedLocalScale = "";
        private GeometryModel3D _selectedMeshModel;

        private bool _dragging;
        private GizmoAxis _dragAxis = GizmoAxis.None;
        private Point _dragStartMouse;
        private string _dragStartWorldPos = "";
        private string _dragStartWorldRot = "";
        private string _dragStartLocalScale = "";

        private readonly Model3DGroup _gizmoGroup = new Model3DGroup();
        private GeometryModel3D _gizmoX;
        private GeometryModel3D _gizmoY;
        private GeometryModel3D _gizmoZ;
        private bool _suppressComponentEnabledClick;
        private bool _suppressHierarchyReveal;
        private string _selectedComponentRuntimeType;

        public RealtimeSceneExplorerWindow()
        {
            InitializeComponent();

            SceneRoot.Children.Add(_gizmoGroup);

            if (HierarchyView != null)
            {
                HierarchyView.GameObjectSelected += id =>
                {
                    if (id == 0)
                        return;
                    Dispatcher.InvokeAsync(async () =>
                    {
                        _suppressHierarchyReveal = true;
                        try
                        {
                            await SelectNodeAsync(id);
                        }
                        finally
                        {
                            _suppressHierarchyReveal = false;
                        }
                    });
                };
            }

            FieldsGrid.ItemsSource = _fields;
            PropertiesGrid.ItemsSource = _properties;
            ComponentsList.ItemsSource = _components;

            ModeText.Text = "Mode: Move (W)";
            SessionText.Text = "Starting...";
            SelectedText.Text = "None";

            Loaded += async (_, __) =>
            {
                await StartSessionAsync();
                Viewport.Focus();
                CompositionTarget.Rendering += OnRenderFrame;
            };

            Closed += async (_, __) =>
            {
                CompositionTarget.Rendering -= OnRenderFrame;
                _pollTimer?.Stop();
                await StopSessionAsync();
                ClearSceneModels();
            };

            _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };
            _pollTimer.Tick += async (_, __) => await PollAsync();

            BuildGizmoModels();
            UpdateGizmoVisibility();
        }

        private async Task StartSessionAsync()
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    SessionText.Text = "Not connected";
                    StatusText.Text = "IPC not connected";
                    return;
                }

                var resp = await IPCMeloaderClient.SendCommandAsync("SCENE_SYNC_START", 8000);
                if (resp == null || resp.StartsWith("ERROR|", StringComparison.Ordinal))
                {
                    SessionText.Text = resp ?? "No response";
                    return;
                }

                var kv = ParseKeyValues(resp);
                _sessionIdText = kv.TryGetValue("sid", out var sid) ? sid : "";
                SessionText.Text = resp;
                _pollTimer.Start();
            }
            catch (Exception ex)
            {
                SessionText.Text = $"Start failed: {ex.Message}";
            }
        }

        private async Task StopSessionAsync()
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                    return;
                await IPCMeloaderClient.SendCommandAsync("SCENE_SYNC_STOP", 8000);
            }
            catch
            {
            }
        }

        private async Task PollAsync()
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var resp = await IPCMeloaderClient.SendCommandAsync("SCENE_SYNC_POLL|128", 8000);
                if (resp == null)
                    return;
                if (resp.StartsWith("ERROR|", StringComparison.Ordinal))
                {
                    StatusText.Text = resp;
                    return;
                }
                if (!resp.StartsWith("SCENE_SYNC|", StringComparison.Ordinal))
                    return;

                var tokens = resp.Split('|');
                var applied = 0;
                for (var i = 1; i < tokens.Length; i++)
                {
                    var t = tokens[i];
                    if (string.IsNullOrWhiteSpace(t))
                        continue;
                    if (t.StartsWith("sid=", StringComparison.OrdinalIgnoreCase) ||
                        t.StartsWith("count=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!t.StartsWith("e=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ApplyEvent(t))
                        applied++;
                }

                if (applied > 0)
                {
                    _worldTransformCache.Clear();
                    if (_meshModels.Count > 0)
                    {
                        var ids = _meshModels.Keys.ToArray();
                        for (var i = 0; i < ids.Length; i++)
                        {
                            var id = ids[i];
                            if (_selectedId == id)
                                continue;
                            if (_meshModels.TryGetValue(id, out var model) && _nodes.TryGetValue(id, out var node) && node != null)
                                model.Transform = BuildMeshTransform(id, node);
                        }
                    }
                }

                if (applied > 0)
                    ViewportStatusText.Text = $"Objects: {_nodes.Count}  Updates: {applied}";

                ScheduleMeshStreaming();
                await ProcessMeshQueueAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private bool ApplyEvent(string token)
        {
            var kv = ParseSemicolonKeyValues(token);
            if (!kv.TryGetValue("e", out var kind))
                return false;
            if (!kv.TryGetValue("id", out var idText) || !int.TryParse(idText, out var id))
                return false;

            if (kind.Equals("RESET", StringComparison.OrdinalIgnoreCase) && id == 0)
            {
                ClearSceneModels();
                return true;
            }

            if (kind.Equals("DESTROY", StringComparison.OrdinalIgnoreCase))
            {
                RemoveNode(id);
                if (_selectedId == id)
                    ClearSelection();
                return true;
            }

            if (!kv.TryGetValue("parent", out var parentText) || !int.TryParse(parentText, out var parentId))
                parentId = 0;

            var name = kv.TryGetValue("name", out var n) ? n : "";
            var active = kv.TryGetValue("active", out var a) && a == "1";

            var node = _nodes.TryGetValue(id, out var existing) ? existing : new SceneNode { Id = id, LocalScale = new Vector3D(1, 1, 1) };
            node.ParentId = parentId;
            node.Name = name;
            node.Active = active;
            if (kv.TryGetValue("lp", out var lp) && TryParseVector3(lp, out var lpos))
                node.LocalPos = lpos;
            if (kv.TryGetValue("lr", out var lr) && TryParseVector3(lr, out var lrot))
                node.LocalRot = lrot;
            if (kv.TryGetValue("ls", out var ls) && TryParseVector3(ls, out var lscale))
                node.LocalScale = lscale;

            if (kv.TryGetValue("bc", out var bc) && TryParseVector3(bc, out var center))
                node.BoundsCenter = center;
            if (kv.TryGetValue("bs", out var bs) && TryParseVector3(bs, out var size))
                node.BoundsSize = size;
            if (kv.TryGetValue("cc", out var cc) && TryParseVector3(cc, out var ccenter))
                node.ColliderCenter = ccenter;
            if (kv.TryGetValue("cs", out var cs) && TryParseVector3(cs, out var csize))
                node.ColliderSize = csize;
            if (kv.TryGetValue("comps", out var comps))
                node.Components = comps;

            _nodes[id] = node;

            UpsertBoundsModel(node);
            UpsertColliderModel(node);

            if (_selectedId == id)
                SelectedText.Text = $"{id}  {node.Name}";

            return true;
        }

        private Transform3D BuildMeshTransform(int id, SceneNode node)
        {
            if (node == null)
                return Transform3D.Identity;

            if (TryGetWorldTransform(id, out var world))
                return world;

            return new TranslateTransform3D(node.BoundsCenter.X, node.BoundsCenter.Y, node.BoundsCenter.Z);
        }

        private bool TryGetWorldTransform(int id, out Transform3D transform)
        {
            if (_worldTransformCache.TryGetValue(id, out transform))
                return transform != null;

            var stack = new HashSet<int>();
            return TryGetWorldTransformInner(id, stack, out transform);
        }

        private bool TryGetWorldTransformInner(int id, HashSet<int> stack, out Transform3D transform)
        {
            if (_worldTransformCache.TryGetValue(id, out transform))
                return transform != null;

            if (!_nodes.TryGetValue(id, out var node) || node == null)
            {
                transform = null;
                _worldTransformCache[id] = null;
                return false;
            }

            if (stack.Contains(id))
            {
                transform = null;
                _worldTransformCache[id] = null;
                return false;
            }

            stack.Add(id);

            var local = BuildLocalTransform(node);
            if (node.ParentId != 0 && TryGetWorldTransformInner(node.ParentId, stack, out var parent) && parent != null)
            {
                var g = new Transform3DGroup();
                g.Children.Add(local);
                g.Children.Add(parent);
                transform = g;
            }
            else
            {
                transform = local;
            }

            stack.Remove(id);
            _worldTransformCache[id] = transform;
            return transform != null;
        }

        private static Transform3D BuildLocalTransform(SceneNode node)
        {
            if (node == null)
                return Transform3D.Identity;

            var pos = node.LocalPos;
            var rot = node.LocalRot;
            var scale = node.LocalScale;

            if (double.IsNaN(scale.X) || double.IsNaN(scale.Y) || double.IsNaN(scale.Z) ||
                (Math.Abs(scale.X) < 0.0000001 && Math.Abs(scale.Y) < 0.0000001 && Math.Abs(scale.Z) < 0.0000001))
                scale = new Vector3D(1, 1, 1);

            if (double.IsNaN(pos.X) || double.IsNaN(pos.Y) || double.IsNaN(pos.Z))
                pos = new Vector3D(0, 0, 0);

            if (double.IsNaN(rot.X) || double.IsNaN(rot.Y) || double.IsNaN(rot.Z))
                rot = new Vector3D(0, 0, 0);

            var tg = new Transform3DGroup();
            tg.Children.Add(new ScaleTransform3D(scale.X, scale.Y, scale.Z));
            if (Math.Abs(rot.Z) > 0.0001)
                tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), rot.Z)));
            if (Math.Abs(rot.X) > 0.0001)
                tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), rot.X)));
            if (Math.Abs(rot.Y) > 0.0001)
                tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), rot.Y)));
            tg.Children.Add(new TranslateTransform3D(pos.X, pos.Y, pos.Z));
            return tg;
        }

        private void UpsertBoundsModel(SceneNode node)
        {
            if (!_boundsModels.TryGetValue(node.Id, out var model))
            {
                model = CreateBoxModel(node.Active ? Color.FromArgb(45, 90, 150, 255) : Color.FromArgb(35, 140, 140, 140),
                    node.Active ? Color.FromArgb(140, 90, 150, 255) : Color.FromArgb(120, 140, 140, 140));

                _boundsModels[node.Id] = model;
                _modelToNodeId[model] = node.Id;
                SceneRoot.Children.Add(model);
            }

            var size = node.BoundsSize;
            if (size.X <= 0 || size.Y <= 0 || size.Z <= 0)
                size = new Vector3D(0.2, 0.2, 0.2);

            var center = node.BoundsCenter;
            if (double.IsNaN(center.X) || double.IsNaN(center.Y) || double.IsNaN(center.Z))
                center = new Vector3D(0, 0, 0);

            var tg = new Transform3DGroup();
            tg.Children.Add(new ScaleTransform3D(size.X, size.Y, size.Z));
            tg.Children.Add(new TranslateTransform3D(center.X, center.Y, center.Z));
            model.Transform = tg;

            if (_selectedId == node.Id)
                SetSelectedMaterial(model, selected: true);
            else
                SetSelectedMaterial(model, selected: false, active: node.Active);
        }

        private void UpsertColliderModel(SceneNode node)
        {
            var hasColliderSize = node.ColliderSize.X > 0 && node.ColliderSize.Y > 0 && node.ColliderSize.Z > 0;
            if (!hasColliderSize)
            {
                if (_colliderModels.TryGetValue(node.Id, out var existing))
                {
                    _colliderModels.Remove(node.Id);
                    _modelToNodeId.Remove(existing);
                    SceneRoot.Children.Remove(existing);
                }
                return;
            }

            if (!_colliderModels.TryGetValue(node.Id, out var model))
            {
                model = CreateBoxModel(Color.FromArgb(18, 255, 150, 60), Color.FromArgb(80, 255, 150, 60));
                _colliderModels[node.Id] = model;
                _modelToNodeId[model] = node.Id;
                SceneRoot.Children.Add(model);
            }

            var size = node.ColliderSize;
            var center = node.ColliderCenter;

            var tg = new Transform3DGroup();
            tg.Children.Add(new ScaleTransform3D(size.X, size.Y, size.Z));
            tg.Children.Add(new TranslateTransform3D(center.X, center.Y, center.Z));
            model.Transform = tg;
        }

        private void RemoveMeshModel(int id)
        {
            if (_meshModels.TryGetValue(id, out var model))
            {
                _meshModels.Remove(id);
                _modelToNodeId.Remove(model);
                SceneRoot.Children.Remove(model);
            }
            _meshPending.Remove(id);
        }

        private void RemoveMeshCache(int id)
        {
            if (_meshCache.TryGetValue(id, out var entry))
            {
                _meshCache.Remove(id);
                _meshCacheBytes -= entry.ApproxBytes;
            }

            if (_meshCacheNodes.TryGetValue(id, out var node))
            {
                _meshCacheNodes.Remove(id);
                try { _meshCacheLru.Remove(node); } catch { }
            }
        }

        private void TouchMeshCache(int id)
        {
            if (_meshCache.TryGetValue(id, out var entry))
                entry.LastUsedTicks = Stopwatch.GetTimestamp();

            if (_meshCacheNodes.TryGetValue(id, out var node))
            {
                try
                {
                    _meshCacheLru.Remove(node);
                    _meshCacheLru.AddFirst(node);
                }
                catch
                {
                }
                return;
            }

            var newNode = new LinkedListNode<int>(id);
            _meshCacheNodes[id] = newNode;
            _meshCacheLru.AddFirst(newNode);
        }

        private void EnforceMeshCacheLimits()
        {
            while (_meshCache.Count > MeshCacheMaxEntries || _meshCacheBytes > MeshCacheMaxBytes)
            {
                var tail = _meshCacheLru.Last;
                if (tail == null)
                    break;
                var id = tail.Value;
                _meshCacheLru.RemoveLast();
                _meshCacheNodes.Remove(id);
                if (_meshCache.TryGetValue(id, out var entry))
                {
                    _meshCache.Remove(id);
                    _meshCacheBytes -= entry.ApproxBytes;
                }
            }
        }

        private void UpsertMeshCache(int id, MeshGeometry3D mesh, Material material, Material backMaterial, int vcount, int icount)
        {
            if (mesh == null || material == null)
                return;

            var approxBytes = checked((vcount * 3 * 4) + (icount * 4));

            RemoveMeshCache(id);

            if (mesh.CanFreeze)
                mesh.Freeze();
            if (material.CanFreeze)
                material.Freeze();
            if (backMaterial != null && backMaterial.CanFreeze)
                backMaterial.Freeze();

            _meshCache[id] = new CachedMeshEntry
            {
                Mesh = mesh,
                Material = material,
                BackMaterial = backMaterial,
                ApproxBytes = approxBytes,
                LastUsedTicks = Stopwatch.GetTimestamp()
            };
            _meshCacheBytes += approxBytes;
            TouchMeshCache(id);
            EnforceMeshCacheLimits();
        }

        private bool TryAddMeshModelFromCache(int id, SceneNode node)
        {
            if (_meshModels.ContainsKey(id))
                return true;
            if (!_meshCache.TryGetValue(id, out var entry) || entry == null || entry.Mesh == null || entry.Material == null)
                return false;

            var model = new GeometryModel3D(entry.Mesh, entry.Material) { BackMaterial = entry.BackMaterial ?? entry.Material };
            model.Transform = BuildMeshTransform(id, node);

            RemoveMeshModel(id);
            _meshModels[id] = model;
            _modelToNodeId[model] = id;
            SceneRoot.Children.Add(model);
            TouchMeshCache(id);
            return true;
        }

        private void ClearSceneModels()
        {
            _nodes.Clear();
            foreach (var kv in _boundsModels)
                SceneRoot.Children.Remove(kv.Value);
            _boundsModels.Clear();
            foreach (var kv in _colliderModels)
                SceneRoot.Children.Remove(kv.Value);
            _colliderModels.Clear();
            foreach (var kv in _meshModels)
                SceneRoot.Children.Remove(kv.Value);
            _meshModels.Clear();
            _meshQueue.Clear();
            _meshPending.Clear();
            _meshCache.Clear();
            _meshCacheLru.Clear();
            _meshCacheNodes.Clear();
            _meshCacheBytes = 0;
            _modelToNodeId.Clear();
            ClearSelection();
        }

        private static bool NodeLikelyHasMesh(SceneNode node)
        {
            if (node == null || string.IsNullOrWhiteSpace(node.Components))
                return false;
            return node.Components.IndexOf("MeshFilter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   node.Components.IndexOf("SkinnedMeshRenderer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ScheduleMeshStreaming()
        {
            if (!IPCMeloaderClient.IsConnected)
                return;

            var now = Stopwatch.GetTimestamp();
            if (now - _lastMeshScheduleTicks < Stopwatch.Frequency / 2)
                return;
            _lastMeshScheduleTicks = now;

            var cam = Camera.Position;
            var camV = new Vector3D(cam.X, cam.Y, cam.Z);

            var candidates = _nodes.Values
                .Where(n => n.Active && NodeLikelyHasMesh(n))
                .Select(n => new
                {
                    Id = n.Id,
                    Dist = (new Vector3D(n.BoundsCenter.X - camV.X, n.BoundsCenter.Y - camV.Y, n.BoundsCenter.Z - camV.Z)).Length
                })
                .OrderBy(x => x.Dist)
                .Take(80)
                .Select(x => x.Id)
                .ToHashSet();

            if (_selectedId != 0)
                candidates.Remove(_selectedId);

            foreach (var id in candidates)
            {
                if (_meshModels.ContainsKey(id))
                    continue;
                if (_meshPending.Contains(id))
                    continue;
                if (_nodes.TryGetValue(id, out var cachedNode) && cachedNode != null && TryAddMeshModelFromCache(id, cachedNode))
                    continue;
                _meshQueue.Enqueue(id);
                _meshPending.Add(id);
            }

            if (_meshModels.Count > 0)
            {
                var existing = _meshModels.Keys.ToArray();
                for (var i = 0; i < existing.Length; i++)
                {
                    var id = existing[i];
                    if (!candidates.Contains(id))
                        RemoveMeshModel(id);
                }
            }
        }

        private async Task ProcessMeshQueueAsync()
        {
            if (_meshQueue.Count == 0)
                return;

            var now = Stopwatch.GetTimestamp();
            if (now - _lastMeshFetchTicks < Stopwatch.Frequency / 15)
                return;
            _lastMeshFetchTicks = now;

            var id = _meshQueue.Dequeue();
            _meshPending.Remove(id);

            if (id == _selectedId)
                return;
            if (!_nodes.TryGetValue(id, out var node) || node == null)
                return;
            if (!NodeLikelyHasMesh(node))
                return;
            if (TryAddMeshModelFromCache(id, node))
                return;

            var resp = await IPCMeloaderClient.SendCommandAsync($"SCENE_GET_MESH|{id}|1200|7200", 8000);
            if (resp == null || resp.StartsWith("ERROR|", StringComparison.Ordinal) || !resp.StartsWith("MESH|", StringComparison.Ordinal))
                return;

            var kv = ParseKeyValues(resp);
            if (kv.TryGetValue("tooLarge", out var tl) && tl == "1")
                return;

            if (!kv.TryGetValue("vcount", out var vcText) || !int.TryParse(vcText, out var vcount) || vcount <= 0)
                return;
            if (!kv.TryGetValue("icount", out var icText) || !int.TryParse(icText, out var icount) || icount <= 0)
                return;
            if (!kv.TryGetValue("vb64", out var vb64) || string.IsNullOrWhiteSpace(vb64))
                return;
            if (!kv.TryGetValue("ib64", out var ib64) || string.IsNullOrWhiteSpace(ib64))
                return;

            var vb = Convert.FromBase64String(vb64);
            var ib = Convert.FromBase64String(ib64);
            var floats = new float[vb.Length / 4];
            Buffer.BlockCopy(vb, 0, floats, 0, vb.Length);
            var indices = new int[ib.Length / 4];
            Buffer.BlockCopy(ib, 0, indices, 0, ib.Length);
            if (floats.Length < vcount * 3 || indices.Length < icount)
                return;

            var mesh = new MeshGeometry3D();
            for (var i = 0; i < vcount; i++)
            {
                var o = i * 3;
                mesh.Positions.Add(new Point3D(floats[o], floats[o + 1], floats[o + 2]));
            }
            for (var i = 0; i < icount; i++)
                mesh.TriangleIndices.Add(indices[i]);

            var fill = Color.FromArgb(28, 220, 220, 220);
            var brush = new SolidColorBrush(fill) { Opacity = fill.A / 255.0 };
            if (brush.CanFreeze)
                brush.Freeze();
            var mat = new DiffuseMaterial(brush);
            UpsertMeshCache(id, mesh, mat, mat, vcount, icount);
            if (!TryAddMeshModelFromCache(id, node))
                return;
        }

        private void RemoveNode(int id)
        {
            _nodes.Remove(id);
            _worldTransformCache.Remove(id);
            if (_boundsModels.TryGetValue(id, out var model))
            {
                _boundsModels.Remove(id);
                _modelToNodeId.Remove(model);
                SceneRoot.Children.Remove(model);
            }
            if (_colliderModels.TryGetValue(id, out var cmodel))
            {
                _colliderModels.Remove(id);
                _modelToNodeId.Remove(cmodel);
                SceneRoot.Children.Remove(cmodel);
            }
            RemoveMeshModel(id);
            RemoveMeshCache(id);
        }

        private static GeometryModel3D CreateBoxModel(Color fill, Color edge)
        {
            var mesh = new MeshGeometry3D();
            var p = new[]
            {
                new Point3D(-0.5,-0.5,-0.5),
                new Point3D( 0.5,-0.5,-0.5),
                new Point3D( 0.5, 0.5,-0.5),
                new Point3D(-0.5, 0.5,-0.5),
                new Point3D(-0.5,-0.5, 0.5),
                new Point3D( 0.5,-0.5, 0.5),
                new Point3D( 0.5, 0.5, 0.5),
                new Point3D(-0.5, 0.5, 0.5),
            };
            foreach (var pt in p)
                mesh.Positions.Add(pt);

            void AddTri(int a, int b, int c)
            {
                mesh.TriangleIndices.Add(a);
                mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(c);
            }

            AddTri(0, 2, 1);
            AddTri(0, 3, 2);

            AddTri(4, 5, 6);
            AddTri(4, 6, 7);

            AddTri(0, 1, 5);
            AddTri(0, 5, 4);

            AddTri(2, 3, 7);
            AddTri(2, 7, 6);

            AddTri(0, 4, 7);
            AddTri(0, 7, 3);

            AddTri(1, 2, 6);
            AddTri(1, 6, 5);

            var mat = new DiffuseMaterial(new SolidColorBrush(fill));
            mat.Brush.Opacity = fill.A / 255.0;
            var back = new DiffuseMaterial(new SolidColorBrush(fill));
            back.Brush.Opacity = fill.A / 255.0;

            return new GeometryModel3D(mesh, mat) { BackMaterial = back };
        }

        private static void SetSelectedMaterial(GeometryModel3D model, bool selected, bool active = true)
        {
            if (model == null)
                return;

            if (selected)
            {
                var fill = Color.FromArgb(80, 255, 210, 80);
                var mat = new DiffuseMaterial(new SolidColorBrush(fill));
                mat.Brush.Opacity = fill.A / 255.0;
                model.Material = mat;
                model.BackMaterial = mat;
                return;
            }

            var c = active ? Color.FromArgb(45, 90, 150, 255) : Color.FromArgb(35, 140, 140, 140);
            var m = new DiffuseMaterial(new SolidColorBrush(c));
            m.Brush.Opacity = c.A / 255.0;
            model.Material = m;
            model.BackMaterial = m;
        }

        private void BuildGizmoModels()
        {
            _gizmoGroup.Children.Clear();
            _modelToGizmoAxis.Clear();

            _gizmoX = CreateAxisModel(Colors.Red);
            _gizmoY = CreateAxisModel(Colors.LimeGreen);
            _gizmoZ = CreateAxisModel(Colors.DeepSkyBlue);

            _gizmoX.Transform = new Transform3DGroup
            {
                Children = new Transform3DCollection
                {
                    new ScaleTransform3D(1.4, 0.04, 0.04),
                    new TranslateTransform3D(0.7, 0, 0),
                }
            };
            _gizmoY.Transform = new Transform3DGroup
            {
                Children = new Transform3DCollection
                {
                    new ScaleTransform3D(0.04, 1.4, 0.04),
                    new TranslateTransform3D(0, 0.7, 0),
                }
            };
            _gizmoZ.Transform = new Transform3DGroup
            {
                Children = new Transform3DCollection
                {
                    new ScaleTransform3D(0.04, 0.04, 1.4),
                    new TranslateTransform3D(0, 0, 0.7),
                }
            };

            _gizmoGroup.Children.Add(_gizmoX);
            _gizmoGroup.Children.Add(_gizmoY);
            _gizmoGroup.Children.Add(_gizmoZ);

            _modelToGizmoAxis[_gizmoX] = GizmoAxis.X;
            _modelToGizmoAxis[_gizmoY] = GizmoAxis.Y;
            _modelToGizmoAxis[_gizmoZ] = GizmoAxis.Z;
        }

        private static GeometryModel3D CreateAxisModel(Color color)
        {
            var fill = Color.FromArgb(170, color.R, color.G, color.B);
            var mat = new DiffuseMaterial(new SolidColorBrush(fill));
            mat.Brush.Opacity = fill.A / 255.0;
            var m = CreateBoxModel(fill, fill);
            m.Material = mat;
            m.BackMaterial = mat;
            return m;
        }

        private void UpdateGizmoVisibility()
        {
            _gizmoGroup.Transform = new TranslateTransform3D(0, 0, 0);
            _gizmoGroup.Children.Clear();
            if (_selectedId == 0)
                return;

            _gizmoGroup.Children.Add(_gizmoX);
            _gizmoGroup.Children.Add(_gizmoY);
            _gizmoGroup.Children.Add(_gizmoZ);

            if (TryParseVector3(_selectedWorldPos, out var wp))
            {
                _gizmoGroup.Transform = new TranslateTransform3D(wp.X, wp.Y, wp.Z);
                return;
            }

            if (_nodes.TryGetValue(_selectedId, out var node))
                _gizmoGroup.Transform = new TranslateTransform3D(node.BoundsCenter.X, node.BoundsCenter.Y, node.BoundsCenter.Z);
        }

        private void ClearSelection()
        {
            if (_selectedId != 0 && _boundsModels.TryGetValue(_selectedId, out var prevModel))
                SetSelectedMaterial(prevModel, selected: false, active: _nodes.TryGetValue(_selectedId, out var n) && n.Active);

            _selectedId = 0;
            _selectedInstanceId = "";
            _selectedWorldPos = "";
            _selectedWorldRot = "";
            _selectedLocalScale = "";
            RemoveSelectedMeshModel();
            SelectedText.Text = "None";
            WorldPosBox.Text = "";
            WorldRotBox.Text = "";
            LocalScaleBox.Text = "";
            TransformStatus.Text = "";
            ComponentEnabledStatus.Text = "";
            _suppressComponentEnabledClick = true;
            ComponentEnabledCheckBox.IsChecked = false;
            ComponentEnabledCheckBox.IsEnabled = false;
            _suppressComponentEnabledClick = false;
            _components.Clear();
            _fields.Clear();
            _properties.Clear();
            ComponentsList.Items.Refresh();
            FieldsGrid.Items.Refresh();
            PropertiesGrid.Items.Refresh();
            UpdateGizmoVisibility();
        }

        private async Task SelectNodeAsync(int id)
        {
            if (id == _selectedId)
                return;
            if (_selectedId != 0 && _selectedId != id && _boundsModels.TryGetValue(_selectedId, out var prevModel))
                SetSelectedMaterial(prevModel, selected: false, active: _nodes.TryGetValue(_selectedId, out var pn) && pn.Active);

            _selectedId = id;
            _selectedInstanceId = id.ToString(CultureInfo.InvariantCulture);
            if (_boundsModels.TryGetValue(id, out var model))
                SetSelectedMaterial(model, selected: true);

            if (_nodes.TryGetValue(id, out var node))
                SelectedText.Text = $"{id}  {node.Name}";
            else
                SelectedText.Text = $"{id}";

            UpdateGizmoVisibility();
            if (!_suppressHierarchyReveal && HierarchyView != null)
                await HierarchyView.RevealGameObjectAsync(id);
            await RefreshSelectionAsync();
        }

        private async Task RefreshSelectionAsync()
        {
            try
            {
                if (_selectedId == 0 || !IPCMeloaderClient.IsConnected)
                    return;

                var componentsResp = await IPCMeloaderClient.SendCommandAsync($"GET_COMPONENTS|{_selectedInstanceId}", 8000);
                _components.Clear();
                if (componentsResp != null && componentsResp.StartsWith("COMPONENTS|", StringComparison.Ordinal))
                {
                    var parts = componentsResp.Split('|');
                    for (var i = 1; i < parts.Length; i++)
                    {
                        var p = parts[i];
                        if (string.IsNullOrWhiteSpace(p))
                            continue;
                        if (p.StartsWith("id=", StringComparison.OrdinalIgnoreCase) && !p.Contains(";"))
                            continue;
                        if (p.StartsWith("count=", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!p.StartsWith("id=", StringComparison.OrdinalIgnoreCase) || !p.Contains(";"))
                            continue;
                        var kvs = p.Split(';');
                        var cid = "";
                        var ctype = "";
                        foreach (var kv in kvs)
                        {
                            var eq = kv.IndexOf('=');
                            if (eq <= 0)
                                continue;
                            var key = kv.Substring(0, eq);
                            var val = kv.Substring(eq + 1);
                            if (key.Equals("id", StringComparison.OrdinalIgnoreCase))
                                cid = val;
                            else if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
                                ctype = val;
                        }
                        if (!string.IsNullOrWhiteSpace(cid))
                            _components.Add(new ComponentNode { Id = cid, TypeName = ctype });
                    }
                }
                ComponentsList.Items.Refresh();

                var tResp = await IPCMeloaderClient.SendCommandAsync($"GET_TRANSFORM|{_selectedInstanceId}", 8000);
                if (tResp != null && tResp.StartsWith("TRANSFORM|", StringComparison.Ordinal))
                {
                    var kv = ParseKeyValues(tResp);
                    _selectedWorldPos = kv.TryGetValue("pos", out var p) ? p : "";
                    _selectedWorldRot = kv.TryGetValue("rot", out var r) ? r : "";
                    _selectedLocalScale = kv.TryGetValue("localScale", out var s) ? s : "";
                }

                WorldPosBox.Text = _selectedWorldPos;
                WorldRotBox.Text = _selectedWorldRot;
                LocalScaleBox.Text = _selectedLocalScale;
                TransformStatus.Text = "";
                UpdateGizmoVisibility();

                await UpdateSelectedMeshAsync();
                StatusText.Text = $"Selected {SelectedText.Text}";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private void RemoveSelectedMeshModel()
        {
            if (_selectedMeshModel == null)
                return;
            try
            {
                _modelToNodeId.Remove(_selectedMeshModel);
                SceneRoot.Children.Remove(_selectedMeshModel);
            }
            catch
            {
            }
            _selectedMeshModel = null;
        }

        private async Task UpdateSelectedMeshAsync()
        {
            try
            {
                if (_selectedId == 0 || !IPCMeloaderClient.IsConnected)
                {
                    RemoveSelectedMeshModel();
                    return;
                }

                RemoveMeshModel(_selectedId);
                var resp = await IPCMeloaderClient.SendCommandAsync($"SCENE_GET_MESH|{_selectedInstanceId}|2000|12000", 8000);
                if (resp == null || resp.StartsWith("ERROR|", StringComparison.Ordinal))
                {
                    RemoveSelectedMeshModel();
                    return;
                }

                if (!resp.StartsWith("MESH|", StringComparison.Ordinal))
                {
                    RemoveSelectedMeshModel();
                    return;
                }

                var kv = ParseKeyValues(resp);
                if (kv.TryGetValue("tooLarge", out var tl) && tl == "1")
                {
                    RemoveSelectedMeshModel();
                    return;
                }

                if (!kv.TryGetValue("vcount", out var vcText) || !int.TryParse(vcText, out var vcount) || vcount <= 0)
                {
                    RemoveSelectedMeshModel();
                    return;
                }

                if (!kv.TryGetValue("icount", out var icText) || !int.TryParse(icText, out var icount) || icount <= 0)
                {
                    RemoveSelectedMeshModel();
                    return;
                }

                if (!kv.TryGetValue("vb64", out var vb64) || string.IsNullOrWhiteSpace(vb64) ||
                    !kv.TryGetValue("ib64", out var ib64) || string.IsNullOrWhiteSpace(ib64))
                {
                    RemoveSelectedMeshModel();
                    return;
                }

                var vb = Convert.FromBase64String(vb64);
                var ib = Convert.FromBase64String(ib64);

                var floats = new float[vb.Length / 4];
                Buffer.BlockCopy(vb, 0, floats, 0, vb.Length);
                var indices = new int[ib.Length / 4];
                Buffer.BlockCopy(ib, 0, indices, 0, ib.Length);

                if (floats.Length < vcount * 3 || indices.Length < icount)
                {
                    RemoveSelectedMeshModel();
                    return;
                }

                var mesh = new MeshGeometry3D();
                for (var i = 0; i < vcount; i++)
                {
                    var o = i * 3;
                    mesh.Positions.Add(new Point3D(floats[o], floats[o + 1], floats[o + 2]));
                }

                for (var i = 0; i < icount; i++)
                    mesh.TriangleIndices.Add(indices[i]);

                var fill = Color.FromArgb(60, 210, 210, 210);
                var mg = new MaterialGroup();
                mg.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(55, 210, 210, 210))));
                var dm = new DiffuseMaterial(new SolidColorBrush(fill));
                dm.Brush.Opacity = fill.A / 255.0;
                mg.Children.Add(dm);

                var model = new GeometryModel3D(mesh, mg) { BackMaterial = mg };
                model.Transform = BuildSelectedTransform();

                RemoveSelectedMeshModel();
                _selectedMeshModel = model;
                _modelToNodeId[model] = _selectedId;
                SceneRoot.Children.Add(model);
            }
            catch
            {
                RemoveSelectedMeshModel();
            }
        }

        private Transform3D BuildSelectedTransform()
        {
            var tg = new Transform3DGroup();

            if (TryParseVector3(_selectedLocalScale, out var scale))
                tg.Children.Add(new ScaleTransform3D(scale.X, scale.Y, scale.Z));

            if (TryParseVector3(_selectedWorldRot, out var rotDeg))
            {
                tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), rotDeg.X)));
                tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), rotDeg.Y)));
                tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), rotDeg.Z)));
            }

            if (TryParseVector3(_selectedWorldPos, out var pos))
                tg.Children.Add(new TranslateTransform3D(pos.X, pos.Y, pos.Z));

            return tg;
        }

        private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Viewport.Focus();
            var pos = e.GetPosition(Viewport);
            _lastMouse = pos;

            if (e.ChangedButton == MouseButton.Right)
            {
                _rightMouseLooking = true;
                Mouse.Capture(Viewport);
                return;
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                var hitModel = HitTestModel(pos);
                if (hitModel != null && _modelToGizmoAxis.TryGetValue(hitModel, out var axis))
                {
                    _dragging = true;
                    _dragAxis = axis;
                    _dragStartMouse = pos;
                    _dragStartWorldPos = _selectedWorldPos;
                    _dragStartWorldRot = _selectedWorldRot;
                    _dragStartLocalScale = _selectedLocalScale;
                    Mouse.Capture(Viewport);
                    return;
                }

                if (hitModel != null && _modelToNodeId.TryGetValue(hitModel, out var id))
                {
                    _ = SelectNodeAsync(id);
                    return;
                }
            }
        }

        private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Right)
                _rightMouseLooking = false;
            if (e.ChangedButton == MouseButton.Left)
            {
                _dragging = false;
                _dragAxis = GizmoAxis.None;
            }
            if (!_rightMouseLooking && !_dragging)
                Mouse.Capture(null);
        }

        private void Viewport_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(Viewport);
            var dx = pos.X - _lastMouse.X;
            var dy = pos.Y - _lastMouse.Y;
            _lastMouse = pos;

            if (_rightMouseLooking)
            {
                _yawDeg += dx * 0.25;
                _pitchDeg -= dy * 0.25;
                _pitchDeg = Math.Max(-89, Math.Min(89, _pitchDeg));
                UpdateCameraFromYawPitch();
                return;
            }

            if (_dragging && _selectedId != 0)
            {
                _ = ApplyDragAsync(pos);
                return;
            }
        }

        private async Task ApplyDragAsync(Point mouse)
        {
            try
            {
                if (_dragAxis == GizmoAxis.None)
                    return;
                if (!IPCMeloaderClient.IsConnected)
                    return;

                var dx = mouse.X - _dragStartMouse.X;
                var dy = mouse.Y - _dragStartMouse.Y;

                var camPos = Camera.Position;
                var pivot = _nodes.TryGetValue(_selectedId, out var node) ? node.BoundsCenter : new Vector3D(0, 0, 0);
                var dist = (new Vector3D(camPos.X - pivot.X, camPos.Y - pivot.Y, camPos.Z - pivot.Z)).Length;
                if (dist < 0.5)
                    dist = 0.5;

                var amount = (dx - dy) * dist * 0.002;
                if (_mode == GizmoMode.Rotate)
                    amount = (dx - dy) * 0.25;
                if (_mode == GizmoMode.Scale)
                    amount = (dx - dy) * 0.004;

                if (_mode == GizmoMode.Move)
                {
                    if (!TryParseVector3(_dragStartWorldPos, out var startWorld))
                        return;
                    var delta = AxisVector(_dragAxis) * amount;
                    var next = startWorld + delta;
                    var posText = FormatVector3(next);
                    var resp = await IPCMeloaderClient.SendCommandAsync($"SET_TRANSFORM_WORLD|{_selectedInstanceId}|{posText}|null|null", 8000);
                    StatusText.Text = resp ?? "";
                    _selectedWorldPos = posText;
                    WorldPosBox.Text = posText;
                    if (_selectedMeshModel != null)
                        _selectedMeshModel.Transform = BuildSelectedTransform();
                    UpdateGizmoVisibility();
                }
                else if (_mode == GizmoMode.Rotate)
                {
                    if (!TryParseVector3(_dragStartWorldRot, out var startRot))
                        return;
                    var next = startRot;
                    if (_dragAxis == GizmoAxis.X) next.X += amount;
                    if (_dragAxis == GizmoAxis.Y) next.Y += amount;
                    if (_dragAxis == GizmoAxis.Z) next.Z += amount;
                    var rotText = FormatVector3(next);
                    var resp = await IPCMeloaderClient.SendCommandAsync($"SET_TRANSFORM_WORLD|{_selectedInstanceId}|null|{rotText}|null", 8000);
                    StatusText.Text = resp ?? "";
                    _selectedWorldRot = rotText;
                    WorldRotBox.Text = rotText;
                    if (_selectedMeshModel != null)
                        _selectedMeshModel.Transform = BuildSelectedTransform();
                }
                else if (_mode == GizmoMode.Scale)
                {
                    if (!TryParseVector3(_dragStartLocalScale, out var startScale))
                        startScale = new Vector3D(1, 1, 1);
                    var next = startScale;
                    if (_dragAxis == GizmoAxis.X) next.X = Math.Max(0.001, next.X * (1 + amount));
                    if (_dragAxis == GizmoAxis.Y) next.Y = Math.Max(0.001, next.Y * (1 + amount));
                    if (_dragAxis == GizmoAxis.Z) next.Z = Math.Max(0.001, next.Z * (1 + amount));
                    var scaleText = FormatVector3(next);
                    var resp = await IPCMeloaderClient.SendCommandAsync($"SET_TRANSFORM_WORLD|{_selectedInstanceId}|null|null|{scaleText}", 8000);
                    StatusText.Text = resp ?? "";
                    _selectedLocalScale = scaleText;
                    LocalScaleBox.Text = scaleText;
                    if (_selectedMeshModel != null)
                        _selectedMeshModel.Transform = BuildSelectedTransform();
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private static Vector3D AxisVector(GizmoAxis axis)
        {
            return axis switch
            {
                GizmoAxis.X => new Vector3D(1, 0, 0),
                GizmoAxis.Y => new Vector3D(0, 1, 0),
                GizmoAxis.Z => new Vector3D(0, 0, 1),
                _ => new Vector3D(0, 0, 0)
            };
        }

        private Model3D HitTestModel(Point p)
        {
            Model3D hit = null;
            VisualTreeHelper.HitTest(Viewport, null, r =>
            {
                if (r is RayMeshGeometry3DHitTestResult rr)
                {
                    hit = rr.ModelHit;
                    return HitTestResultBehavior.Stop;
                }
                return HitTestResultBehavior.Continue;
            }, new PointHitTestParameters(p));
            return hit;
        }

        private void OnRenderFrame(object sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            var dt = (now - _lastFrameUtc).TotalSeconds;
            _lastFrameUtc = now;
            if (dt <= 0 || dt > 0.2)
                dt = 0.016;

            var speed = 6.0;
            if (_keysDown.Contains(Key.LeftShift) || _keysDown.Contains(Key.RightShift))
                speed = 18.0;

            var move = new Vector3D(0, 0, 0);
            if (_keysDown.Contains(Key.W)) move += GetCameraForward();
            if (_keysDown.Contains(Key.S)) move -= GetCameraForward();
            if (_keysDown.Contains(Key.A)) move -= GetCameraRight();
            if (_keysDown.Contains(Key.D)) move += GetCameraRight();
            if (_keysDown.Contains(Key.Q)) move -= new Vector3D(0, 1, 0);
            if (_keysDown.Contains(Key.E)) move += new Vector3D(0, 1, 0);

            if (move.Length > 0.0001)
            {
                move.Normalize();
                move *= speed * dt;
                Camera.Position = new Point3D(Camera.Position.X + move.X, Camera.Position.Y + move.Y, Camera.Position.Z + move.Z);
            }
        }

        private Vector3D GetCameraForward()
        {
            var d = Camera.LookDirection;
            var v = new Vector3D(d.X, d.Y, d.Z);
            if (v.Length > 0.0001)
                v.Normalize();
            return v;
        }

        private Vector3D GetCameraRight()
        {
            var f = GetCameraForward();
            var up = new Vector3D(0, 1, 0);
            var r = Vector3D.CrossProduct(f, up);
            if (r.Length > 0.0001)
                r.Normalize();
            return r;
        }

        private void UpdateCameraFromYawPitch()
        {
            var yaw = _yawDeg * Math.PI / 180.0;
            var pitch = _pitchDeg * Math.PI / 180.0;
            var x = Math.Cos(pitch) * Math.Sin(yaw);
            var y = Math.Sin(pitch);
            var z = Math.Cos(pitch) * Math.Cos(yaw);
            Camera.LookDirection = new Vector3D(x, y, z);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            _keysDown.Add(e.Key);
            if (e.Key == Key.W)
            {
                _mode = GizmoMode.Move;
                ModeText.Text = "Mode: Move (W)";
            }
            else if (e.Key == Key.E)
            {
                _mode = GizmoMode.Rotate;
                ModeText.Text = "Mode: Rotate (E)";
            }
            else if (e.Key == Key.R)
            {
                _mode = GizmoMode.Scale;
                ModeText.Text = "Mode: Scale (R)";
            }
        }

        private void Window_KeyUp(object sender, KeyEventArgs e)
        {
            _keysDown.Remove(e.Key);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void ApplyTransform_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selectedId == 0)
                    return;
                if (!IPCMeloaderClient.IsConnected)
                    return;

                var posRaw = WorldPosBox.Text ?? "";
                var rotRaw = WorldRotBox.Text ?? "";
                var scaleRaw = LocalScaleBox.Text ?? "";

                var posArg = "null";
                var rotArg = "null";
                var scaleArg = "null";

                if (!string.IsNullOrWhiteSpace(posRaw))
                {
                    if (!TryParseVector3(posRaw, out var pos))
                    {
                        TransformStatus.Text = "Invalid world position";
                        return;
                    }
                    posArg = FormatVector3(pos);
                }

                if (!string.IsNullOrWhiteSpace(rotRaw))
                {
                    if (!TryParseVector3(rotRaw, out var rot))
                    {
                        TransformStatus.Text = "Invalid world rotation";
                        return;
                    }
                    rotArg = FormatVector3(rot);
                }

                if (!string.IsNullOrWhiteSpace(scaleRaw))
                {
                    if (!TryParseVector3(scaleRaw, out var scale))
                    {
                        TransformStatus.Text = "Invalid local scale";
                        return;
                    }
                    scaleArg = FormatVector3(scale);
                }

                var resp = await IPCMeloaderClient.SendCommandAsync($"SET_TRANSFORM_WORLD|{_selectedInstanceId}|{posArg}|{rotArg}|{scaleArg}", 8000);
                TransformStatus.Text = resp ?? "";

                if (posArg != "null")
                    _selectedWorldPos = posArg;
                if (rotArg != "null")
                    _selectedWorldRot = rotArg;
                if (scaleArg != "null")
                    _selectedLocalScale = scaleArg;

                WorldPosBox.Text = _selectedWorldPos;
                WorldRotBox.Text = _selectedWorldRot;
                LocalScaleBox.Text = _selectedLocalScale;

                if (_selectedMeshModel != null)
                    _selectedMeshModel.Transform = BuildSelectedTransform();
                UpdateGizmoVisibility();
            }
            catch (Exception ex)
            {
                TransformStatus.Text = ex.Message;
            }
        }

        private void ResetTransform_Click(object sender, RoutedEventArgs e)
        {
            TransformStatus.Text = "";
            WorldPosBox.Text = _selectedWorldPos;
            WorldRotBox.Text = _selectedWorldRot;
            LocalScaleBox.Text = _selectedLocalScale;
        }

        private async void RefreshSelection_Click(object sender, RoutedEventArgs e)
        {
            await RefreshSelectionAsync();
        }

        private async void OpenTypeInspector_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ComponentsList.SelectedItem is not ComponentNode comp)
                    return;

                if (!IPCMeloaderClient.IsConnected)
                    return;

                var resp = await IPCMeloaderClient.SendCommandAsync($"GET_OBJECT_INFO||{comp.Id}", 8000);
                if (resp == null || !resp.StartsWith("OBJECT_INFO|", StringComparison.Ordinal))
                    return;

                var kv = ParseKeyValues(resp);
                if (!kv.TryGetValue("type", out var typeName) || string.IsNullOrWhiteSpace(typeName))
                    return;

                var win = new TypeInspectorWindow(typeName, comp.Id)
                {
                    Owner = this
                };
                win.Show();
            }
            catch
            {
            }
        }

        private void ComponentsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                InspectSelectedComponent_Click(sender, e);
            }
            catch
            {
            }
        }

        private async void InspectSelectedComponent_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Yield();
                    OpenTypeInspector_Click(sender, e);
                });
            }
            catch
            {
            }
        }

        private async void PinSelectedComponent_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ComponentsList.SelectedItem is not ComponentNode comp)
                    return;
                if (!IPCMeloaderClient.IsConnected)
                    return;
                var resp = await IPCMeloaderClient.SendCommandAsync($"PIN_OBJECT|{comp.Id}", 8000);
                StatusText.Text = resp ?? "No response";
            }
            catch
            {
            }
        }

        private async void UnpinSelectedComponent_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ComponentsList.SelectedItem is not ComponentNode comp)
                    return;
                if (!IPCMeloaderClient.IsConnected)
                    return;
                var resp = await IPCMeloaderClient.SendCommandAsync($"UNPIN_OBJECT|{comp.Id}", 8000);
                StatusText.Text = resp ?? "No response";
            }
            catch
            {
            }
        }

        private async void DumpSelectedComponent_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ComponentsList.SelectedItem is not ComponentNode comp)
                    return;
                if (!IPCMeloaderClient.IsConnected)
                    return;

                StatusText.Text = "Dumping...";
                var resp = await IPCMeloaderClient.SendCommandAsync($"DUMP_JSON|{comp.Id}|depth=3|maxChars=200000", 12000);
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
                var json = DecodeBase64Utf8(resp.Substring(idx + b64Key.Length));
                Clipboard.SetText(json);
                StatusText.Text = "Dump copied to clipboard";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private void WatchSelectedField_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ComponentsList.SelectedItem is not ComponentNode comp)
                    return;
                if (FieldsGrid.SelectedItem is not FieldRow row)
                {
                    FieldsStatus.Text = "Select a field";
                    return;
                }
                var typeName = _selectedComponentRuntimeType;
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    FieldsStatus.Text = "Missing runtime type";
                    return;
                }
                WatchHub.AddFieldWatch(typeName, comp.Id, row.Name);
                FieldsStatus.Text = $"Watching: {row.Name}";
            }
            catch (Exception ex)
            {
                FieldsStatus.Text = ex.Message;
            }
        }

        private void CopySelectedFieldPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ComponentsList.SelectedItem is not ComponentNode comp)
                    return;
                if (FieldsGrid.SelectedItem is not FieldRow row)
                    return;
                var typeName = _selectedComponentRuntimeType;
                if (string.IsNullOrWhiteSpace(typeName))
                    return;
                Clipboard.SetText($"{typeName}[{comp.Id}].{row.Name}");
                FieldsStatus.Text = "Copied";
            }
            catch
            {
            }
        }

        private void WatchSelectedProperty_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ComponentsList.SelectedItem is not ComponentNode comp)
                    return;
                if (PropertiesGrid.SelectedItem is not PropertyRow row)
                {
                    PropertiesStatus.Text = "Select a property";
                    return;
                }
                var typeName = _selectedComponentRuntimeType;
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    PropertiesStatus.Text = "Missing runtime type";
                    return;
                }
                WatchHub.AddPropertyWatch(typeName, comp.Id, row.Name);
                PropertiesStatus.Text = $"Watching: {row.Name}";
            }
            catch (Exception ex)
            {
                PropertiesStatus.Text = ex.Message;
            }
        }

        private void CopySelectedPropertyPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ComponentsList.SelectedItem is not ComponentNode comp)
                    return;
                if (PropertiesGrid.SelectedItem is not PropertyRow row)
                    return;
                var typeName = _selectedComponentRuntimeType;
                if (string.IsNullOrWhiteSpace(typeName))
                    return;
                Clipboard.SetText($"{typeName}[{comp.Id}].{row.Name}");
                PropertiesStatus.Text = "Copied";
            }
            catch
            {
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

        private async void ComponentsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _fields.Clear();
            _properties.Clear();
            FieldsGrid.Items.Refresh();
            PropertiesGrid.Items.Refresh();
            FieldsStatus.Text = "";
            PropertiesStatus.Text = "";
            await LoadFieldsAsync();
            await LoadPropertiesAsync();
            await RefreshComponentEnabledAsync();
        }

        private async Task RefreshComponentEnabledAsync()
        {
            try
            {
                ComponentEnabledStatus.Text = "";
                _suppressComponentEnabledClick = true;
                ComponentEnabledCheckBox.IsEnabled = false;
                ComponentEnabledCheckBox.IsChecked = false;
                _suppressComponentEnabledClick = false;

                if (ComponentsList.SelectedItem is not ComponentNode comp)
                    return;

                if (!IPCMeloaderClient.IsConnected)
                    return;

                var resp = await IPCMeloaderClient.SendCommandAsync($"GET_ENABLED|{comp.Id}", 8000);
                if (resp == null || resp.StartsWith("ERROR|", StringComparison.Ordinal))
                {
                    ComponentEnabledStatus.Text = resp ?? "No response";
                    return;
                }

                if (!resp.StartsWith("ENABLED|", StringComparison.Ordinal))
                {
                    ComponentEnabledStatus.Text = resp;
                    return;
                }

                var kv = ParseKeyValues(resp);
                var enabled = kv.TryGetValue("value", out var v) && v == "1";

                _suppressComponentEnabledClick = true;
                ComponentEnabledCheckBox.IsEnabled = true;
                ComponentEnabledCheckBox.IsChecked = enabled;
                _suppressComponentEnabledClick = false;
            }
            catch (Exception ex)
            {
                ComponentEnabledStatus.Text = ex.Message;
            }
        }

        private async void ComponentEnabled_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_suppressComponentEnabledClick)
                    return;
                if (ComponentsList.SelectedItem is not ComponentNode comp)
                    return;
                if (!IPCMeloaderClient.IsConnected)
                    return;

                var next = ComponentEnabledCheckBox.IsChecked == true ? "1" : "0";
                var resp = await IPCMeloaderClient.SendCommandAsync($"SET_ENABLED|{comp.Id}|{next}", 8000);
                ComponentEnabledStatus.Text = resp ?? "";
                await RefreshComponentEnabledAsync();
            }
            catch (Exception ex)
            {
                ComponentEnabledStatus.Text = ex.Message;
            }
        }

        private async void LoadFields_Click(object sender, RoutedEventArgs e)
        {
            await LoadFieldsAsync();
        }

        private async Task LoadFieldsAsync()
        {
            try
            {
                if (ComponentsList.SelectedItem is not ComponentNode comp)
                    return;
                if (!IPCMeloaderClient.IsConnected)
                    return;

                var infoResp = await IPCMeloaderClient.SendCommandAsync($"GET_OBJECT_INFO||{comp.Id}", 8000);
                if (infoResp == null || !infoResp.StartsWith("OBJECT_INFO|", StringComparison.Ordinal))
                {
                    FieldsStatus.Text = infoResp ?? "No response";
                    return;
                }

                var info = ParseKeyValues(infoResp);
                if (!info.TryGetValue("type", out var typeName) || string.IsNullOrWhiteSpace(typeName))
                {
                    FieldsStatus.Text = "Missing type";
                    return;
                }
                _selectedComponentRuntimeType = typeName;

                FieldsStatus.Text = "Loading fields...";
                _fields.Clear();

                var resp = await IPCMeloaderClient.SendCommandAsync($"GET_INSTANCE_FIELDS|{typeName}|{comp.Id}", 8000);
                if (resp == null || !resp.StartsWith("FIELDS|", StringComparison.Ordinal))
                {
                    FieldsStatus.Text = resp ?? "No response";
                    return;
                }

                var parts = resp.Split('|');
                for (var i = 1; i < parts.Length; i++)
                {
                    var token = parts[i];
                    if (string.IsNullOrWhiteSpace(token))
                        continue;

                    var sp = token.LastIndexOf(' ');
                    if (sp <= 0 || sp >= token.Length - 1)
                        continue;

                    var type = token.Substring(0, sp);
                    var name = token.Substring(sp + 1);
                    _fields.Add(new FieldRow { Name = name, Type = type, Value = "" });
                }

                var limit = Math.Min(_fields.Count, 220);
                for (var i = 0; i < limit; i++)
                {
                    var f = _fields[i];
                    var vResp = await IPCMeloaderClient.SendCommandAsync($"GET_FIELD_INSTANCE|{typeName}|{comp.Id}|{f.Name}", 8000);
                    if (vResp != null && vResp.StartsWith("SUCCESS|", StringComparison.Ordinal))
                        f.Value = vResp.Substring("SUCCESS|".Length);
                }

                FieldsGrid.Items.Refresh();
                FieldsStatus.Text = $"Fields: {_fields.Count}";
            }
            catch (Exception ex)
            {
                FieldsStatus.Text = ex.Message;
            }
        }

        private async void ApplyField_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ComponentsList.SelectedItem is not ComponentNode comp)
                    return;
                if (FieldsGrid.SelectedItem is not FieldRow field)
                    return;
                if (!IPCMeloaderClient.IsConnected)
                    return;

                var infoResp = await IPCMeloaderClient.SendCommandAsync($"GET_OBJECT_INFO||{comp.Id}", 8000);
                if (infoResp == null || !infoResp.StartsWith("OBJECT_INFO|", StringComparison.Ordinal))
                {
                    FieldsStatus.Text = infoResp ?? "No response";
                    return;
                }

                var info = ParseKeyValues(infoResp);
                if (!info.TryGetValue("type", out var typeName) || string.IsNullOrWhiteSpace(typeName))
                    return;

                var valueString = field.Value ?? "";
                var resp = await IPCMeloaderClient.SendCommandAsync($"SET_FIELD_INSTANCE|{typeName}|{comp.Id}|{field.Name}|{valueString}", 8000);
                FieldsStatus.Text = resp ?? "";
                await LoadFieldsAsync();
            }
            catch (Exception ex)
            {
                FieldsStatus.Text = ex.Message;
            }
        }

        private async void LoadProperties_Click(object sender, RoutedEventArgs e)
        {
            await LoadPropertiesAsync();
        }

        private async Task LoadPropertiesAsync()
        {
            try
            {
                if (ComponentsList.SelectedItem is not ComponentNode comp)
                    return;
                if (!IPCMeloaderClient.IsConnected)
                    return;

                var infoResp = await IPCMeloaderClient.SendCommandAsync($"GET_OBJECT_INFO||{comp.Id}", 8000);
                if (infoResp == null || !infoResp.StartsWith("OBJECT_INFO|", StringComparison.Ordinal))
                {
                    PropertiesStatus.Text = infoResp ?? "No response";
                    return;
                }

                var info = ParseKeyValues(infoResp);
                if (!info.TryGetValue("type", out var typeName) || string.IsNullOrWhiteSpace(typeName))
                {
                    PropertiesStatus.Text = "Missing type";
                    return;
                }
                _selectedComponentRuntimeType = typeName;

                PropertiesStatus.Text = "Loading properties...";
                _properties.Clear();

                var resp = await IPCMeloaderClient.SendCommandAsync($"GET_INSTANCE_PROPERTIES|{typeName}|{comp.Id}", 8000);
                if (resp == null || !resp.StartsWith("PROPERTIES|", StringComparison.Ordinal))
                {
                    PropertiesStatus.Text = resp ?? "No response";
                    return;
                }

                var parts = resp.Split('|');
                for (var i = 1; i < parts.Length; i++)
                {
                    var token = parts[i];
                    if (string.IsNullOrWhiteSpace(token))
                        continue;

                    var sp = token.LastIndexOf(' ');
                    if (sp <= 0 || sp >= token.Length - 1)
                        continue;

                    var type = token.Substring(0, sp);
                    var name = token.Substring(sp + 1);
                    _properties.Add(new PropertyRow { Name = name, Type = type, CanRead = true, CanWrite = true, Value = "" });
                }

                var limit = Math.Min(_properties.Count, 220);
                for (var i = 0; i < limit; i++)
                {
                    var p = _properties[i];
                    var vResp = await IPCMeloaderClient.SendCommandAsync($"GET_PROPERTY_INSTANCE|{typeName}|{comp.Id}|{p.Name}", 8000);
                    if (vResp != null && vResp.StartsWith("SUCCESS|", StringComparison.Ordinal))
                        p.Value = vResp.Substring("SUCCESS|".Length);
                }

                PropertiesGrid.Items.Refresh();
                PropertiesStatus.Text = $"Properties: {_properties.Count}";
            }
            catch (Exception ex)
            {
                PropertiesStatus.Text = ex.Message;
            }
        }

        private async void ApplyProperty_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ComponentsList.SelectedItem is not ComponentNode comp)
                    return;
                if (PropertiesGrid.SelectedItem is not PropertyRow prop)
                    return;
                if (!IPCMeloaderClient.IsConnected)
                    return;

                var infoResp = await IPCMeloaderClient.SendCommandAsync($"GET_OBJECT_INFO||{comp.Id}", 8000);
                if (infoResp == null || !infoResp.StartsWith("OBJECT_INFO|", StringComparison.Ordinal))
                {
                    PropertiesStatus.Text = infoResp ?? "No response";
                    return;
                }

                var info = ParseKeyValues(infoResp);
                if (!info.TryGetValue("type", out var typeName) || string.IsNullOrWhiteSpace(typeName))
                    return;

                var valueString = prop.Value ?? "";
                var resp = await IPCMeloaderClient.SendCommandAsync($"SET_PROPERTY_INSTANCE|{typeName}|{comp.Id}|{prop.Name}|{valueString}", 8000);
                PropertiesStatus.Text = resp ?? "";
                await LoadPropertiesAsync();
            }
            catch (Exception ex)
            {
                PropertiesStatus.Text = ex.Message;
            }
        }

        private static Dictionary<string, string> ParseKeyValues(string response)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(response))
                return dict;

            var parts = response.Split('|');
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                if (string.IsNullOrWhiteSpace(p))
                    continue;
                var eq = p.IndexOf('=');
                if (eq <= 0 || eq >= p.Length - 1)
                    continue;
                var key = p.Substring(0, eq);
                var val = p.Substring(eq + 1);
                dict[key] = val;
            }
            return dict;
        }

        private static Dictionary<string, string> ParseSemicolonKeyValues(string token)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(token))
                return dict;
            var parts = token.Split(';');
            for (var i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                if (string.IsNullOrWhiteSpace(p))
                    continue;
                var eq = p.IndexOf('=');
                if (eq <= 0 || eq >= p.Length - 1)
                    continue;
                dict[p.Substring(0, eq)] = p.Substring(eq + 1);
            }
            return dict;
        }

        private static bool TryParseVector3(string text, out Vector3D v)
        {
            v = new Vector3D(0, 0, 0);
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var parts = text.Split(',');
            if (parts.Length != 3)
                return false;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                return false;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                return false;
            v = new Vector3D(x, y, z);
            return true;
        }

        private static string FormatVector3(Vector3D v)
        {
            return v.X.ToString(CultureInfo.InvariantCulture) + "," +
                   v.Y.ToString(CultureInfo.InvariantCulture) + "," +
                   v.Z.ToString(CultureInfo.InvariantCulture);
        }
    }
}
