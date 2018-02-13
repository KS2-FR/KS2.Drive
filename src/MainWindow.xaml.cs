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

namespace KS2Drive
{
    public partial class MainWindow : Window
    {
        private FSPService service = new FSPService();
        private bool IsMounted = false;
        private Thread T;
        private Configuration AppConfiguration;
        private System.Windows.Forms.NotifyIcon ni;
        public ObservableCollection<LogListItem> ItemsToLog = new ObservableCollection<LogListItem>();

        public MainWindow()
        {
            InitializeComponent();

            service.RepositoryActionPerformed += (s1, e1) =>
            {
                Dispatcher.Invoke(() => ItemsToLog.Add(e1));
                if (!e1.Resultat.Equals("STATUS_SUCCESS"))
                {
                    Dispatcher.Invoke(() => ni.ShowBalloonTip(3000, "KS² Drive", $"L'action {e1.Action} pour le fichier {e1.Fichier} a échoué", System.Windows.Forms.ToolTipIcon.Warning));

                };
            };

            #region Icon

            ni = new System.Windows.Forms.NotifyIcon();
            Stream iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/KS2Drive;component/Resources/Main.ico")).Stream;
            ni.Icon = new System.Drawing.Icon(iconStream);
            ni.Visible = true;
            ni.Text = "KS² Drive";
            ni.BalloonTipText = "KS² Drive";
            ni.DoubleClick += delegate (object sender, EventArgs args)
                            {
                                this.Show();
                                this.WindowState = WindowState.Normal;
                            };

            #endregion

            //Get Free drives
            ArrayList FreeDrives = new ArrayList(26); // Allocate space for alphabet
            for (int i = 65; i < 91; i++) // increment from ASCII values for A-Z
            {
                FreeDrives.Add(Convert.ToChar(i)); // Add uppercase letters to possible drive letters
            }

            foreach (string drive in Directory.GetLogicalDrives())
            {
                FreeDrives.Remove(drive[0]); // removed used drive letters from possible drive letters
            }

            foreach (char drive in FreeDrives)
            {
                CBFreeDrives.Items.Add(drive); // add unused drive letters to the combo box
            }
            CBFreeDrives.SelectedIndex = 0;

            CBMode.SelectedValuePath = "Key";
            CBMode.DisplayMemberPath = "Value";
            CBMode.Items.Add(new KeyValuePair<int, string>(0, "webDAV"));
            //CBMode.Items.Add(new KeyValuePair<int, string>(1, "AOS")); //TEMP
            CBMode.SelectedIndex = 0;

            CBKernelCache.SelectedValuePath = "Key";
            CBKernelCache.DisplayMemberPath = "Value";
            CBKernelCache.Items.Add(new KeyValuePair<int, string>((Int32)KernelCacheMode.Disabled, KernelCacheMode.Disabled.ToString()));
            CBKernelCache.Items.Add(new KeyValuePair<int, string>((Int32)KernelCacheMode.MetaDataOnly, KernelCacheMode.MetaDataOnly.ToString()));
            CBKernelCache.Items.Add(new KeyValuePair<int, string>((Int32)KernelCacheMode.DataAndMetaData, KernelCacheMode.DataAndMetaData.ToString()));
            CBKernelCache.SelectedIndex = 0;

            CBFlush.SelectedValuePath = "Key";
            CBFlush.DisplayMemberPath = "Value";
            CBFlush.Items.Add(new KeyValuePair<int, string>((Int32)FlushMode.FlushAtCleanup, FlushMode.FlushAtCleanup.ToString()));
            CBFlush.Items.Add(new KeyValuePair<int, string>((Int32)FlushMode.FlushAtWrite, FlushMode.FlushAtWrite.ToString()));
            CBFlush.SelectedIndex = 0;

            CBSyncOps.SelectedValuePath = "Key";
            CBSyncOps.DisplayMemberPath = "Value";
            CBSyncOps.Items.Add(new KeyValuePair<int, string>(0, "No"));
            CBSyncOps.Items.Add(new KeyValuePair<int, string>(1, "Yes"));
            CBSyncOps.SelectedIndex = 0;

            CBPreloading.SelectedValuePath = "Key";
            CBPreloading.DisplayMemberPath = "Value";
            CBPreloading.Items.Add(new KeyValuePair<int, string>(0, "No"));
            CBPreloading.Items.Add(new KeyValuePair<int, string>(1, "Yes"));
            CBPreloading.SelectedIndex = 0;

            try
            {
                T = new Thread(() => service.Run());
                T.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start WinFSP Server. Please ensure you have installed version 2017.2. {ex.Message}");
                this.Close();
            }

            //Reload values from config
            this.AppConfiguration = ((App)Application.Current).AppConfiguration;
            if (!String.IsNullOrEmpty(this.AppConfiguration.DriveLetter)) CBFreeDrives.SelectedIndex = CBFreeDrives.Items.IndexOf(this.AppConfiguration.DriveLetter[0]) == -1 ? 0 : CBFreeDrives.Items.IndexOf(this.AppConfiguration.DriveLetter[0]);
            if (!String.IsNullOrEmpty(this.AppConfiguration.ServerURL)) txtURL.Text = this.AppConfiguration.ServerURL;

            if (this.AppConfiguration.ServerType.HasValue)
            {
                var SelectedMode = CBMode.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(this.AppConfiguration.ServerType.Value));
                if (!SelectedMode.Equals(default(KeyValuePair<int, string>))) CBMode.SelectedItem = SelectedMode;
            }

            if (!String.IsNullOrEmpty(this.AppConfiguration.ServerLogin)) txtLogin.Text = this.AppConfiguration.ServerLogin;
            if (!String.IsNullOrEmpty(this.AppConfiguration.ServerPassword)) txtPassword.Password = this.AppConfiguration.ServerPassword;

            if (this.AppConfiguration.KernelCacheMode.HasValue)
            {
                var SelectedMode = CBKernelCache.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(this.AppConfiguration.KernelCacheMode.Value));
                if (!SelectedMode.Equals(default(KeyValuePair<int, string>))) CBKernelCache.SelectedItem = SelectedMode;
            }

            if (this.AppConfiguration.FlushMode.HasValue)
            {
                var SelectedMode = CBFlush.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(this.AppConfiguration.FlushMode.Value));
                if (!SelectedMode.Equals(default(KeyValuePair<int, string>))) CBFlush.SelectedItem = SelectedMode;
            }

            if (this.AppConfiguration.SyncOps.HasValue)
            {
                var SelectedMode = CBSyncOps.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(Convert.ToInt16(this.AppConfiguration.SyncOps.Value)));
                if (!SelectedMode.Equals(default(KeyValuePair<int, string>))) CBSyncOps.SelectedItem = SelectedMode;
            }

            if (this.AppConfiguration.PreLoading.HasValue)
            {
                var SelectedMode = CBPreloading.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(Convert.ToInt16(this.AppConfiguration.PreLoading.Value)));
                if (!SelectedMode.Equals(default(KeyValuePair<int, string>))) CBPreloading.SelectedItem = SelectedMode;
            }

            if (this.AppConfiguration.AutoMount) button1_Click(null, null);

            LogList.ItemsSource = ItemsToLog;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == System.Windows.WindowState.Minimized) this.Hide();
            base.OnStateChanged(e);
        }

        private void button1_Click(object sender, RoutedEventArgs e)
        {
            if (!IsMounted)
            {
                if (String.IsNullOrEmpty(txtURL.Text)) return;
                if (String.IsNullOrEmpty(txtLogin.Text)) return;
                if (String.IsNullOrEmpty(txtPassword.Password)) return;

                try
                {
                    var u = new Uri(txtURL.Text);
                }
                catch
                {
                    MessageBox.Show("l'URL est invalide");
                    return;
                }

                try
                {
                    service.Mount(CBFreeDrives.SelectedValue.ToString(), 
                        txtURL.Text, 
                        (Int32)CBMode.SelectedValue, 
                        txtLogin.Text, 
                        txtPassword.Password,
                        (FlushMode)Enum.ToObject(typeof(FlushMode), Convert.ToInt32(CBFlush.SelectedValue)),
                        (KernelCacheMode)Enum.ToObject(typeof(KernelCacheMode), Convert.ToInt32(CBKernelCache.SelectedValue)), 
                        Convert.ToBoolean(Convert.ToInt16(CBSyncOps.SelectedValue)), 
                        Convert.ToBoolean(Convert.ToInt16(CBPreloading.SelectedValue)));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                    return;
                }

                //Update saved configuration
                this.AppConfiguration.DriveLetter = CBFreeDrives.SelectedValue.ToString();
                this.AppConfiguration.ServerURL = txtURL.Text;
                this.AppConfiguration.ServerType = (Int32)CBMode.SelectedValue;
                this.AppConfiguration.ServerLogin = txtLogin.Text;
                this.AppConfiguration.ServerPassword = txtPassword.Password;
                this.AppConfiguration.KernelCacheMode = Convert.ToInt32(CBKernelCache.SelectedValue);
                this.AppConfiguration.SyncOps = Convert.ToBoolean(Convert.ToInt16(CBSyncOps.SelectedValue));
                this.AppConfiguration.PreLoading = Convert.ToBoolean(Convert.ToInt16(CBPreloading.SelectedValue));
                this.AppConfiguration.FlushMode = Convert.ToInt32(CBFlush.SelectedValue);
                this.AppConfiguration.Save();

                button1.Content = "Unmount";
                IsMounted = true;
                txtURL.IsEnabled = false;
                txtLogin.IsEnabled = false;
                txtPassword.IsEnabled = false;
                CBMode.IsEnabled = false;
                CBFreeDrives.IsEnabled = false;
                CBKernelCache.IsEnabled = false;
                CBFlush.IsEnabled = false;
                CBPreloading.IsEnabled = false;
                CBSyncOps.IsEnabled = false;

                ItemsToLog.Clear();

                Process.Start($@"{CBFreeDrives.SelectedValue.ToString()}:\");
            }
            else
            {
                service.Unmount();

                button1.Content = "Mount";
                IsMounted = false;
                txtURL.IsEnabled = true;
                txtLogin.IsEnabled = true;
                txtPassword.IsEnabled = true;
                CBMode.IsEnabled = true;
                CBFreeDrives.IsEnabled = true;
                CBKernelCache.IsEnabled = true;
                CBPreloading.IsEnabled = true;
                CBSyncOps.IsEnabled = true;
                CBFlush.IsEnabled = true;
            }
        }

        private void MenuOptions_Click(object sender, RoutedEventArgs e)
        {
            Options OptionWindow = new Options();
            OptionWindow.ShowDialog();
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            QuitApp();
        }

        private void MenuDebug_Click(object sender, RoutedEventArgs e)
        {
            service.ShowDebug();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            QuitApp();
        }

        private void QuitApp()
        {
            ni.Visible = false;
            ni.Dispose();
            service.Stop();
            Application.Current.Shutdown();
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            ItemsToLog.Clear();
        }
    }

    public class LogListItem
    {
        public String Date { get; set; }
        public String Action { get; set; }
        public String Fichier { get; set; }
        public String Resultat { get; set; }
    }
}