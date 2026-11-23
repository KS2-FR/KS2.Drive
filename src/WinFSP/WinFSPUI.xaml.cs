using MahApps.Metro.Controls;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

            await InstallWinFSPAsync();
            this.Close();
        }

        /// <summary>
        /// Downloads the WinFSP MSI to the temp folder, runs it (msiexec basic UI, "/qb+"), waits for it
        /// to finish and deletes the package. Sets <see cref="IsInstallSuccessFull"/> when the installer
        /// exits with code 0. Cancellation and errors are handled internally.
        /// </summary>
        private async Task InstallWinFSPAsync()
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    var Response = await client.GetAsync(RequiredWinFSP.PackageURL, CT);
                    //Never run an error page (404, proxy error...) as an installer
                    Response.EnsureSuccessStatusCode();
                    var ResponseContent = Response.Content;
                    var FileContent = await ResponseContent.ReadAsByteArrayAsync();
                    //An MSI package is an OLE compound file: check its signature before running it
                    byte[] MsiSignature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
                    if (FileContent.Length < MsiSignature.Length || !FileContent.Take(MsiSignature.Length).SequenceEqual(MsiSignature)) throw new InvalidDataException("The downloaded file is not an MSI package");
                    //Save a temporary file, with a unique name so that an existing file cannot be run instead
                    String TemporaryFilePath = Path.Combine(Path.GetTempPath(), $"winFSP_{Guid.NewGuid()}.msi");
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
                MessageBox.Show($"An error occurred while installing WinFSP : {ex.Message}");
            }
        }
    }
}
