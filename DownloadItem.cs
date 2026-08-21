using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace DownloadMuck
{
    public class DownloadItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

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
                IsDownloading = value.Contains("İndiriliyor")
                    || value.Contains("Dosya bilgileri")
                    || value.Contains("Tek kanaldan");
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

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
