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
    public partial class FactoryMethodBrowserView : UserControl
    {
        private readonly List<FactoryMethodRow> _methods = new List<FactoryMethodRow>();
        private readonly List<ArgRow> _args = new List<ArgRow>();
        private FactoryMethodRow _selected;

        public FactoryMethodBrowserView()
        {
            InitializeComponent();
            MethodsGrid.ItemsSource = _methods;
            ArgsGrid.ItemsSource = _args;
        }

        private async void FindStatic_Click(object sender, RoutedEventArgs e)
        {
            await FindAsync(instance: false);
        }

        private async void FindInstance_Click(object sender, RoutedEventArgs e)
        {
            await FindAsync(instance: true);
        }

        private async Task FindAsync(bool instance)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    CountText.Text = "Not connected";
                    return;
                }

                var term = SearchBox.Text?.Trim() ?? string.Empty;
                string cmd;
                if (instance)
                {
                    var id = InstanceIdBox.Text?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        CountText.Text = "Missing instance id";
                        return;
                    }
                    cmd = $"FIND_INSTANCE_FACTORY_METHODS|{id}|{term}";
                }
                else
                {
                    cmd = $"FIND_FACTORY_METHODS|{term}";
                }

                CountText.Text = "Searching...";
                var response = await IPCMeloaderClient.SendCommandAsync(cmd, 12000);
                if (response == null)
                {
                    CountText.Text = "No response";
                    return;
                }

                if (!response.StartsWith("FACTORY_METHODS|", StringComparison.Ordinal))
                {
                    CountText.Text = response;
                    return;
                }

                _methods.Clear();
                ParseMethods(response, _methods);
                MethodsGrid.Items.Refresh();
                CountText.Text = $"Methods: {_methods.Count}";
            }
            catch (Exception ex)
            {
                CountText.Text = $"Error: {ex.Message}";
            }
        }

        private void ParseMethods(string response, List<FactoryMethodRow> output)
        {
            var parts = response.Split('|');
            for (var i = 1; i < parts.Length; i++)
            {
                var token = parts[i];
                if (string.IsNullOrWhiteSpace(token))
                    continue;
                if (token.StartsWith("count=", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("instanceId=", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!token.StartsWith("id=", StringComparison.OrdinalIgnoreCase))
                    continue;

                var kvs = token.Split(';');
                var row = new FactoryMethodRow();
                foreach (var kv in kvs)
                {
                    var eq = kv.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    var k = kv.Substring(0, eq);
                    var v = kv.Substring(eq + 1);
                    if (k.Equals("id", StringComparison.OrdinalIgnoreCase))
                        row.Id = v;
                    else if (k.Equals("type", StringComparison.OrdinalIgnoreCase))
                        row.DeclaringType = v;
                    else if (k.Equals("method", StringComparison.OrdinalIgnoreCase))
                        row.MethodName = v;
                    else if (k.Equals("static", StringComparison.OrdinalIgnoreCase))
                        row.IsStatic = v == "1";
                    else if (k.Equals("ret", StringComparison.OrdinalIgnoreCase))
                        row.ReturnType = v;
                    else if (k.Equals("params", StringComparison.OrdinalIgnoreCase))
                        row.Params = v;
                }

                if (!string.IsNullOrWhiteSpace(row.Id))
                    output.Add(row);
            }
        }

        private void MethodsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MethodsGrid.SelectedItem is not FactoryMethodRow row)
                return;
            _selected = row;
            _args.Clear();

            var ps = ParseParamList(row.Params);
            foreach (var p in ps)
            {
                _args.Add(new ArgRow
                {
                    Name = p.Name,
                    Type = p.Type,
                    Value = ""
                });
            }

            ArgsGrid.Items.Refresh();
        }

        private List<ParamEntry> ParseParamList(string text)
        {
            var list = new List<ParamEntry>();
            if (string.IsNullOrWhiteSpace(text))
                return list;

            var parts = text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var t = p.Trim();
                var sp = t.LastIndexOf(' ');
                if (sp <= 0 || sp >= t.Length - 1)
                {
                    list.Add(new ParamEntry { Type = t, Name = $"arg{list.Count}" });
                }
                else
                {
                    list.Add(new ParamEntry { Type = t.Substring(0, sp).Trim(), Name = t.Substring(sp + 1).Trim() });
                }
            }
            return list;
        }

        private async void Invoke_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selected == null || string.IsNullOrWhiteSpace(_selected.Id))
                    return;
                if (!IPCMeloaderClient.IsConnected)
                    return;

                if (!int.TryParse(_selected.Id, out var id))
                    return;

                var parts = new List<string>();
                parts.Add("INVOKE_FACTORY_B64");
                parts.Add(id.ToString());
                foreach (var a in _args)
                {
                    var v = a.Value ?? "";
                    parts.Add(ToB64(v));
                }

                var cmd = string.Join("|", parts);
                var response = await IPCMeloaderClient.SendCommandAsync(cmd, 12000);
                if (response != null && response.StartsWith("SUCCESS|id=", StringComparison.Ordinal))
                {
                    var dict = ParseKeyValues(response);
                    if (dict.TryGetValue("type", out var t) && dict.TryGetValue("id", out var rid))
                    {
                        var win = new TypeInspectorWindow(t, rid)
                        {
                            Owner = Window.GetWindow(this)
                        };
                        win.Show();
                    }
                }
                CountText.Text = response ?? "No response";
            }
            catch (Exception ex)
            {
                CountText.Text = $"Error: {ex.Message}";
            }
        }

        private string ToB64(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text ?? "");
            return Convert.ToBase64String(bytes);
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

        private class FactoryMethodRow
        {
            public string Id { get; set; }
            public bool IsStatic { get; set; }
            public string ReturnType { get; set; }
            public string DeclaringType { get; set; }
            public string MethodName { get; set; }
            public string Params { get; set; }
        }

        private class ArgRow
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public string Value { get; set; }
        }

        private class ParamEntry
        {
            public string Type { get; set; }
            public string Name { get; set; }
        }
    }
}
