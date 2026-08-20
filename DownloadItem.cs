using System;

namespace DownloadMuck
{
    public class DownloadItem
    {
        public string FileName { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string FileSize { get; set; } = "-";
        public string FileType { get; set; } = "";
        public DateTime DateAdded { get; set; } = DateTime.Now;

        private string _status = "Hazır";
        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                IsDownloading = _status.Contains("İndiriliyor");
            }
        }

        public bool IsDownloading { get; set; } = false;
        public double ProgressValue { get; set; } = 0;
        public string StatusText { get; set; } = "";
        public string CurrentSpeed { get; set; } = "";

        public System.Windows.Media.ImageSource? FileIcon { get; set; }
    }
}