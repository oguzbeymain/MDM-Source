using System.Windows;

namespace MDM.Updater
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var window = new MainWindow();
            MainWindow = window;
            window.Show();

            try
            {
                await window.RunUpdateFlowAsync();
            }
            catch (Exception ex)
            {
                window.SetStatus($"Hata: {ex.Message}");
                await Task.Delay(2000);
                window.LaunchMainAppAndExit();
            }
        }
    }
}
