using KS2Drive.Config;
using KS2Drive.FS;
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
    public partial class MainWindow : MetroWindow
    {
        private FSPService Service;
        private Configuration CurrentConfiguration;

        private bool IsMounted = false;
        private Thread T;

        private ConfigurationManager AppConfiguration;
        public ObservableCollection<LogListItem> ItemsToLog = new ObservableCollection<LogListItem>();

        private System.Windows.Forms.NotifyIcon AppNotificationIcon;
        private ContextMenu AppMenu;

        public MainWindow()
        {
            InitializeComponent();

            AppConfiguration = ((App)Application.Current).AppConfiguration;
            CurrentConfiguration = ((App)Application.Current).CurrentConfiguration;

            AppMenu = (ContextMenu)this.FindResource("NotifierContextMenu");

            ((MenuItem)AppMenu.Items[0]).IsEnabled = CurrentConfiguration.IsConfigured;

            this.Hide();

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
                var MB = new WinFSPUI();
                MB.ShowDialog();
                QuitApp();
                return;
            }


            #endregion

            LogList.ItemsSource = ItemsToLog;

            Dispatcher.Invoke(() => AppNotificationIcon.ShowBalloonTip(3000, "KS² Drive", $"KS² Drive has started", System.Windows.Forms.ToolTipIcon.Info));
            
            if (this.AppConfiguration.IsConfigured())
            {
                // Currentconfiguration is altijd 0, dit aanpassen als ik daar mee verder ga
                MountDrives(true);
            }
            else
            {
                MenuConfigure_Click(this, null);
            }
        }
        
        private void MountDrives(bool isAutoMount)
        {
            foreach (Configuration config in AppConfiguration.Configurations) if (config.AutoMount || !isAutoMount) MountDrive(config);
        }

        private void MountDrive(Configuration config)
        {
            try
            {
                Service.Mount(config);
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
            
            Process.Start($@"{config.DriveLetter}:\");

        }

        private void UnmountDrives()
        {
            for (int i = 0; i < AppConfiguration.Configurations.Count; i++) UnmountDrive(i);
        }

        private void UnmountDrive(int drive)
        {
            try
            {
                Service.Unmount(AppConfiguration.Configurations[drive]);
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
        /// Hide window when minimized
        /// </summary>
        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == System.Windows.WindowState.Minimized) this.Hide();
            base.OnStateChanged(e);
        }

        /// <summary>
        /// Cleanup and shutdown the app
        /// </summary>
        private void QuitApp()
        {
            AppNotificationIcon.Visible = false;
            AppNotificationIcon.Dispose();
            Service?.Stop();
            Application.Current.Shutdown();
        }

        #region Menu actions

        private void MenuMount_Click(object sender, RoutedEventArgs e)
        {
            // Dit geeft nu nog een error omdat niet alle drives gemount worden
            if (IsMounted) UnmountDrives();
            //else MountDrive();
            else MountDrives(false);
        }

        private void MenuConfigure_Click(object sender, RoutedEventArgs e)
        {
            ConfigurationUI OptionWindow = new ConfigurationUI();
            OptionWindow.ShowDialog();
            if (CurrentConfiguration.IsConfigured) ((MenuItem)AppMenu.Items[0]).IsEnabled = true;
        }

        private void MenuLog_Click(object sender, RoutedEventArgs e)
        {
            if (!this.IsVisible) this.Show();
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            QuitApp();
        }

        private void MenuAbout_Click(object sender, RoutedEventArgs e)
        {
            About.About A = new About.About();
            A.ShowDialog();
        }

        #endregion

        #region Log actions

        private void bt_ClearLog_Click(object sender, RoutedEventArgs e)
        {
            ItemsToLog.Clear();
        }

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