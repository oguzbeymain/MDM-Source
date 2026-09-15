namespace MDM
{
    public interface ITransferBackend
    {
        bool IsPaused { get; }
        bool IsDownloading { get; }
        bool IsCancelled { get; }
        bool CompletedSuccessfully { get; }

        event Action<double>? ProgressChanged;
        event Action<string>? StatusChanged;
        event Action<string, string>? SpeedAndTimeChanged;
        event Action<long>? TotalSizeKnown;
        event Action<string, string>? OutputResolved;

        Task StartOrResumeDownloadAsync();
        void Pause();
        void Cancel();
    }

    public interface IMdmPlugin
    {
        string Name { get; }
        ITransferBackend? TryCreate(string url, string savePath, int threadCount);
    }
}
