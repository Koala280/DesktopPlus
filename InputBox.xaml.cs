using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DesktopPlus
{
    /// <summary>
    /// Interaktionslogik für Window1.xaml
    /// </summary>
    public partial class InputBox : Window
    {
        public string ResultText { get; private set; } = string.Empty;

        public InputBox(string message, string defaultText = "")
        {
            InitializeComponent();
            LblMessage.Text = message;
            TxtInput.Text = defaultText;
            Loaded += (_, __) =>
            {
                TxtInput.Focus();
                TxtInput.SelectAll();
            };
        }

        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            ResultText = TxtInput.Text;
            this.DialogResult = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }

}
