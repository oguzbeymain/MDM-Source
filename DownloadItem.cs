using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace DownloadMuck
{
    public class DownloadItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private string _id = Guid.NewGuid().ToString("N")[..12];
        public string Id
        {
            get => _id;
            set => SetField(ref _id, string.IsNullOrWhiteSpace(value) ? _id : value);
        }

        private string _fileName = "";
        public string FileName
        {
            get => _fileName;
            set => SetField(ref _fileName, value);
        }

        private string _filePath = "";
        public string FilePath
        {
            get => _filePath;
            set => SetField(ref _filePath, value);
        }

        private string _fileSize = "-";
        public string FileSize
        {
            get => _fileSize;
            set => SetField(ref _fileSize, value);
        }

        private long _fileSizeBytes = -1;
        public long FileSizeBytes
        {
            get => _fileSizeBytes;
            set => SetField(ref _fileSizeBytes, value);
        }

        private string _fileType = "";
        public string FileType
        {
            get => _fileType;
            set => SetField(ref _fileType, value);
        }

        private DateTime _dateAdded = DateTime.Now;
        public DateTime DateAdded
        {
            get => _dateAdded;
            set => SetField(ref _dateAdded, value);
        }

        private string _status = "Hazır";
        public string Status
        {
            get => _status;
            set
            {
                if (!SetField(ref _status, value)) return;
                if (value.Contains("İptal", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("Tamamlandı", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("Duraklat", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("Hata", StringComparison.OrdinalIgnoreCase))
                {
                    IsDownloading = false;
                }
                else
                {
                    IsDownloading = value.Contains("İndiriliyor")
                        || value.Contains("Dosya bilgileri")
                        || value.Contains("Tek kanaldan")
                        || value.Contains("Torrent")
                        || value.Contains("Magnet")
                        || value.Contains("Ağ")
                        || value.Contains("bekleniyor")
                        || value.Contains("devam");
                }
            }
        }

        private bool _isDownloading;
        public bool IsDownloading
        {
            get => _isDownloading;
            set => SetField(ref _isDownloading, value);
        }

        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            set => SetField(ref _progressValue, value);
        }

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            set => SetField(ref _statusText, value);
        }

        private string _currentSpeed = "";
        public string CurrentSpeed
        {
            get => _currentSpeed;
            set => SetField(ref _currentSpeed, value);
        }

        private ImageSource? _fileIcon;
        public ImageSource? FileIcon
        {
            get => _fileIcon;
            set => SetField(ref _fileIcon, value);
        }

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set => SetField(ref _isChecked, value);
        }

        private string _categoryId = "All";
        public string CategoryId
        {
            get => _categoryId;
            set => SetField(ref _categoryId, value);
        }

        private string _url = "";
        public string Url
        {
            get => _url;
            set => SetField(ref _url, value);
        }

        public bool IsPausedState =>
            Status.Contains("Duraklat", StringComparison.OrdinalIgnoreCase);

        public bool IsErrorState =>
            Status.Contains("Hata", StringComparison.OrdinalIgnoreCase);

        public bool CanPauseResume => IsDownloading || IsPausedState || IsErrorState;

        public bool IsCompleted =>
            Status.Contains("Tamamlandı", StringComparison.OrdinalIgnoreCase);

        public bool IsCancelled =>
            Status.Contains("İptal", StringComparison.OrdinalIgnoreCase);

        /// <summary>Tamamlanan / iptal edilen satırlarda sağdaki aksiyon ikonları gizlenir.</summary>
        public bool ShowRowActions => !IsCompleted && !IsCancelled;

        public string PauseResumeGlyph => (IsPausedState || IsErrorState) ? "▶" : "⏸";

        public string PauseResumeTip => (IsPausedState || IsErrorState) ? "Devam et" : "Durdur";

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            if (name is nameof(Status) or nameof(IsDownloading))
            {
                OnPropertyChanged(nameof(IsPausedState));
                OnPropertyChanged(nameof(IsErrorState));
                OnPropertyChanged(nameof(CanPauseResume));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsCancelled));
                OnPropertyChanged(nameof(ShowRowActions));
                OnPropertyChanged(nameof(PauseResumeGlyph));
                OnPropertyChanged(nameof(PauseResumeTip));
            }
            return true;
        }
    }
}
