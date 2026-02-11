using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    public partial class MethodBrowserView : UserControl
    {
        private List<MethodInfoWrapper> _allMethods = new List<MethodInfoWrapper>();
        
        public MethodBrowserView()
        {
            InitializeComponent();
        }

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            SearchMethods();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (AutoSearchCheckBox.IsChecked == true)
                SearchMethods();
        }

        private void RefreshAssemblies_Click(object sender, RoutedEventArgs e)
        {
            LoadAssemblies();
        }

        private void LoadAssemblies()
        {
            try
            {
                var assemblies = MetadataLoader.GetRuntimeAssemblies();
                AssemblyCountText.Text = $"Assemblies: {assemblies.Count}";

                _allMethods = new List<MethodInfoWrapper>();
                MethodsGrid.ItemsSource = _allMethods;
                MethodCountText.Text = "Methods: 0";
                MethodStatus.Text = assemblies.Count == 0
                    ? "Metadata not loaded yet"
                    : "Metadata loaded. Use search to find methods.";
            }
            catch (Exception ex)
            {
                MethodStatus.Text = $"Error loading assemblies: {ex.Message}";
            }
        }

        private void SearchMethods()
        {
            try
            {
                var searchTerm = SearchBox.Text.Trim();
                var typeFilter = TypeFilterBox.Text.Trim();
                
                var assemblies = MetadataLoader.GetRuntimeAssemblies();
                if (assemblies.Count == 0)
                {
                    MethodsGrid.ItemsSource = Array.Empty<MethodInfoWrapper>();
                    MethodStatus.Text = "Metadata not loaded yet";
                    MethodCountText.Text = "Methods: 0";
                    return;
                }

                var filteredMethods = TypeBrowser.SearchMethods(assemblies, searchTerm, typeFilter).Take(2000).ToList();
                MethodsGrid.ItemsSource = filteredMethods;
                MethodStatus.Text = $"Found {filteredMethods.Count} methods matching criteria";
                MethodCountText.Text = $"Methods: {filteredMethods.Count}";
            }
            catch (Exception ex)
            {
                MethodStatus.Text = $"Error searching methods: {ex.Message}";
            }
        }

        private void PatchMethod_Click(object sender, RoutedEventArgs e)
        {
            if (MethodsGrid.SelectedItem is MethodInfoWrapper method)
            {
                var dialog = new AddPatchDialog
                {
                    MethodName = method.MethodName,
                    TypeName = method.DeclaringType,
                    AssemblyName = method.MethodInfo.DeclaringType?.Assembly.GetName().Name ?? "Unknown"
                };
                
                if (dialog.ShowDialog() == true)
                {
                    PatchManager.AddPatch(dialog.PatchDefinition);
                    MethodStatus.Text = $"Patch created for {method.MethodName}";
                }
            }
        }

        private async void InvokeMethod_Click(object sender, RoutedEventArgs e)
        {
            if (MethodsGrid.SelectedItem is MethodInfoWrapper method)
            {
                try
                {
                    var command = $"INVOKE|{method.DeclaringType}|{method.MethodName}|null";
                    var response = await IPCMeloaderClient.SendCommandAsync(command);
                    MethodStatus.Text = response != null && response.StartsWith("SUCCESS|")
                        ? $"Invoked {method.MethodName}. Result: {response.Substring("SUCCESS|".Length)}"
                        : $"Invoke failed: {response}";
                }
                catch (Exception ex)
                {
                    MethodStatus.Text = $"Error invoking method via pipe: {ex.Message}";
                }
            }
        }
    }
}
