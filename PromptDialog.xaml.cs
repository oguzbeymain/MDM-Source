using System.Windows;
using System.Windows.Input;

namespace DownloadMuck
{
    public partial class PromptDialog : Window
    {
        public string ResultText => TxtInput.Text;

        public PromptDialog(string title, string prompt, string defaultValue = "")
        {
            InitializeComponent();
            TxtTitle.Text = title;
            TxtPrompt.Text = prompt;
            TxtInput.Text = defaultValue;
            TxtInput.SelectAll();
            Loaded += (_, _) =>
            {
                TxtInput.Focus();
                Keyboard.Focus(TxtInput);
            };
        }

        private void TxtInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Accept();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = false;
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) => Accept();

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Accept()
        {
            if (string.IsNullOrWhiteSpace(TxtInput.Text))
            {
                TxtInput.Focus();
                return;
            }
            DialogResult = true;
        }
    }
}
