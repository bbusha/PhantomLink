using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    public partial class ObjectFinderView : UserControl
    {
        private readonly List<ObjectEntry> _results = new List<ObjectEntry>();

        public ObjectFinderView()
        {
            InitializeComponent();
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

                var typeName = TypeBox.Text?.Trim() ?? string.Empty;
                var filter = FilterBox.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    StatusText.Text = "Missing type";
                    return;
                }

                StatusText.Text = "Searching...";
                DetailsText.Text = string.Empty;

                var response = await IPCMeloaderClient.SendCommandAsync($"FIND_OBJECTS|{typeName}|{filter}", 8000);
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

                if (!response.StartsWith("OBJECTS|", StringComparison.Ordinal))
                {
                    StatusText.Text = response;
                    return;
                }

                _results.Clear();
                ParseObjectsResponse(response, _results);
                ResultsGrid.ItemsSource = null;
                ResultsGrid.ItemsSource = _results;
                StatusText.Text = $"Found: {_results.Count}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private void ParseObjectsResponse(string response, List<ObjectEntry> output)
        {
            var parts = response.Split('|');
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                if (string.IsNullOrWhiteSpace(p))
                    continue;

                if (p.StartsWith("type=", StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith("count=", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!p.StartsWith("id=", StringComparison.OrdinalIgnoreCase))
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

                output.Add(new ObjectEntry
                {
                    TrackedId = id,
                    Name = name ?? string.Empty,
                    Active = active
                });
            }
        }

        private async void RefreshInfo_Click(object sender, RoutedEventArgs e)
        {
            if (ResultsGrid.SelectedItem is ObjectEntry obj)
                await ShowInfoAsync(obj);
        }

        private async void Info_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.DataContext is ObjectEntry obj)
                await ShowInfoAsync(obj);
        }

        private async Task ShowInfoAsync(ObjectEntry obj)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                var typeName = TypeBox.Text?.Trim() ?? string.Empty;
                var response = await IPCMeloaderClient.SendCommandAsync($"GET_OBJECT_INFO|{typeName}|{obj.TrackedId}", 8000);
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

                if (!response.StartsWith("OBJECT_INFO|", StringComparison.Ordinal))
                {
                    StatusText.Text = response;
                    return;
                }

                var info = ParseInfo(response);
                obj.RuntimeType = info.TryGetValue("type", out var t) ? t : obj.RuntimeType;
                DetailsText.Text = BuildSummary(info);
                StatusText.Text = "Info loaded";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private Dictionary<string, string> ParseInfo(string response)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var parts = response.Split('|');
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                var eq = p.IndexOf('=');
                if (eq <= 0)
                    continue;
                var key = p.Substring(0, eq);
                var val = p.Substring(eq + 1);
                dict[key] = val;
            }
            return dict;
        }

        private string BuildSummary(Dictionary<string, string> info)
        {
            var pieces = new List<string>();
            if (info.TryGetValue("type", out var t)) pieces.Add($"type={t}");
            if (info.TryGetValue("name", out var n)) pieces.Add($"name={n}");
            if (info.TryGetValue("active", out var a)) pieces.Add($"active={a}");
            if (info.TryGetValue("pos", out var p)) pieces.Add($"pos={p}");
            if (info.TryGetValue("components", out var c)) pieces.Add($"components={c}");
            return string.Join(" | ", pieces.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private async void Inspect_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.DataContext is ObjectEntry obj)
                await OpenInspectorAsync(obj);
        }

        private async void InspectSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ResultsGrid.SelectedItem is ObjectEntry obj)
                    await OpenInspectorAsync(obj);
            }
            catch
            {
            }
        }

        private async void ResultsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (ResultsGrid.SelectedItem is ObjectEntry obj)
                    await OpenInspectorAsync(obj);
            }
            catch
            {
            }
        }

        private async void PinSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }
                if (ResultsGrid.SelectedItem is not ObjectEntry obj)
                    return;
                var resp = await IPCMeloaderClient.SendCommandAsync($"PIN_OBJECT|{obj.TrackedId}", 8000);
                StatusText.Text = resp ?? "No response";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void UnpinSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }
                if (ResultsGrid.SelectedItem is not ObjectEntry obj)
                    return;
                var resp = await IPCMeloaderClient.SendCommandAsync($"UNPIN_OBJECT|{obj.TrackedId}", 8000);
                StatusText.Text = resp ?? "No response";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void DumpSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }
                if (ResultsGrid.SelectedItem is not ObjectEntry obj)
                    return;

                StatusText.Text = "Dumping...";
                var resp = await IPCMeloaderClient.SendCommandAsync($"DUMP_JSON|{obj.TrackedId}|depth=3|maxChars=200000", 12000);
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

        private static string DecodeBase64Utf8(string b64)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(b64))
                    return "";
                var bytes = Convert.FromBase64String(b64);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return "";
            }
        }

        private async Task OpenInspectorAsync(ObjectEntry obj)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(obj.RuntimeType))
                    await ShowInfoAsync(obj);

                if (string.IsNullOrWhiteSpace(obj.RuntimeType))
                {
                    StatusText.Text = "No runtime type available";
                    return;
                }

                var win = new TypeInspectorWindow(obj.RuntimeType, obj.TrackedId)
                {
                    Owner = Window.GetWindow(this)
                };
                win.Show();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private class ObjectEntry
        {
            public string TrackedId { get; set; }
            public string Name { get; set; }
            public bool? Active { get; set; }
            public string RuntimeType { get; set; }
        }
    }
}
