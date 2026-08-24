using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace DownloadMuck
{
    public partial class NewUrlDialog : Window
    {
        public string Url => TxtUrl.Text?.Trim() ?? "";
        public IReadOnlyList<string> Urls
        {
            get
            {
                var list = UrlClassifier.ExtractDownloadUrls(TxtUrl.Text);
                if (list.Count > 0)
                    return list;
                if (UrlClassifier.CanDownloadNow(UrlClassifier.Classify(Url)))
                    return new[] { Url };
                return list;
            }
        }
        public bool GrabLinks { get; private set; }

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

        private void BtnBrowseTorrent_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Torrent dosyası seçin",
                Filter = "Torrent (*.torrent)|*.torrent|Tüm dosyalar|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FileName))
                TxtUrl.Text = dlg.FileName;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void BtnDownload_Click(object sender, RoutedEventArgs e) => TryAccept();

        private void BtnGrab_Click(object sender, RoutedEventArgs e)
        {
            GrabLinks = true;
            DialogResult = true;
        }

        private void TxtUrl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = false;
                return;
            }
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                TryAccept();
            }
        }

        private async void TxtUrl_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string t = Url;
            var urls = UrlClassifier.ExtractHttpUrls(t);
            if (_busy || urls.Count != 1 || t.Contains('\n') || t.Contains('\r'))
                return;

            _busy = true;
            try
            {
                await System.Threading.Tasks.Task.Delay(180);
                if (Url == t && UrlClassifier.ExtractHttpUrls(Url).Count == 1)
                    TryAccept();
            }
            finally { _busy = false; }
        }

        private void TryAccept()
        {
            if (Urls.Count > 0 || UrlClassifier.CanDownloadNow(UrlClassifier.Classify(Url)))
            {
                DialogResult = true;
                return;
            }

            TxtUrl.Focus();
        }
    }
}
