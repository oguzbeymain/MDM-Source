using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace DownloadMuck
{
    public partial class CategoryCreateDialog : Window
    {
        public string CategoryName => TxtName.Text?.Trim() ?? "";
        public string FolderPath => TxtFolder.Text?.Trim() ?? "";

        public CategoryCreateDialog(string defaultFolder)
        {
            InitializeComponent();
            TxtName.Text = "Yeni kategori";
            TxtName.SelectAll();
            string suggested = Path.Combine(defaultFolder, "Yeni kategori");
            TxtFolder.Text = suggested;
            TxtName.TextChanged += (_, _) =>
            {
                string n = CategoryStore.SanitizeFolderName(CategoryName);
                if (string.IsNullOrWhiteSpace(n)) return;
                string parent = Path.GetDirectoryName(TxtFolder.Text) ?? defaultFolder;
                // Sadece varsayilan oneriyi otomatik guncelle
                if (TxtFolder.Text.StartsWith(defaultFolder, System.StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(TxtFolder.Text))
                    TxtFolder.Text = Path.Combine(defaultFolder, n);
            };
            Loaded += (_, _) =>
            {
                TxtName.Focus();
                Keyboard.Focus(TxtName);
            };
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Kategori klasörünü seçin"
            };
            if (!string.IsNullOrWhiteSpace(TxtFolder.Text) && Directory.Exists(Path.GetDirectoryName(TxtFolder.Text)))
                dialog.InitialDirectory = Path.GetDirectoryName(TxtFolder.Text)!;
            else if (Directory.Exists(TxtFolder.Text))
                dialog.InitialDirectory = TxtFolder.Text;

            if (dialog.ShowDialog() == true)
            {
                string chosen = dialog.FolderName;
                string name = CategoryStore.SanitizeFolderName(CategoryName);
                // Secilen klasorun kendisi hedef; altinda kategori adi olusturulacak
                TxtFolder.Text = Path.Combine(chosen, name);
            }
        }

        private void Txt_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; Accept(); }
            else if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) => Accept();
        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Accept()
        {
            if (string.IsNullOrWhiteSpace(CategoryName))
            {
                TxtName.Focus();
                return;
            }
            if (string.IsNullOrWhiteSpace(FolderPath))
            {
                TxtFolder.Focus();
                return;
            }
            DialogResult = true;
        }
    }
}
