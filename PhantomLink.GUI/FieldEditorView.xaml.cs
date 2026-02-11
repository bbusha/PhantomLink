using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    public partial class FieldEditorView : UserControl
    {
        private string _currentTypeName;
        private Type _currentType;
        
        public FieldEditorView()
        {
            InitializeComponent();
        }

        private async void InspectObject_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var typeName = ObjectAddressBox.Text.Trim();
                if (string.IsNullOrEmpty(typeName))
                {
                    FieldStatus.Text = "Please enter a type name";
                    return;
                }

                _currentTypeName = typeName;
                _currentType = MetadataLoader.FindType(typeName);

                if (_currentType == null)
                {
                    var candidates = MetadataLoader.FindTypes(typeName).Take(200).ToList();
                    if (candidates.Count == 0)
                    {
                        FieldStatus.Text = $"Type not found in loaded metadata: {typeName}";
                        return;
                    }

                    if (candidates.Count == 1)
                    {
                        _currentType = candidates[0];
                        _currentTypeName = _currentType.FullName;
                        ObjectAddressBox.Text = _currentTypeName;
                    }
                    else
                    {
                        var picker = new TypePickerDialog($"Multiple matches for '{typeName}'. Select one:", candidates)
                        {
                            Owner = Window.GetWindow(this)
                        };
                        if (picker.ShowDialog() == true && picker.SelectedType != null)
                        {
                            _currentType = picker.SelectedType;
                            _currentTypeName = _currentType.FullName;
                            ObjectAddressBox.Text = _currentTypeName;
                        }
                        else
                        {
                            FieldStatus.Text = "No type selected";
                            return;
                        }
                    }
                }

                await LoadFieldsAsync();
            }
            catch (Exception ex)
            {
                FieldStatus.Text = $"Error loading type: {ex.Message}";
            }
        }

        private async void RefreshFields_Click(object sender, RoutedEventArgs e)
        {
            if (_currentType != null)
            {
                await LoadFieldsAsync();
            }
        }

        private async Task LoadFieldsAsync()
        {
            try
            {
                FieldStatus.Text = "Loading fields...";

                var fields = MetadataLoader.GetFields(_currentType).ToList();
                var wrappers = fields.Select(f => new FieldInfoWrapper
                    {
                        FieldName = f.Name,
                        DeclaringType = _currentType.FullName,
                        FieldType = f.FieldType.Name,
                        IsStatic = f.IsStatic,
                        IsPublic = f.IsPublic,
                        IsLiteral = f.IsLiteral,
                        IsInitOnly = f.IsInitOnly,
                        CanWrite = f.IsStatic && !f.IsInitOnly && !f.IsLiteral,
                        FieldInfo = f,
                        Value = f.IsStatic ? null : "(instance)"
                    })
                    .ToList();

                FieldsGrid.ItemsSource = wrappers;
                ObjectTypeText.Text = $"Type: {_currentType.FullName}";
                FieldCountText.Text = $"Fields: {wrappers.Count}";

                if (IPCMeloaderClient.IsConnected)
                {
                    await PopulateStaticFieldValuesAsync(wrappers);
                }

                FieldStatus.Text = IPCMeloaderClient.IsConnected
                    ? "Loaded fields (static values fetched)"
                    : "Loaded fields (connect to game to fetch values)";
            }
            catch (Exception ex)
            {
                FieldStatus.Text = $"Error loading fields: {ex.Message}";
            }
        }

        private async void UpdateField_Click(object sender, RoutedEventArgs e)
        {
            if (FieldsGrid.SelectedItem is FieldInfoWrapper field && _currentType != null)
            {
                try
                {
                    if (!IPCMeloaderClient.IsConnected)
                    {
                        FieldStatus.Text = "Not connected to game";
                        return;
                    }

                    if (!field.IsStatic)
                    {
                        FieldStatus.Text = "Instance fields require object enumeration support";
                        return;
                    }

                    if (!field.CanWrite)
                    {
                        FieldStatus.Text = "Field is read-only";
                        return;
                    }

                    var valueString = field.Value?.ToString() ?? string.Empty;
                    var response = await IPCMeloaderClient.SendCommandAsync(
                        $"SET_FIELD|{_currentTypeName}|{field.FieldName}|{valueString}");

                    if (response != null && response.StartsWith("SUCCESS|"))
                    {
                        await RefreshSingleFieldValueAsync(field);
                        FieldStatus.Text = $"Updated {field.FieldName}";
                    }
                    else
                    {
                        FieldStatus.Text = $"Update failed: {response}";
                    }
                }
                catch (Exception ex)
                {
                    FieldStatus.Text = $"Error updating field: {ex.Message}";
                }
            }
        }

        private async Task PopulateStaticFieldValuesAsync(List<FieldInfoWrapper> wrappers)
        {
            var staticFields = wrappers.Where(w => w.IsStatic).Take(200).ToList();
            foreach (var field in staticFields)
            {
                await RefreshSingleFieldValueAsync(field);
            }
        }

        private async Task RefreshSingleFieldValueAsync(FieldInfoWrapper field)
        {
            var response = await IPCMeloaderClient.SendCommandAsync($"GET_FIELD|{_currentTypeName}|{field.FieldName}");
            if (response != null && response.StartsWith("SUCCESS|"))
            {
                field.Value = response.Substring("SUCCESS|".Length);
                FieldsGrid.Items.Refresh();
            }
        }
    }
}
