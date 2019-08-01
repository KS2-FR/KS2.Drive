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
        //private FSPService Service1;
        private MountingHelper[] mountingHelpers;

        private bool IsMounted = false;
        //private Thread T;
        //private ConfigurationManager AppConfigManager;
        //private Configuration AppConfiguration;
        public ObservableCollection<LogListItem> ItemsToLog = new ObservableCollection<LogListItem>();

        private System.Windows.Forms.NotifyIcon AppNotificationIcon;
        private ContextMenu AppMenu;

        public MainWindow()
        {
            InitializeComponent();
            mountingHelpers = new MountingHelper[2];
            mountingHelpers[0] = new MountingHelper();
            mountingHelpers[1] = new MountingHelper();

            //AppConfigManager = new ConfigurationManager();

            //AppConfigManager.AddConfiguration(((App)Application.Current).AppConfiguration);

            //Configuration config2 = ((App)Application.Current).AppConfiguration;

            //config2.DriveLetter = "B";
            //AppConfigManager.AddConfiguration(config2);
            //AppConfiguration = ((App)Application.Current).AppConfiguration;
            mountingHelpers[0].config = ((App)Application.Current).AppConfiguration;

            Configuration config2 = ((App)Application.Current).AppConfiguration;

            config2.DriveLetter = "B";

            mountingHelpers[1].config = config2;

            AppMenu = (ContextMenu)this.FindResource("NotifierContextMenu");
            ((MenuItem)AppMenu.Items[0]).IsEnabled = mountingHelpers[0].config.IsConfigured;

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

            for (int i = 0; i < mountingHelpers.Length; i++)
            {
                try
                {
                    mountingHelpers[i].service = new FSPService();

                    #region Service Events

                    mountingHelpers[i].service.RepositoryActionPerformed += (s1, e1) =>
                    {
                        Dispatcher.Invoke(() => ItemsToLog.Add(e1));
                        if (!e1.Result.Equals("STATUS_SUCCESS"))
                        {
                            if (!e1.AllowRetryOrRecover) Dispatcher.Invoke(() => AppNotificationIcon.ShowBalloonTip(3000, "KS² Drive", $"The action {e1.Method} for the file {e1.File} failed", System.Windows.Forms.ToolTipIcon.Warning));
                            else Dispatcher.Invoke(() => AppNotificationIcon.ShowBalloonTip(3000, "KS² Drive", $"The action {e1.Method} for the file {e1.File} failed. You can recover this file via the LOG menu", System.Windows.Forms.ToolTipIcon.Warning));
                        };
                    };

                    mountingHelpers[i].service.RepositoryAuthenticationFailed += (s2, e2) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            AppNotificationIcon.ShowBalloonTip(3000, "KS² Drive", $"Your credentials are invalid. Please update them in the Configuration panel", System.Windows.Forms.ToolTipIcon.Error);
                        });
                    };

                    #endregion
                    mountingHelpers[i].Start();
                }
                catch
                {
                    var MB = new WinFSPUI();
                    MB.ShowDialog();
                    QuitApp();
                    return;
                }
            }


            #endregion

            LogList.ItemsSource = ItemsToLog;

            Dispatcher.Invoke(() => AppNotificationIcon.ShowBalloonTip(3000, "KS² Drive", $"KS² Drive has started", System.Windows.Forms.ToolTipIcon.Info));

            if (this.mountingHelpers[0].config.IsConfigured)
            {
                for(int i = 0; i < mountingHelpers.Length; i++) if (mountingHelpers[i].config.AutoMount) MountDrive(i);
            }
            else
            {
                MenuConfigure_Click(this, null);
            }
        }

        private void MountDrives()
        {
            for (int i = 0; i < mountingHelpers.Length; i++) MountDrive(i);
        }

        private void MountDrive(int drive)
        {
            try
            {
                mountingHelpers[drive].Mount();
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
            //foreach (Configuration config in AppConfigManager.GetConfigurations())
            //{
            Process.Start($@"{mountingHelpers[0].config.DriveLetter}:\");
            //}

        }

        private void UnmountDrives()
        {
            for (int i = 0; i < mountingHelpers.Length; i++) UnmountDrive(i);
        }

        private void UnmountDrive(int drive)
        {
            try
            {
                mountingHelpers[drive].Unmount();
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
            mountingHelpers[0].service?.Stop();
            Application.Current.Shutdown();
        }

        #region Menu actions

        private void MenuMount_Click(object sender, RoutedEventArgs e)
        {
            if (IsMounted) UnmountDrives();
            //else MountDrive();
            else MountDrives();
        }

        private void MenuConfigure_Click(object sender, RoutedEventArgs e)
        {
            ConfigurationUI OptionWindow = new ConfigurationUI();
            OptionWindow.ShowDialog();
            if (mountingHelpers[0].config.IsConfigured) ((MenuItem)AppMenu.Items[0]).IsEnabled = true;
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