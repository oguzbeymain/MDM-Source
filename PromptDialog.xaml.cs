using System.IO;
using System.Windows;
using System.Windows.Input;

namespace DownloadMuck
{
    public partial class PromptDialog : Window
    {
        private readonly string _originalExtension;
        private readonly bool _lockExtensionMode;

        public string ResultText => TxtInput.Text;
        public bool AllowExtensionChange => ChkChangeExtension.IsChecked == true;

        public PromptDialog(string title, string prompt, string defaultValue = "", bool extensionLockMode = false)
        {
            InitializeComponent();
            TxtTitle.Text = title;
            TxtPrompt.Text = prompt;
            TxtInput.Text = defaultValue;
            _lockExtensionMode = extensionLockMode;
            _originalExtension = Path.GetExtension(defaultValue);

            if (extensionLockMode)
            {
                ChkChangeExtension.Visibility = Visibility.Visible;
                ChkChangeExtension.IsChecked = false;
                // Varsayilan: sadece dosya adini sec (uzanti haric)
                string nameOnly = Path.GetFileNameWithoutExtension(defaultValue);
                TxtInput.Text = defaultValue;
                Loaded += (_, _) =>
                {
                    TxtInput.Focus();
                    Keyboard.Focus(TxtInput);
                    if (!string.IsNullOrEmpty(nameOnly) && TxtInput.Text.StartsWith(nameOnly))
                        TxtInput.Select(0, nameOnly.Length);
                    else
                        TxtInput.SelectAll();
                };
            }
            else
            {
                TxtInput.SelectAll();
                Loaded += (_, _) =>
                {
                    TxtInput.Focus();
                    Keyboard.Focus(TxtInput);
                };
            }
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

            if (_lockExtensionMode && !AllowExtensionChange)
            {
                string typed = TxtInput.Text.Trim();
                string typedExt = Path.GetExtension(typed);
                if (!string.Equals(typedExt, _originalExtension, System.StringComparison.OrdinalIgnoreCase))
                {
                    // Uzanti korunur
                    string baseName = Path.GetFileNameWithoutExtension(typed);
                    if (string.IsNullOrWhiteSpace(baseName))
                        baseName = Path.GetFileNameWithoutExtension(TxtInput.Text) ?? "dosya";
                    TxtInput.Text = baseName + _originalExtension;
                }
            }

            DialogResult = true;
        }
    }
}
