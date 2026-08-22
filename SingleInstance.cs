using System.Threading;

namespace DownloadMuck
{
    internal static class SingleInstance
    {
        private const string MutexName = @"Local\MuckDownloadManager_SingleInstance";
        private const string ShowEventName = @"Local\MuckDownloadManager_ShowWindow";

        private static Mutex? _mutex;
        private static EventWaitHandle? _showEvent;
        private static CancellationTokenSource? _listenCts;

        public static bool TryAcquire()
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                try
                {
                    using var pulse = EventWaitHandle.OpenExisting(ShowEventName);
                    pulse.Set();
                }
                catch { /* ilk örnek henüz event oluşturmamış olabilir */ }

                _mutex.Dispose();
                _mutex = null;
                return false;
            }

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            return true;
        }

        public static void StartListening(Action onShowRequested)
        {
            if (_showEvent == null) return;
            _listenCts = new CancellationTokenSource();
            var token = _listenCts.Token;
            var handle = _showEvent;

            _ = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (handle.WaitOne(500))
                            onShowRequested();
                    }
                    catch (ObjectDisposedException) { break; }
                    catch { /* ignore */ }
                }
            }, token);
        }

        public static void Release()
        {
            try { _listenCts?.Cancel(); } catch { /* ignore */ }
            try { _listenCts?.Dispose(); } catch { /* ignore */ }
            _listenCts = null;

            try { _showEvent?.Dispose(); } catch { /* ignore */ }
            _showEvent = null;

            try
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
            }
            catch { /* ignore */ }
            _mutex = null;
        }
    }
}