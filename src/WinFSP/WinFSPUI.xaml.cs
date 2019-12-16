using MahApps.Metro.Controls;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace KS2Drive.WinFSP
{
    /// <summary>
    /// Logique d'interaction pour WinFSPUI.xaml
    /// </summary>
    public partial class WinFSPUI : MetroWindow
    {
        private (String MsiProductCode, String PackageURL, String VersionName) RequiredWinFSP;
        private CancellationTokenSource CTS;
        private CancellationToken CT;

        public WinFSPUI((String MsiProductCode, String PackageURL, String VersionName) RequiredWinFSP)
        {
            InitializeComponent();
            this.RequiredWinFSP = RequiredWinFSP;
            CTS = new CancellationTokenSource();
            CT = CTS.Token;

            InstallWinFSP.Content = $"KS² Drive requires {RequiredWinFSP.VersionName} to run.\nThis dependency is not installed.\n\nClick 'Install' to install it now.\n\nNote : If a previous version of WinFSP is already installed on your system,\nplease uninstall it first";
        }

        private void Bt_Quit_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private async void Btn_Install_Click(object sender, RoutedEventArgs e)
        {
            InstallPanel.Visibility = Visibility.Hidden;
            InstallingPanel.Visibility = Visibility.Visible;
            btn_Install.IsEnabled = false;

            var InstallResult = await IntallWinFSPAsync();
            this.DialogResult = InstallResult;
            this.Close();
        }

        private async Task<bool> IntallWinFSPAsync()
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    var Response = await client.GetAsync(RequiredWinFSP.PackageURL, CT);
                    var ResponseContent = Response.Content;
                    var FileContent = await ResponseContent.ReadAsByteArrayAsync();
                    //Save a temporary file
                    String TemporaryFilePath = Path.Combine(Path.GetTempPath(), "winFSP.msi");
                    File.WriteAllBytes(TemporaryFilePath, FileContent);
                    CT.ThrowIfCancellationRequested();
                    //msiexec parameters : https://stackoverflow.com/questions/11947909/how-to-report-msi-installation-status-on-quiet-install
                    using (var InstallProcess = new Process { StartInfo = new ProcessStartInfo(TemporaryFilePath, "/qb+"), })
                    {
                        InstallProcess.Start();
                        InstallProcess.WaitForExit();
                        var ExitCode = InstallProcess.ExitCode;
                        File.Delete(TemporaryFilePath);
                        return ExitCode == 0;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                //Task was cancelled by user
                Debug.WriteLine("WinFSP install cancelled");
            }
            catch (Exception ex)
            {
                MessageBox.Show("An error occur when installing WinFSP");
            }
            return false;
        }

        private void MetroWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
           CTS.Cancel();
        }
    }
}
