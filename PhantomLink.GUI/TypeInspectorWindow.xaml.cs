using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    public partial class TypeInspectorWindow : Window
    {
        private readonly string _typeName;

        private readonly List<InstanceEntry> _instances = new List<InstanceEntry>();
        private readonly List<InstanceEntry> _instancesFiltered = new List<InstanceEntry>();
        private readonly List<InstanceFieldEntry> _instanceFields = new List<InstanceFieldEntry>();
        private readonly List<InstancePropertyEntry> _instanceProperties = new List<InstancePropertyEntry>();
        private readonly List<InstanceMethodEntry> _instanceMethods = new List<InstanceMethodEntry>();

        private string _selectedInstanceId;

        public TypeInspectorWindow(string typeFullName)
        {
            InitializeComponent();

            _typeName = typeFullName ?? string.Empty;
            TypeTitle.Text = _typeName;
            TypeSubtitle.Text = IPCMeloaderClient.IsConnected ? "Connected" : "Not connected";
        }


        public TypeInspectorWindow(string typeFullName, string instanceId) : this(typeFullName)
        {
            _selectedInstanceId = instanceId;

            Loaded += async (_, __) =>
            {
                if (!string.IsNullOrWhiteSpace(_selectedInstanceId))
                {
                    Tabs.SelectedIndex = Tabs.Items.Count - 1;
                    InstancesStatus.Text = $"Selected: {_selectedInstanceId}";
                    await LoadInstanceFieldsAsync();
                    await LoadInstancePropertiesAsync();
                    await LoadInstanceMethodsAsync();
                }
            };
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void EnumerateInstances_Click(object sender, RoutedEventArgs e)
        {
            await EnumerateInstancesAsync();
        }

        private async Task EnumerateInstancesAsync()
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    InstancesStatus.Text = "Not connected";
                    return;
                }

                InstancesStatus.Text = "Enumerating...";
                _instances.Clear();
                _instancesFiltered.Clear();
                _selectedInstanceId = null;
                InstancesGrid.ItemsSource = null;
                InstanceFieldsGrid.ItemsSource = null;
                InstancePropertiesGrid.ItemsSource = null;
                InstanceMethodsGrid.ItemsSource = null;

                var response = await IPCMeloaderClient.SendCommandAsync($"ENUMERATE_OBJECTS|{_typeName}", 8000);
                if (response == null)
                {
                    InstancesStatus.Text = "No response";
                    return;
                }

                if (response.StartsWith("ERROR|"))
                {
                    InstancesStatus.Text = response;
                    return;
                }

                if (!response.StartsWith("OBJECTS|"))
                {
                    InstancesStatus.Text = response;
                    return;
                }

                ParseObjectList(response);
                ApplyInstanceFilter();
                InstancesStatus.Text = "Ready";
            }
            catch (Exception ex)
            {
                InstancesStatus.Text = $"Error: {ex.Message}";
            }
        }

        private void ParseObjectList(string response)
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
                for (var k = 0; k < kvs.Length; k++)
                {
                    var kv = kvs[k];
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

                _instances.Add(new InstanceEntry
                {
                    Id = id,
                    Name = name ?? string.Empty,
                    Active = active
                });
            }

            InstanceCountText.Text = $"Found: {_instances.Count}";
        }

        private void InstanceFilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ApplyInstanceFilter();
        }

        private void ApplyInstanceFilter()
        {
            var filter = InstanceFilterBox.Text ?? string.Empty;
            filter = filter.Trim();

            _instancesFiltered.Clear();
            if (string.IsNullOrWhiteSpace(filter))
            {
                _instancesFiltered.AddRange(_instances);
            }
            else
            {
                foreach (var i in _instances)
                {
                    if (i.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        i.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        _instancesFiltered.Add(i);
                }
            }

            InstancesGrid.ItemsSource = null;
            InstancesGrid.ItemsSource = _instancesFiltered;
        }

        private void InstancesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (InstancesGrid.SelectedItem is InstanceEntry entry)
            {
                _selectedInstanceId = entry.Id;
                InstancesStatus.Text = $"Selected: {entry.Id}";
            }
        }

        private async void LoadInstanceFields_Click(object sender, RoutedEventArgs e)
        {
            await LoadInstanceFieldsAsync();
        }

        private async void PinSelectedInstance_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    InstancesStatus.Text = "Not connected";
                    return;
                }
                if (string.IsNullOrWhiteSpace(_selectedInstanceId))
                {
                    InstancesStatus.Text = "Select an instance first";
                    return;
                }

                var resp = await IPCMeloaderClient.SendCommandAsync($"PIN_OBJECT|{_selectedInstanceId}", 8000);
                InstancesStatus.Text = resp ?? "No response";
            }
            catch (Exception ex)
            {
                InstancesStatus.Text = $"Error: {ex.Message}";
            }
        }

        private async void UnpinSelectedInstance_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    InstancesStatus.Text = "Not connected";
                    return;
                }
                if (string.IsNullOrWhiteSpace(_selectedInstanceId))
                {
                    InstancesStatus.Text = "Select an instance first";
                    return;
                }

                var resp = await IPCMeloaderClient.SendCommandAsync($"UNPIN_OBJECT|{_selectedInstanceId}", 8000);
                InstancesStatus.Text = resp ?? "No response";
            }
            catch (Exception ex)
            {
                InstancesStatus.Text = $"Error: {ex.Message}";
            }
        }

        private async void DumpSelectedInstance_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    InstancesStatus.Text = "Not connected";
                    return;
                }
                if (string.IsNullOrWhiteSpace(_selectedInstanceId))
                {
                    InstancesStatus.Text = "Select an instance first";
                    return;
                }

                InstancesStatus.Text = "Dumping...";
                var resp = await IPCMeloaderClient.SendCommandAsync($"DUMP_JSON|{_selectedInstanceId}|depth=3|maxChars=200000", 12000);
                if (resp == null || resp.StartsWith("ERROR|", StringComparison.Ordinal))
                {
                    InstancesStatus.Text = resp ?? "No response";
                    return;
                }
                if (!resp.StartsWith("JSON|", StringComparison.Ordinal))
                {
                    InstancesStatus.Text = resp;
                    return;
                }

                var b64Key = "|b64=";
                var idx = resp.IndexOf(b64Key, StringComparison.Ordinal);
                if (idx < 0)
                {
                    InstancesStatus.Text = "Missing b64 payload";
                    return;
                }
                var b64 = resp.Substring(idx + b64Key.Length);
                var json = DecodeBase64Utf8(b64);
                Clipboard.SetText(json);
                InstancesStatus.Text = "Dump copied to clipboard";
            }
            catch (Exception ex)
            {
                InstancesStatus.Text = $"Error: {ex.Message}";
            }
        }

        private void WatchInstanceField_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (InstanceFieldsGrid.SelectedItem is not InstanceFieldEntry f)
                {
                    InstanceFieldsStatus.Text = "Select a field first";
                    return;
                }
                if (string.IsNullOrWhiteSpace(_selectedInstanceId))
                {
                    InstanceFieldsStatus.Text = "Select an instance first";
                    return;
                }

                WatchHub.AddFieldWatch(_typeName, _selectedInstanceId, f.Name);
                InstanceFieldsStatus.Text = $"Watching: {f.Name}";
            }
            catch (Exception ex)
            {
                InstanceFieldsStatus.Text = $"Error: {ex.Message}";
            }
        }

        private void CopyInstanceFieldPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (InstanceFieldsGrid.SelectedItem is not InstanceFieldEntry f)
                    return;
                if (string.IsNullOrWhiteSpace(_selectedInstanceId))
                    return;
                Clipboard.SetText($"{_typeName}[{_selectedInstanceId}].{f.Name}");
                InstanceFieldsStatus.Text = "Copied";
            }
            catch
            {
            }
        }

        private void WatchInstanceProperty_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (InstancePropertiesGrid.SelectedItem is not InstancePropertyEntry p)
                {
                    InstancePropertiesStatus.Text = "Select a property first";
                    return;
                }
                if (string.IsNullOrWhiteSpace(_selectedInstanceId))
                {
                    InstancePropertiesStatus.Text = "Select an instance first";
                    return;
                }

                WatchHub.AddPropertyWatch(_typeName, _selectedInstanceId, p.Name);
                InstancePropertiesStatus.Text = $"Watching: {p.Name}";
            }
            catch (Exception ex)
            {
                InstancePropertiesStatus.Text = $"Error: {ex.Message}";
            }
        }

        private void CopyInstancePropertyPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (InstancePropertiesGrid.SelectedItem is not InstancePropertyEntry p)
                    return;
                if (string.IsNullOrWhiteSpace(_selectedInstanceId))
                    return;
                Clipboard.SetText($"{_typeName}[{_selectedInstanceId}].{p.Name}");
                InstancePropertiesStatus.Text = "Copied";
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

        private async Task LoadInstanceFieldsAsync()
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    InstanceFieldsStatus.Text = "Not connected";
                    return;
                }

                if (string.IsNullOrWhiteSpace(_selectedInstanceId))
                {
                    InstanceFieldsStatus.Text = "Select an instance first";
                    return;
                }

                InstanceFieldsStatus.Text = "Loading fields...";
                _instanceFields.Clear();

                var response = await IPCMeloaderClient.SendCommandAsync($"GET_INSTANCE_FIELDS|{_typeName}|{_selectedInstanceId}", 8000);
                if (response == null)
                {
                    InstanceFieldsStatus.Text = "No response";
                    return;
                }

                if (!response.StartsWith("FIELDS|"))
                {
                    InstanceFieldsStatus.Text = response;
                    return;
                }

                var parts = response.Split('|');
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
                    _instanceFields.Add(new InstanceFieldEntry
                    {
                        Name = name,
                        Type = type,
                        Value = string.Empty
                    });
                }

                InstanceFieldsGrid.ItemsSource = _instanceFields;
                await PopulateInstanceFieldValuesAsync();
                InstanceFieldsStatus.Text = $"Fields: {_instanceFields.Count}";
            }
            catch (Exception ex)
            {
                InstanceFieldsStatus.Text = $"Error: {ex.Message}";
            }
        }

        private async Task PopulateInstanceFieldValuesAsync()
        {
            var limit = Math.Min(_instanceFields.Count, 300);
            for (var i = 0; i < limit; i++)
            {
                var field = _instanceFields[i];
                var resp = await IPCMeloaderClient.SendCommandAsync($"GET_FIELD_INSTANCE|{_typeName}|{_selectedInstanceId}|{field.Name}");
                if (resp != null && resp.StartsWith("SUCCESS|"))
                    field.Value = resp.Substring("SUCCESS|".Length);
            }
            InstanceFieldsGrid.Items.Refresh();
        }

        private async void UpdateInstanceField_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (InstanceFieldsGrid.SelectedItem is not InstanceFieldEntry field)
                    return;

                if (!IPCMeloaderClient.IsConnected)
                {
                    InstanceFieldsStatus.Text = "Not connected";
                    return;
                }

                if (string.IsNullOrWhiteSpace(_selectedInstanceId))
                {
                    InstanceFieldsStatus.Text = "Select an instance first";
                    return;
                }

                var valueString = field.Value ?? string.Empty;
                var response = await IPCMeloaderClient.SendCommandAsync($"SET_FIELD_INSTANCE|{_typeName}|{_selectedInstanceId}|{field.Name}|{valueString}");
                InstanceFieldsStatus.Text = response ?? "No response";
                await PopulateInstanceFieldValuesAsync();
            }
            catch (Exception ex)
            {
                InstanceFieldsStatus.Text = $"Error: {ex.Message}";
            }
        }

        private async void LoadInstanceProperties_Click(object sender, RoutedEventArgs e)
        {
            await LoadInstancePropertiesAsync();
        }

        private async Task LoadInstancePropertiesAsync()
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    InstancePropertiesStatus.Text = "Not connected";
                    return;
                }

                if (string.IsNullOrWhiteSpace(_selectedInstanceId))
                {
                    InstancePropertiesStatus.Text = "Select an instance first";
                    return;
                }

                InstancePropertiesStatus.Text = "Loading properties...";
                _instanceProperties.Clear();

                var response = await IPCMeloaderClient.SendCommandAsync($"GET_INSTANCE_PROPERTIES|{_typeName}|{_selectedInstanceId}", 8000);
                if (response == null)
                {
                    InstancePropertiesStatus.Text = "No response";
                    return;
                }

                if (!response.StartsWith("PROPERTIES|"))
                {
                    InstancePropertiesStatus.Text = response;
                    return;
                }

                var parts = response.Split('|');
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
                    _instanceProperties.Add(new InstancePropertyEntry
                    {
                        Name = name,
                        Type = type,
                        Value = string.Empty,
                        CanRead = true,
                        CanWrite = true
                    });
                }

                InstancePropertiesGrid.ItemsSource = _instanceProperties;
                await PopulateInstancePropertyValuesAsync();
                InstancePropertiesStatus.Text = $"Properties: {_instanceProperties.Count}";
            }
            catch (Exception ex)
            {
                InstancePropertiesStatus.Text = $"Error: {ex.Message}";
            }
        }

        private async Task PopulateInstancePropertyValuesAsync()
        {
            var limit = Math.Min(_instanceProperties.Count, 300);
            for (var i = 0; i < limit; i++)
            {
                var prop = _instanceProperties[i];
                var resp = await IPCMeloaderClient.SendCommandAsync($"GET_PROPERTY_INSTANCE|{_typeName}|{_selectedInstanceId}|{prop.Name}");
                if (resp != null && resp.StartsWith("SUCCESS|"))
                    prop.Value = resp.Substring("SUCCESS|".Length);
                else if (resp != null && resp.StartsWith("ERROR|"))
                    prop.Value = string.Empty;
            }
            InstancePropertiesGrid.Items.Refresh();
        }

        private async void SetInstanceProperty_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (InstancePropertiesGrid.SelectedItem is not InstancePropertyEntry prop)
                    return;

                if (!IPCMeloaderClient.IsConnected)
                {
                    InstancePropertiesStatus.Text = "Not connected";
                    return;
                }

                if (string.IsNullOrWhiteSpace(_selectedInstanceId))
                {
                    InstancePropertiesStatus.Text = "Select an instance first";
                    return;
                }

                var dialog = new TextPromptDialog(
                    "Set Instance Property",
                    $"{_typeName}.{prop.Name} (id={_selectedInstanceId})",
                    prop.Value ?? string.Empty)
                {
                    Owner = this
                };

                if (dialog.ShowDialog() != true)
                    return;

                var newValue = dialog.ValueText ?? string.Empty;
                var response = await IPCMeloaderClient.SendCommandAsync($"SET_PROPERTY_INSTANCE|{_typeName}|{_selectedInstanceId}|{prop.Name}|{newValue}");
                InstancePropertiesStatus.Text = response ?? "No response";
                await PopulateInstancePropertyValuesAsync();
            }
            catch (Exception ex)
            {
                InstancePropertiesStatus.Text = $"Error: {ex.Message}";
            }
        }

        private async void UpdateInstanceProperty_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (InstancePropertiesGrid.SelectedItem is not InstancePropertyEntry prop)
                    return;

                if (!prop.CanWrite)
                {
                    InstancePropertiesStatus.Text = "Property is read-only";
                    return;
                }

                if (!IPCMeloaderClient.IsConnected)
                {
                    InstancePropertiesStatus.Text = "Not connected";
                    return;
                }

                if (string.IsNullOrWhiteSpace(_selectedInstanceId))
                {
                    InstancePropertiesStatus.Text = "Select an instance first";
                    return;
                }

                var newValue = prop.Value ?? string.Empty;
                var response = await IPCMeloaderClient.SendCommandAsync($"SET_PROPERTY_INSTANCE|{_typeName}|{_selectedInstanceId}|{prop.Name}|{newValue}");
                InstancePropertiesStatus.Text = response ?? "No response";
                await PopulateInstancePropertyValuesAsync();
            }
            catch (Exception ex)
            {
                InstancePropertiesStatus.Text = $"Error: {ex.Message}";
            }
        }

        private void InstanceFieldsGrid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            UpdateInstanceField_Click(sender, e);
        }

        private void InstanceFieldsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            UpdateInstanceField_Click(sender, e);
        }

        private void InstancePropertiesGrid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            UpdateInstanceProperty_Click(sender, e);
        }

        private void InstancePropertiesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            UpdateInstanceProperty_Click(sender, e);
        }

        private async void LoadInstanceMethods_Click(object sender, RoutedEventArgs e)
        {
            await LoadInstanceMethodsAsync();
        }

        private async Task LoadInstanceMethodsAsync()
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    InstanceMethodsStatus.Text = "Not connected";
                    return;
                }

                if (string.IsNullOrWhiteSpace(_selectedInstanceId))
                {
                    InstanceMethodsStatus.Text = "Select an instance first";
                    return;
                }

                InstanceMethodsStatus.Text = "Loading methods...";
                _instanceMethods.Clear();

                var response = await IPCMeloaderClient.SendCommandAsync($"GET_INSTANCE_METHODS|{_typeName}|{_selectedInstanceId}", 8000);
                if (response == null)
                {
                    InstanceMethodsStatus.Text = "No response";
                    return;
                }

                if (!response.StartsWith("METHODS|"))
                {
                    InstanceMethodsStatus.Text = response;
                    return;
                }

                var parts = response.Split('|');
                for (var i = 1; i < parts.Length; i++)
                {
                    var sig = parts[i];
                    if (string.IsNullOrWhiteSpace(sig))
                        continue;

                    var name = ExtractMethodName(sig);
                    _instanceMethods.Add(new InstanceMethodEntry
                    {
                        MethodName = name,
                        Signature = sig,
                        IsPublic = true
                    });
                }

                InstanceMethodsGrid.ItemsSource = _instanceMethods;
                InstanceMethodsStatus.Text = $"Methods: {_instanceMethods.Count}";
            }
            catch (Exception ex)
            {
                InstanceMethodsStatus.Text = $"Error: {ex.Message}";
            }
        }

        private static string ExtractMethodName(string signature)
        {
            var open = signature.IndexOf('(');
            if (open < 0)
                return signature;

            var before = signature.Substring(0, open).Trim();
            var sp = before.LastIndexOf(' ');
            if (sp < 0 || sp >= before.Length - 1)
                return before;

            return before.Substring(sp + 1);
        }

        private async void InvokeInstanceMethod_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (InstanceMethodsGrid.SelectedItem is not InstanceMethodEntry method)
                    return;

                if (!IPCMeloaderClient.IsConnected)
                {
                    InstanceMethodsStatus.Text = "Not connected";
                    return;
                }

                if (string.IsNullOrWhiteSpace(_selectedInstanceId))
                {
                    InstanceMethodsStatus.Text = "Select an instance first";
                    return;
                }

                var args = InstanceInvokeArgsBox.Text?.Trim();
                if (string.IsNullOrEmpty(args))
                    args = "null";

                var response = await IPCMeloaderClient.SendCommandAsync($"INVOKE_INSTANCE|{_typeName}|{_selectedInstanceId}|{method.MethodName}|{args}");
                InstanceMethodsStatus.Text = response ?? "No response";
            }
            catch (Exception ex)
            {
                InstanceMethodsStatus.Text = $"Error: {ex.Message}";
            }
        }

        private class InstanceEntry
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public bool? Active { get; set; }
        }

        private class InstanceFieldEntry
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public string Value { get; set; }
        }

        private class InstancePropertyEntry
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public bool CanRead { get; set; }
            public bool CanWrite { get; set; }
            public string Value { get; set; }
        }

        private class InstanceMethodEntry
        {
            public string MethodName { get; set; }
            public string Signature { get; set; }
            public bool IsPublic { get; set; }
        }
    }
}
