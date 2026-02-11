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
    public partial class ClassesView : UserControl
    {
        private List<Assembly> _loadedAssemblies = new List<Assembly>();
        private CancellationTokenSource _searchCts;

        public ClassesView()
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
                    var includeAbstract = false;
                    var includeSealed = false;
                    var includePublic = false;

                    Dispatcher.Invoke(() =>
                    {
                        searchTerm = SearchBox.Text;
                        namespaceFilter = NamespaceBox.Text;
                        includeAbstract = AbstractCheckBox.IsChecked == true;
                        includeSealed = SealedCheckBox.IsChecked == true;
                        includePublic = PublicCheckBox.IsChecked == true;
                    });

                    if (_loadedAssemblies.Count == 0)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ClassesGrid.ItemsSource = Array.Empty<ClassInfoWrapper>();
                            ClassCountText.Text = "Classes: 0";
                        });
                        return;
                    }

                    var classes = TypeBrowser.SearchClasses(_loadedAssemblies, searchTerm, namespaceFilter)
                        .Where(c =>
                        {
                            if (!includeAbstract && c.IsAbstract)
                                return false;
                            if (!includeSealed && c.IsSealed)
                                return false;
                            if (!includePublic && c.IsPublic)
                                return false;
                            return true;
                        })
                        .Take(3000)
                        .ToList();

                    Dispatcher.Invoke(() =>
                    {
                        ClassesGrid.ItemsSource = classes;
                        ClassCountText.Text = $"Classes: {classes.Count}";
                    });
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to search classes: {ex.Message}");
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

        private void BrowseClass_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is ClassInfoWrapper classWrapper)
            {
                try
                {
                    var win = new TypeInspectorWindow(classWrapper.FullName)
                    {
                        Owner = Window.GetWindow(this)
                    };
                    win.Show();
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to browse class: {ex.Message}");
                }
            }
        }

        private void InspectClass_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is ClassInfoWrapper classWrapper)
            {
                try
                {
                    var win = new TypeInspectorWindow(classWrapper.FullName)
                    {
                        Owner = Window.GetWindow(this)
                    };
                    win.Show();
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to inspect class: {ex.Message}");
                }
            }
        }
    }
}
