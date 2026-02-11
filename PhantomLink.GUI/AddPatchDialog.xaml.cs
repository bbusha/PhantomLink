using System.Windows;
using System.Windows.Controls;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    public partial class AddPatchDialog : Window
    {
        public PatchDefinition PatchDefinition { get; private set; }
        
        public string AssemblyName
        {
            get => AssemblyTextBox.Text;
            set => AssemblyTextBox.Text = value;
        }
        
        public string TypeName
        {
            get => TypeTextBox.Text;
            set => TypeTextBox.Text = value;
        }
        
        public string MethodName
        {
            get => MethodTextBox.Text;
            set => MethodTextBox.Text = value;
        }

        public AddPatchDialog()
        {
            InitializeComponent();
            PatchTypeComboBox.SelectedIndex = 0; // Select Prefix by default
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            if (ValidateInput())
            {
                PatchDefinition = new PatchDefinition
                {
                    AssemblyName = AssemblyTextBox.Text.Trim(),
                    TypeName = TypeTextBox.Text.Trim(),
                    MethodName = MethodTextBox.Text.Trim(),
                    PatchType = ((ComboBoxItem)PatchTypeComboBox.SelectedItem).Content.ToString(),
                    PatchMethod = PatchMethodTextBox.Text.Trim(),
                    Enabled = EnabledCheckBox.IsChecked ?? true,
                    Parameters = new System.Collections.Generic.Dictionary<string, object>()
                };

                DialogResult = true;
                Close();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private bool ValidateInput()
        {
            if (string.IsNullOrWhiteSpace(AssemblyTextBox.Text))
            {
                MessageBox.Show("Please enter an assembly name.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(TypeTextBox.Text))
            {
                MessageBox.Show("Please enter a type name.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(MethodTextBox.Text))
            {
                MessageBox.Show("Please enter a method name.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(PatchMethodTextBox.Text))
            {
                MessageBox.Show("Please enter a patch method name.");
                return false;
            }

            return true;
        }
    }
}
