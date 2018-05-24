using KS2Drive.FS;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Linq;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Controls;
using KS2Drive.Config;
using KS2Drive.Log;
using MahApps.Metro.Controls;

namespace KS2Drive
{
    public partial class MainWindow : MetroWindow
    {
        private FSPService Service = new FSPService();
        private bool IsMounted = false;
        private Thread T;
        private Configuration AppConfiguration;
        public ObservableCollection<LogListItem> ItemsToLog = new ObservableCollection<LogListItem>();

        private System.Windows.Forms.NotifyIcon AppNotificationIcon;
        private ContextMenu AppMenu;

        public MainWindow()
        {
            InitializeComponent();

            AppConfiguration = ((App)Application.Current).AppConfiguration;

            AppMenu = (ContextMenu)this.FindResource("NotifierContextMenu");

            if (AppConfiguration.IsConfigured) ((MenuItem)AppMenu.Items[0]).IsEnabled = true;
            else ((MenuItem)AppMenu.Items[0]).IsEnabled = false;

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
            /*
            AppNotificationIcon.DoubleClick += delegate (object sender, EventArgs args)
                            {
                                this.Show();
                                this.WindowState = WindowState.Normal;
                            };
            */
            AppNotificationIcon.MouseClick += (s, e) => { this.Dispatcher.Invoke(() => { AppMenu.IsOpen = !AppMenu.IsOpen; }); };

            #endregion

            #region Try to start WinFSP Service

            try
            {
                T = new Thread(() => Service.Run());
                T.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start WinFSP Server. Please ensure you have installed version 2017.2. {ex.Message}");
                this.Close();
            }

            #endregion

            #region Service Events

            Service.RepositoryActionPerformed += (s1, e1) =>
            {
                Dispatcher.Invoke(() => ItemsToLog.Add(e1));
                if (!e1.Resultat.Equals("STATUS_SUCCESS"))
                {
                    Dispatcher.Invoke(() => AppNotificationIcon.ShowBalloonTip(3000, "KS² Drive", $"L'action {e1.Action} pour le fichier {e1.Fichier} a échoué", System.Windows.Forms.ToolTipIcon.Warning));
                };
            };

            #endregion

            LogList.ItemsSource = ItemsToLog;

            if (this.AppConfiguration.IsConfigured && AppConfiguration.AutoMount)
            {
                MountDrive();
            }
        }

        private void MountDrive()
        {
            try
            {
                Service.Mount(this.AppConfiguration.DriveLetter,
                    this.AppConfiguration.ServerURL,
                    this.AppConfiguration.ServerType.Value,
                    this.AppConfiguration.ServerLogin,
                    this.AppConfiguration.ServerPassword,
                    (FlushMode)Enum.ToObject(typeof(FlushMode), this.AppConfiguration.FlushMode.Value),
                    (KernelCacheMode)Enum.ToObject(typeof(KernelCacheMode), this.AppConfiguration.KernelCacheMode),
                    Convert.ToBoolean(this.AppConfiguration.SyncOps.Value),
                    Convert.ToBoolean(this.AppConfiguration.PreLoading.Value));
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

        private void UnmountDrive()
        {
            Service.Unmount();
            ((MenuItem)AppMenu.Items[0]).Header = "_MOUNT";
            IsMounted = false;
            ((MenuItem)AppMenu.Items[2]).IsEnabled = true;
            ((MenuItem)AppMenu.Items[2]).ToolTip = null;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == System.Windows.WindowState.Minimized) this.Hide();
            base.OnStateChanged(e);
        }

        private void QuitApp()
        {
            AppNotificationIcon.Visible = false;
            AppNotificationIcon.Dispose();
            Service.Stop();
            Application.Current.Shutdown();
        }


        #region Menu actions

        private void MenuMount_Click(object sender, RoutedEventArgs e)
        {
            if (IsMounted) UnmountDrive();
            else MountDrive();
        }

        private void MenuConfigure_Click(object sender, RoutedEventArgs e)
        {
            ConfigurationUI OptionWindow = new ConfigurationUI();
            OptionWindow.ShowDialog();
            if (AppConfiguration.IsConfigured) ((MenuItem)AppMenu.Items[0]).IsEnabled = true;
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
                SB.AppendLine($"{I.Date};{I.Object};{I.Action};{I.Fichier};{I.Resultat};");
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

        #endregion
    }
}