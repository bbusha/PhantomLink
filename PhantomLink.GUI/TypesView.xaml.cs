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
    public partial class TypesView : UserControl
    {
        private List<Assembly> _loadedAssemblies = new List<Assembly>();
        private CancellationTokenSource _searchCts;

        public TypesView()
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
                    string namespaceFilter = null;
                    var includeClasses = false;
                    var includeInterfaces = false;
                    var includeEnums = false;
                    var includeValueTypes = false;

                    Dispatcher.Invoke(() =>
                    {
                        searchTerm = SearchBox.Text;
                        namespaceFilter = NamespaceBox.Text;
                        includeClasses = ClassesCheckBox.IsChecked == true;
                        includeInterfaces = InterfacesCheckBox.IsChecked == true;
                        includeEnums = EnumsCheckBox.IsChecked == true;
                        includeValueTypes = ValueTypesCheckBox.IsChecked == true;
                    });

                    if (_loadedAssemblies.Count == 0)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            TypesGrid.ItemsSource = Array.Empty<TypeInfoWrapper>();
                            TypeCountText.Text = "Types: 0";
                        });
                        return;
                    }

                    var types = TypeBrowser.SearchTypes(_loadedAssemblies, searchTerm, namespaceFilter)
                        .Where(t =>
                            (includeClasses && t.IsClass) ||
                            (includeInterfaces && t.IsInterface) ||
                            (includeEnums && t.IsEnum) ||
                            (includeValueTypes && t.IsValueType))
                        .Take(3000)
                        .ToList();

                    Dispatcher.Invoke(() =>
                    {
                        TypesGrid.ItemsSource = types;
                        TypeCountText.Text = $"Types: {types.Count}";
                    });
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to search types: {ex.Message}");
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

        private void BrowseType_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is TypeInfoWrapper typeWrapper)
            {
                try
                {
                    var win = new TypeInspectorWindow(typeWrapper.FullName)
                    {
                        Owner = Window.GetWindow(this)
                    };
                    win.Show();
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to browse type: {ex.Message}");
                }
            }
        }

        private void InspectType_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is TypeInfoWrapper typeWrapper)
            {
                try
                {
                    var win = new TypeInspectorWindow(typeWrapper.FullName)
                    {
                        Owner = Window.GetWindow(this)
                    };
                    win.Show();
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to inspect type: {ex.Message}");
                }
            }
        }
    }
}
