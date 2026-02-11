using System.Windows;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    public partial class TextPromptDialog : Window
    {
        public string ValueText => ValueBox.Text;

        public TextPromptDialog(string title, string prompt, string initialValue)
        {
            InitializeComponent();
            if (!string.IsNullOrWhiteSpace(title))
                Title = title;
            PromptText.Text = prompt ?? "Value:";
            ValueBox.Text = initialValue ?? string.Empty;
            ValueBox.SelectAll();
            ValueBox.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
