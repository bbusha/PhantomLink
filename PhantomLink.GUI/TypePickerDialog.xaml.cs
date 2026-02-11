using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PhantomLink.Core;

namespace PhantomLink.GUI
{
    public partial class TypePickerDialog : Window
    {
        public Type SelectedType { get; private set; }

        public TypePickerDialog(string header, IEnumerable<Type> candidates)
        {
            InitializeComponent();
            HeaderText.Text = header ?? "Select a type";
            TypesList.ItemsSource = candidates?.ToList() ?? new List<Type>();
            TypesList.SelectionChanged += (_, __) =>
            {
                SelectedType = TypesList.SelectedItem as Type;
            };

            if (TypesList.Items.Count > 0)
                TypesList.SelectedIndex = 0;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            SelectedType = TypesList.SelectedItem as Type;
            DialogResult = SelectedType != null;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
