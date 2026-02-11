using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    public partial class PropertiesView : UserControl
    {
        private List<Assembly> _loadedAssemblies = new List<Assembly>();
        private CancellationTokenSource _searchCts;

        public PropertiesView()
        {
            InitializeComponent();
            MetadataLoader.MetadataLoaded += OnMetadataLoaded;
            LoadAssembliesFromMetadata();
            ScheduleSearch();
        }

        private void OnMetadataLoaded(string gameDirectory, int assemblyCount)
        {
            Dispatcher.Invoke(() =>
            {
                LoadAssembliesFromMetadata();
                ScheduleSearch();
            });
        }

        private void LoadAssembliesFromMetadata()
        {
            try
            {
                _loadedAssemblies = MetadataLoader.GetRuntimeAssemblies().ToList();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to load metadata assemblies: {ex.Message}");
            }
        }

        private void ScheduleSearch()
        {
            _searchCts?.Cancel();
            var cts = new CancellationTokenSource();
            _searchCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(350, cts.Token);
                    string searchTerm = null;
                    string typeFilter = null;
                    var includeReadable = false;
                    var includeWritable = false;
                    var includeStatic = false;
                    var includePublic = false;

                    Dispatcher.Invoke(() =>
                    {
                        searchTerm = SearchBox.Text;
                        typeFilter = TypeFilterBox.Text;
                        includeReadable = ReadableCheckBox.IsChecked == true;
                        includeWritable = WritableCheckBox.IsChecked == true;
                        includeStatic = StaticCheckBox.IsChecked == true;
                        includePublic = PublicCheckBox.IsChecked == true;
                    });

                    if (_loadedAssemblies.Count == 0)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            PropertiesGrid.ItemsSource = Array.Empty<PropertyInfoWrapper>();
                            PropertyCountText.Text = "Properties: 0";
                        });
                        return;
                    }

                    var properties = TypeBrowser.SearchProperties(_loadedAssemblies, searchTerm, typeFilter)
                        .Where(p =>
                        {
                            var anyFilter = includeReadable || includeWritable || includeStatic || includePublic;
                            if (!anyFilter)
                                return true;
                            if (includeReadable && !p.CanRead)
                                return false;
                            if (includeWritable && !p.CanWrite)
                                return false;
                            if (includeStatic && !p.IsStatic)
                                return false;
                            if (includePublic && !p.IsPublic)
                                return false;
                            return true;
                        })
                        .Take(3000)
                        .ToList();

                    Dispatcher.Invoke(() =>
                    {
                        PropertiesGrid.ItemsSource = properties;
                        PropertyCountText.Text = $"Properties: {properties.Count}";
                    });
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to search properties: {ex.Message}");
                }
            }, cts.Token);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (AutoSearchCheckBox.IsChecked == true)
                ScheduleSearch();
        }

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            ScheduleSearch();
        }

        private void RefreshAssemblies_Click(object sender, RoutedEventArgs e)
        {
            LoadAssembliesFromMetadata();
            ScheduleSearch();
        }

        private void GetProperty_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is PropertyInfoWrapper propertyWrapper)
            {
                try
                {
                    if (!IPCMeloaderClient.IsConnected)
                    {
                        MessageBox.Show("Not connected to game", "Property");
                        return;
                    }

                    if (!propertyWrapper.IsStatic)
                    {
                        MessageBox.Show("Only static properties are supported here", "Property");
                        return;
                    }

                    _ = Dispatcher.InvokeAsync(async () =>
                    {
                        var response = await IPCMeloaderClient.SendCommandAsync(
                            $"GET_PROPERTY|{propertyWrapper.DeclaringType}|{propertyWrapper.PropertyName}");

                        if (response != null && response.StartsWith("SUCCESS|"))
                        {
                            var value = response.Substring("SUCCESS|".Length);
                            MessageBox.Show(value, $"{propertyWrapper.DeclaringType}.{propertyWrapper.PropertyName}");
                        }
                        else
                        {
                            MessageBox.Show(response ?? "No response", "Property");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to get property: {ex.Message}");
                }
            }
        }

        private void SetProperty_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is PropertyInfoWrapper propertyWrapper)
            {
                try
                {
                    if (!IPCMeloaderClient.IsConnected)
                    {
                        MessageBox.Show("Not connected to game", "Property");
                        return;
                    }

                    if (!propertyWrapper.IsStatic)
                    {
                        MessageBox.Show("Only static properties are supported here", "Property");
                        return;
                    }

                    var dialog = new TextPromptDialog(
                        "Set Property",
                        $"{propertyWrapper.DeclaringType}.{propertyWrapper.PropertyName}",
                        string.Empty)
                    {
                        Owner = Window.GetWindow(this)
                    };

                    if (dialog.ShowDialog() != true)
                        return;

                    var newValue = dialog.ValueText ?? string.Empty;
                    _ = Dispatcher.InvokeAsync(async () =>
                    {
                        var response = await IPCMeloaderClient.SendCommandAsync(
                            $"SET_PROPERTY|{propertyWrapper.DeclaringType}|{propertyWrapper.PropertyName}|{newValue}");

                        MessageBox.Show(response ?? "No response", "Property");
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to set property: {ex.Message}");
                }
            }
        }
    }
}
