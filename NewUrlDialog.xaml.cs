using System.Windows;
using System.Windows.Input;

namespace DownloadMuck
{
    public partial class NewUrlDialog : Window
    {
        public string Url => TxtUrl.Text?.Trim() ?? "";

        private bool _busy;

        public NewUrlDialog()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                TxtUrl.Focus();
                Keyboard.Focus(TxtUrl);
            };
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void BtnDownload_Click(object sender, RoutedEventArgs e) => TryAccept();

        private void TxtUrl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = false;
                return;
            }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            TryAccept();
        }

        private async void TxtUrl_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string t = Url;
            if (_busy || !LooksLikeUrl(t)) return;

            _busy = true;
            try
            {
                await System.Threading.Tasks.Task.Delay(180);
                if (Url == t)
                    TryAccept();
            }
            finally { _busy = false; }
        }

        private void TryAccept()
        {
            if (!LooksLikeUrl(Url))
            {
                TxtUrl.Focus();
                return;
            }
            DialogResult = true;
        }

        private static bool LooksLikeUrl(string t)
            => t.Length > 12
               && (t.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase)
                   || t.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase))
               && !t.Contains(' ')
               && t.Contains('.');
    }
}
