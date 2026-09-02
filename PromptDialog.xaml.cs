using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DownloadMuck
{
    public partial class PromptDialog : UserControl
    {
        public event Action? Accepted;
        public event Action? Cancelled;

        private string _originalExtension = "";
        private bool _lockExtensionMode;

        public string ResultText => TxtInput.Text;
        public bool AllowExtensionChange => ChkChangeExtension.IsChecked == true;

        public PromptDialog()
        {
            InitializeComponent();
        }

        public void Configure(string title, string prompt, string defaultValue = "", bool extensionLockMode = false)
        {
            _lockExtensionMode = extensionLockMode;
            _originalExtension = Path.GetExtension(defaultValue);
            TxtTitle.Text = title;
            TxtPrompt.Text = prompt;
            TxtInput.Text = defaultValue;

            if (extensionLockMode)
            {
                ChkChangeExtension.Visibility = Visibility.Visible;
                ChkChangeExtension.IsChecked = false;
                string nameOnly = Path.GetFileNameWithoutExtension(defaultValue);
                TxtInput.Text = defaultValue;
                if (!string.IsNullOrEmpty(nameOnly) && TxtInput.Text.StartsWith(nameOnly))
                    TxtInput.Select(0, nameOnly.Length);
                else
                    TxtInput.SelectAll();
            }
            else
            {
                ChkChangeExtension.Visibility = Visibility.Collapsed;
                TxtInput.SelectAll();
            }

            TxtInput.Focus();
            Keyboard.Focus(TxtInput);
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
                Cancelled?.Invoke();
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) => Accept();

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

        private void Accept()
        {
            if (string.IsNullOrWhiteSpace(TxtInput.Text))
            {
                TxtInput.Focus();
                return;
            }

            if (_lockExtensionMode && !AllowExtensionChange)
            {
                string typed = TxtInput.Text.Trim();
                string typedExt = Path.GetExtension(typed);
                if (!string.Equals(typedExt, _originalExtension, System.StringComparison.OrdinalIgnoreCase))
                {
                    string baseName = Path.GetFileNameWithoutExtension(typed);
                    if (string.IsNullOrWhiteSpace(baseName))
                        baseName = Path.GetFileNameWithoutExtension(TxtInput.Text) ?? "dosya";
                    TxtInput.Text = baseName + _originalExtension;
                }
            }

            Accepted?.Invoke();
        }
    }
}
