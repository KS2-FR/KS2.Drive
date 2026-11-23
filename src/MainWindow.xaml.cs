using KS2Drive.Config;
using KS2Drive.Log;
using KS2Drive.WinFSP;
using MahApps.Metro.Controls;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace KS2Drive
{
    /// <summary>
    /// Hidden main window of the tray application. Owns the notification-area icon and its context menu,
    /// hosts the WinFSP service that mounts the remote repository as a drive, and displays the operation log.
    /// </summary>
    public partial class MainWindow : MetroWindow
    {
        /// <summary>WinFSP service hosting the file system.</summary>
        private FSPService Service;
        /// <summary>Whether the drive is currently mounted.</summary>
        private bool IsMounted = false;
        /// <summary>Background thread running the WinFSP service loop.</summary>
        private Thread T;
        private Configuration AppConfiguration;
        /// <summary>Log entries displayed in the log list (populated from service events).</summary>
        public ObservableCollection<LogListItem> ItemsToLog = new ObservableCollection<LogListItem>();

        //Set the WinFSP version against which the program was built (see /Reference folder in source)
        //Installer ProductCode (can be extracted from MSI file with superorca http://www.pantaray.com/msi_super_orca.html)
        //Installer URL
        //WinFSP Version Name
        private (String MsiProductCode, String PackageURL, String VersionName) RequiredWinFSP = ("{4EE1629A-41FB-4261-847A-C12B466B017D}", "https://github.com/billziss-gh/winfsp/releases/download/v1.5/winfsp-1.5.20002.msi", "WinFsp 2019.3");
        private System.Windows.Forms.NotifyIcon AppNotificationIcon;
        private ContextMenu AppMenu;

        /// <summary>
        /// Initializes the main window: checks that the required WinFSP version is installed (offering
        /// to install it, and quitting if that fails), creates the tray icon, starts the WinFSP service
        /// on a background thread and subscribes to its events, then either auto-mounts the drive or
        /// opens the configuration dialog when the application is not configured yet.
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();

            AppConfiguration = ((App)Application.Current).AppConfiguration;

            AppMenu = (ContextMenu)this.FindResource("NotifierContextMenu");
            ((MenuItem)AppMenu.Items[0]).IsEnabled = AppConfiguration.IsConfigured;

            this.Hide();

            //Check installed WinFSP version
            if (!Tools.IsMsiIntalled(RequiredWinFSP.MsiProductCode))
            {
                WinFSPUI Dialog = new WinFSPUI(RequiredWinFSP);
                Dialog.ShowDialog();
                if (!Dialog.IsInstallSuccessFull)
                {
                    QuitApp();
                    return;
                }
            }

            #region Window events

            this.Closing += (s, e) =>
            {
                e.Cancel = true;
                this.Hide();
            };

            #endregion

            #region Icon

            AppNotificationIcon = new System.Windows.Forms.NotifyIcon();
            Stream iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/KS2Drive;component/Resources/Main.ico")).Stream;
            AppNotificationIcon.Icon = new System.Drawing.Icon(iconStream);
            AppNotificationIcon.Visible = true;
            AppNotificationIcon.Text = "KS² Drive";
            AppNotificationIcon.BalloonTipText = "KS² Drive";
            AppNotificationIcon.MouseClick += (s, e) => { this.Dispatcher.Invoke(() => { AppMenu.IsOpen = !AppMenu.IsOpen; }); };

            #endregion

            LogList.ItemsSource = ItemsToLog;

            #region Try to start WinFSP Service

            try
            {
                Service = new FSPService();

                #region Service Events

                Service.RepositoryActionPerformed += (s1, e1) =>
                {
                    Dispatcher.Invoke(() => ItemsToLog.Add(e1));
                    if (!e1.Result.Equals("STATUS_SUCCESS"))
                    {
                        if (!e1.AllowRetryOrRecover) Dispatcher.Invoke(() => AppNotificationIcon.ShowBalloonTip(3000, "KS² Drive", $"The action {e1.Method} for the file {e1.File} failed", System.Windows.Forms.ToolTipIcon.Warning));
                        else Dispatcher.Invoke(() => AppNotificationIcon.ShowBalloonTip(3000, "KS² Drive", $"The action {e1.Method} for the file {e1.File} failed. You can recover this file via the LOG menu", System.Windows.Forms.ToolTipIcon.Warning));
                    };
                };

                Service.RepositoryAuthenticationFailed += (s2, e2) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        AppNotificationIcon.ShowBalloonTip(3000, "KS² Drive", $"Your credentials are invalid. Please update them in the Configuration panel", System.Windows.Forms.ToolTipIcon.Error);
                    });
                };

                #endregion

                T = new Thread(() => Service.Run());
                T.Start();
            }
            catch
            {
                MessageBox.Show("Cannot start WinFSP service. KS² Drive will now close", "", MessageBoxButton.OK, MessageBoxImage.Error);
                QuitApp();
                return;
            }

            #endregion

            Dispatcher.Invoke(() => AppNotificationIcon.ShowBalloonTip(3000, "KS² Drive", $"KS² Drive has started", System.Windows.Forms.ToolTipIcon.Info));

            if (this.AppConfiguration.IsConfigured)
            {
                if (AppConfiguration.AutoMount) MountDrive();
            }
            else
            {
                MenuConfigure_Click(this, null);
            }
        }

        /// <summary>
        /// Mounts the drive with the current configuration, updates the tray menu (Mount becomes Unmount,
        /// Configure is disabled), clears the log and opens the drive in Explorer. Shows an error on failure.
        /// </summary>
        private void MountDrive()
        {
            try
            {
                Service.Mount(this.AppConfiguration);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return;
            }

            ItemsToLog.Clear();
            ((MenuItem)AppMenu.Items[0]).Header = "_UNMOUNT";
            IsMounted = true;
            ((MenuItem)AppMenu.Items[2]).IsEnabled = false;
            Process.Start($@"{this.AppConfiguration.DriveLetter}:\");
        }

        /// <summary>
        /// Unmounts the drive and restores the tray menu (Mount label, Configure re-enabled).
        /// </summary>
        private void UnmountDrive()
        {
            try
            {
                Service.Unmount();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }

            ((MenuItem)AppMenu.Items[0]).Header = "_MOUNT";
            IsMounted = false;
            ((MenuItem)AppMenu.Items[2]).IsEnabled = true;
            ((MenuItem)AppMenu.Items[2]).ToolTip = null;
        }

        /// <summary>
        /// Hides the window when it is minimized (the application lives in the notification area).
        /// </summary>
        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == System.Windows.WindowState.Minimized) this.Hide();
            base.OnStateChanged(e);
        }

        /// <summary>
        /// Removes the tray icon, stops the WinFSP service and shuts down the application.
        /// </summary>
        private void QuitApp()
        {
            if (AppNotificationIcon != null)
            {
                AppNotificationIcon.Visible = false;
                AppNotificationIcon.Dispose();
            }
            Service?.Stop();
            Application.Current.Shutdown();
        }

        #region Menu actions

        /// <summary>
        /// Toggles the drive between mounted and unmounted.
        /// </summary>
        private void MenuMount_Click(object sender, RoutedEventArgs e)
        {
            if (IsMounted) UnmountDrive();
            else MountDrive();
        }

        /// <summary>
        /// Opens the configuration dialog and enables the Mount menu item once the application is configured.
        /// </summary>
        private void MenuConfigure_Click(object sender, RoutedEventArgs e)
        {
            ConfigurationUI OptionWindow = new ConfigurationUI();
            OptionWindow.ShowDialog();
            if (AppConfiguration.IsConfigured) ((MenuItem)AppMenu.Items[0]).IsEnabled = true;
        }

        /// <summary>
        /// Shows the log window.
        /// </summary>
        private void MenuLog_Click(object sender, RoutedEventArgs e)
        {
            if (!this.IsVisible) this.Show();
        }

        /// <summary>
        /// Quits the application.
        /// </summary>
        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            QuitApp();
        }

        /// <summary>
        /// Shows the About dialog.
        /// </summary>
        private void MenuAbout_Click(object sender, RoutedEventArgs e)
        {
            About.About A = new About.About();
            A.ShowDialog();
        }

        #endregion

        #region Log actions

        /// <summary>
        /// Clears all log entries.
        /// </summary>
        private void bt_ClearLog_Click(object sender, RoutedEventArgs e)
        {
            ItemsToLog.Clear();
        }

        /// <summary>
        /// Exports the log entries to a semicolon-separated CSV file chosen by the user.
        /// </summary>
        private void bt_ExportLog_Click(object sender, RoutedEventArgs e)
        {
            StringBuilder SB = new StringBuilder();
            foreach (var I in ItemsToLog)
            {
                SB.AppendLine($"{I.Date};{I.Object};{I.Method};{I.File};{I.Result};");
            }

            System.Windows.Forms.SaveFileDialog SFD = new System.Windows.Forms.SaveFileDialog();
            SFD.Filter = "CSV File|*.csv";
            SFD.Title = "Save a CSV File";
            SFD.FileName = "LogExport.csv";
            SFD.ShowDialog();

            if (!String.IsNullOrEmpty(SFD.FileName))
            {
                try
                {
                    File.WriteAllText(SFD.FileName, SB.ToString());
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }

        /// <summary>
        /// Recovers a file whose remote operation failed by copying its local temporary copy
        /// (from the clicked log entry) to a location chosen by the user.
        /// </summary>
        private void bt_FileRecover_Click(object sender, RoutedEventArgs e)
        {
            var SenderButton = (Button)sender;
            var FileInfo = (LogListItem)SenderButton.Tag;

            System.Windows.Forms.SaveFileDialog saveFileDialog = new System.Windows.Forms.SaveFileDialog();
            saveFileDialog.FileName = Path.GetFileName(FileInfo.File);
            if (saveFileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                try
                {
                    File.Copy(FileInfo.LocalTemporaryPath, saveFileDialog.FileName);
                    MessageBox.Show("File has been saved");
                }
                catch
                {
                    MessageBox.Show("Failed to save file");
                }
            }
        }

        #endregion
    }
}