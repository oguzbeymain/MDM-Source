using System.Windows;

namespace MDM
{
    public static class CompleteNotify
    {
        /// <summary>
        /// Yalnızca Windows tepsi bildirimi (kendi sesi). Uygulama MessageBeep kullanmaz — çift ses olmaz.
        /// </summary>
        public static void PlayIfEnabled(string? fileName = null)
        {
            if (!AppSettingsStore.Load().NotifyOnComplete)
                return;

            string name = string.IsNullOrWhiteSpace(fileName) ? "Dosya" : fileName.Trim();
            try
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (Application.Current?.MainWindow is MainWindow mw)
                        mw.ShowDownloadCompleteTip(name);
                });
            }
            catch { /* ignore */ }
        }
    }
}
