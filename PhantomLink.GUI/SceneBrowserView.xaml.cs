using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    public partial class SceneBrowserView : UserControl
    {
        public SceneBrowserView()
        {
            InitializeComponent();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                StatusText.Text = "Loading scene info...";
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

                var info = ParseScene(response);
                ActiveSceneText.Text = $"Active: {Get(info, "active")} (buildIndex={Get(info, "activeBuildIndex")}, loaded={Get(info, "activeLoaded")})";
                SceneSummaryText.Text = $"Scenes loaded: {Get(info, "sceneCount")}";

                var scenes = new List<string>();
                if (info.TryGetValue("scenes", out var list) && !string.IsNullOrWhiteSpace(list))
                    scenes.AddRange(list.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)));

                ScenesList.ItemsSource = scenes;
                StatusText.Text = "Ready";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void ActiveObjects_Click(object sender, RoutedEventArgs e)
        {
            await RefreshActiveObjectsAsync();
        }

        private async Task RefreshActiveObjectsAsync()
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                StatusText.Text = "Counting objects...";
                var response = await IPCMeloaderClient.SendCommandAsync("GET_ACTIVE_OBJECTS", 8000);
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

                ActiveObjectsText.Text = response;
                StatusText.Text = "Ready";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void Reload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IPCMeloaderClient.IsConnected)
                {
                    StatusText.Text = "Not connected";
                    return;
                }

                StatusText.Text = "Reloading scene...";
                var response = await IPCMeloaderClient.SendCommandAsync("RELOAD_SCENE", 8000);
                StatusText.Text = response ?? "No response";
                await Task.Delay(250);
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private Dictionary<string, string> ParseScene(string response)
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

        private static string Get(Dictionary<string, string> dict, string key)
        {
            return dict.TryGetValue(key, out var v) ? v : string.Empty;
        }
    }
}
