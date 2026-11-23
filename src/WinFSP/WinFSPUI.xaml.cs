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
    /// Dialog shown when the required WinFSP version is not installed. Offers to download
    /// the WinFSP MSI package and run its installer.
    /// </summary>
    public partial class WinFSPUI : MetroWindow
    {
        /// <summary>
        /// Description of the WinFSP package to install (MSI product code, download URL, display name).
        /// </summary>
        private (String MsiProductCode, String PackageURL, String VersionName) RequiredWinFSP;
        /// <summary>
        /// Cancels the download when the window is closed.
        /// </summary>
        private CancellationTokenSource CTS;
        private CancellationToken CT;

        /// <summary>
        /// <c>true</c> once the MSI installer has completed with exit code 0.
        /// </summary>
        public bool IsInstallSuccessFull { get; set; } = false;

        /// <summary>
        /// Initializes the dialog and displays which WinFSP version is required.
        /// </summary>
        /// <param name="RequiredWinFSP">MSI product code, download URL and display name of the required WinFSP version.</param>
        public WinFSPUI((String MsiProductCode, String PackageURL, String VersionName) RequiredWinFSP)
        {
            InitializeComponent();
            this.RequiredWinFSP = RequiredWinFSP;
            CTS = new CancellationTokenSource();
            CT = CTS.Token;

            InstallWinFSP.Content = $"KS² Drive requires {RequiredWinFSP.VersionName} to run.\nThis dependency is not installed.\n\nClick 'Install' to install it now.\n\nNote : If a previous version of WinFSP is already installed on your system,\nplease uninstall it first";
        }

        /// <summary>
        /// Closes the dialog without installing.
        /// </summary>
        private void Bt_Quit_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        /// <summary>
        /// Requests cancellation of any download in progress when the window is closing.
        /// </summary>
        private void MetroWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            CTS.Cancel();
        }

        /// <summary>
        /// Switches to the "installing" panel, runs the download and installation, then closes the dialog.
        /// </summary>
        private async void Btn_Install_Click(object sender, RoutedEventArgs e)
        {
            InstallPanel.Visibility = Visibility.Hidden;
            InstallingPanel.Visibility = Visibility.Visible;
            btn_Install.IsEnabled = false;

            await IntallWinFSPAsync();
            this.Close();
        }

        /// <summary>
        /// Downloads the WinFSP MSI to the temp folder, runs it (msiexec basic UI, "/qb+"), waits for it
        /// to finish and deletes the package. Sets <see cref="IsInstallSuccessFull"/> when the installer
        /// exits with code 0. Cancellation and errors are handled internally.
        /// </summary>
        private async Task IntallWinFSPAsync()
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
                        if(ExitCode == 0) IsInstallSuccessFull = true;
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
        }
    }
}
