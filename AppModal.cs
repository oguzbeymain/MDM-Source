using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MDM
{
    /// <summary>
    /// Ana pencere içinde karartmalı modal popup (uygulama dışına çıkmaz).
    /// </summary>
    internal static class AppModal
    {
        public static bool Confirm(string title, string message, string detail = "",
            string? confirmText = null, string? cancelText = null, bool danger = false,
            bool accentCancel = false)
        {
            // Varsayılan buton yazıları çalışma anında dile göre çözülür
            string ok = string.IsNullOrWhiteSpace(confirmText) ? Loc.T("dialog.confirm", "Onayla") : confirmText!;
            string cancel = string.IsNullOrWhiteSpace(cancelText) ? Loc.T("dialog.cancel", "İptal") : cancelText!;

            if (Application.Current?.MainWindow is MainWindow mw)
                return mw.ShowModalConfirm(title, message, detail, ok, cancel, danger, accentCancel);

            var dlg = new ConfirmDialog(title, message, detail, ok, cancel, danger, accentCancel);
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
