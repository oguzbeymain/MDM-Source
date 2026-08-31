using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DownloadMuck
{
    /// <summary>
    /// Ana pencere içinde karartmalı modal popup (uygulama dışına çıkmaz).
    /// </summary>
    internal static class AppModal
    {
        public static bool Confirm(string title, string message, string detail = "",
            string confirmText = "Onayla", string cancelText = "İptal", bool danger = false)
        {
            if (Application.Current?.MainWindow is MainWindow mw)
                return mw.ShowModalConfirm(title, message, detail, confirmText, cancelText, danger);

            // Fallback (MainWindow yoksa)
            var dlg = new ConfirmDialog(title, message, detail, confirmText, cancelText, danger);
            dlg.ShowDialog();
            return dlg.Confirmed;
        }

        public static void Info(string title, string message, string detail = "")
        {
            if (Application.Current?.MainWindow is MainWindow mw)
            {
                mw.ShowModalInfo(title, message, detail);
                return;
            }

            var dlg = new InfoDialog(title, message, detail);
            dlg.ShowDialog();
        }
    }
}
