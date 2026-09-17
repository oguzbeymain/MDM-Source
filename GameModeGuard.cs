using System.Runtime.InteropServices;

namespace MDM
{
    /// <summary>
    /// Ayarlar > Oyun modu açıkken tam ekran oyun / sunum sırasında
    /// balon, mini oturum ve odak çalan pencereleri bastırır.
    /// </summary>
    public static class GameModeGuard
    {
        private const int QunsBusy = 2;
        private const int QunsRunningD3dFullScreen = 3;
        private const int QunsPresentationMode = 4;
        private const int QunsApp = 7;

        public static bool ShouldSuppressUi()
        {
            try
            {
                if (!AppSettingsStore.Load().GameModeEnabled)
                    return false;
            }
            catch
            {
                return false;
            }

            return IsUserBusyWithGameOrPresentation();
        }

        public static bool IsUserBusyWithGameOrPresentation()
        {
            try
            {
                if (SHQueryUserNotificationState(out int state) == 0)
                {
                    if (state is QunsBusy or QunsRunningD3dFullScreen or QunsPresentationMode or QunsApp)
                        return true;
                }
            }
            catch
            {
                /* SHQuery yoksa bastırma */
            }

            return false;
        }

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int pquns);
    }
}
