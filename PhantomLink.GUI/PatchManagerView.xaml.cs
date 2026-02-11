using System.Windows;
using System.Windows.Controls;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    public partial class PatchManagerView : UserControl
    {
        public PatchManagerView()
        {
            InitializeComponent();
            LoadPatches();
        }

        private void LoadPatches()
        {
            PatchesGrid.ItemsSource = PatchManager.GetPatches();
            PatchStatus.Text = $"Loaded {PatchesGrid.Items.Count} patches";
        }

        private void AddPatch_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddPatchDialog();
            if (dialog.ShowDialog() == true)
            {
                PatchManager.AddPatch(dialog.PatchDefinition);
                LoadPatches();
            }
        }

        private void RemovePatch_Click(object sender, RoutedEventArgs e)
        {
            if (PatchesGrid.SelectedItem is PatchDefinition patch)
            {
                PatchManager.RemovePatch(patch);
                LoadPatches();
            }
            else
            {
                MessageBox.Show("Please select a patch to remove.");
            }
        }

        private void EnablePatch_Click(object sender, RoutedEventArgs e)
        {
            if (PatchesGrid.SelectedItem is PatchDefinition patch)
            {
                PatchManager.EnablePatch(patch);
                LoadPatches();
            }
        }

        private void DisablePatch_Click(object sender, RoutedEventArgs e)
        {
            if (PatchesGrid.SelectedItem is PatchDefinition patch)
            {
                PatchManager.DisablePatch(patch);
                LoadPatches();
            }
        }

        private void ApplyAll_Click(object sender, RoutedEventArgs e)
        {
            _ = ApplyAllEnabledPatchesAsync();
        }

        private void RemoveAll_Click(object sender, RoutedEventArgs e)
        {
            _ = RemoveAllEnabledPatchesAsync();
        }

        private async System.Threading.Tasks.Task ApplyAllEnabledPatchesAsync()
        {
            if (!IPCMeloaderClient.IsConnected)
            {
                PatchStatus.Text = "Not connected to game";
                return;
            }

            int applied = 0;
            int failed = 0;

            foreach (PatchDefinition patch in PatchesGrid.Items)
            {
                if (!patch.Enabled)
                    continue;

                var response = await IPCMeloaderClient.SendCommandAsync(
                    $"APPLY_HARMONY_PATCH|{patch.TypeName}|{patch.MethodName}|{patch.PatchType}|{patch.PatchMethod}");

                if (response != null && response.StartsWith("SUCCESS|"))
                    applied++;
                else
                    failed++;
            }

            PatchStatus.Text = $"Apply requested. Success: {applied}, Failed: {failed}";
        }

        private async System.Threading.Tasks.Task RemoveAllEnabledPatchesAsync()
        {
            if (!IPCMeloaderClient.IsConnected)
            {
                PatchStatus.Text = "Not connected to game";
                return;
            }

            int removed = 0;
            int failed = 0;

            foreach (PatchDefinition patch in PatchesGrid.Items)
            {
                if (!patch.Enabled)
                    continue;

                var id = $"{patch.TypeName}::{patch.MethodName}::{patch.PatchType}::{patch.PatchMethod}";
                var response = await IPCMeloaderClient.SendCommandAsync($"REMOVE_HARMONY_PATCH|{id}");
                if (response != null && response.StartsWith("SUCCESS|"))
                    removed++;
                else
                    failed++;
            }

            PatchStatus.Text = $"Remove requested. Success: {removed}, Failed: {failed}";
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            PatchManager.SavePatches();
            PatchStatus.Text = "Patches saved";
        }

        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            PatchManager.LoadPatches();
            LoadPatches();
            PatchStatus.Text = "Patches reloaded";
        }
    }
}
