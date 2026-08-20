using System.Windows;

namespace DownloadMuck
{
    public partial class UpdateWindow : Window
    {
        public UpdateWindow()
        {
            InitializeComponent();
        }

        public void SetStatus(string message)
        {
            TxtStatus.Text = message;
        }
    }
}
