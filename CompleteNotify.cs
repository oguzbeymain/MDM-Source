using System.Runtime.InteropServices;

namespace DownloadMuck
{
    public static class CompleteNotify
    {
        private const uint MbIconAsterisk = 0x00000040;

        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint type);

        public static void PlayIfEnabled()
        {
            if (!AppSettingsStore.Load().NotifyOnComplete)
                return;
            try { MessageBeep(MbIconAsterisk); }
            catch { /* ignore */ }
        }
    }
}
