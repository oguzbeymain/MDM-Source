using System.Threading;

namespace DownloadMuck
{
    internal static class SingleInstance
    {
        private const string MutexName = @"Local\MuckDownloadManager_SingleInstance";
        private const string ShowEventName = @"Local\MuckDownloadManager_ShowWindow";
        private const string ExitEventName = @"Local\MuckDownloadManager_ExitApp";

        private static Mutex? _mutex;
        private static EventWaitHandle? _showEvent;
        private static EventWaitHandle? _exitEvent;
        private static CancellationTokenSource? _listenCts;

        public static bool TryAcquire()
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
            return true;
        }

        public static void RequestShow()
        {
            try
            {
                using var pulse = EventWaitHandle.OpenExisting(ShowEventName);
                pulse.Set();
            }
            catch { /* ignore */ }
        }

        public static void RequestExit()
        {
            try
            {
                using var pulse = EventWaitHandle.OpenExisting(ExitEventName);
                pulse.Set();
            }
            catch { /* ignore */ }
        }

        /// <summary>Eski örneğin kapanması için kısa süre bekler; mutex alınırsa true.</summary>
        public static bool TryAcquireAfterExitRequest(int waitMs = 4000)
        {
            RequestExit();
            int slice = 150;
            int waited = 0;
            while (waited < waitMs)
            {
                Thread.Sleep(slice);
                waited += slice;
                if (TryAcquire())
                    return true;
            }
            return false;
        }

        public static void StartListening(Action onShowRequested, Action? onExitRequested = null)
        {
            if (_showEvent == null && _exitEvent == null) return;
            _listenCts = new CancellationTokenSource();
            var token = _listenCts.Token;
            var show = _showEvent;
            var exit = _exitEvent;

            _ = Task.Run(() =>
            {
                var handles = new List<WaitHandle>();
                if (show != null) handles.Add(show);
                if (exit != null) handles.Add(exit);
                if (handles.Count == 0) return;

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        int idx = WaitHandle.WaitAny(handles.ToArray(), 500);
                        if (idx == WaitHandle.WaitTimeout) continue;
                        if (show != null && ReferenceEquals(handles[idx], show))
                            onShowRequested();
                        else if (exit != null && onExitRequested != null && ReferenceEquals(handles[idx], exit))
                            onExitRequested();
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
            try { _exitEvent?.Dispose(); } catch { /* ignore */ }
            _exitEvent = null;

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
