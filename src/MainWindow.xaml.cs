using KS2Drive.FS;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Linq;

namespace KS2Drive
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private FSPService service = new FSPService();
        private bool IsMounted = false;
        private Thread T;
        private Configuration AppConfiguration;

        public MainWindow()
        {
            InitializeComponent();

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
            CBMode.Items.Add(new KeyValuePair<int, string>(1, "AOS"));
            CBMode.SelectedIndex = 0;

            CBKernelCache.SelectedValuePath = "Key";
            CBKernelCache.DisplayMemberPath = "Value";
            CBKernelCache.Items.Add(new KeyValuePair<int, string>((Int32)KernelCacheMode.Disabled, KernelCacheMode.Disabled.ToString()));
            CBKernelCache.Items.Add(new KeyValuePair<int, string>((Int32)KernelCacheMode.MetaDataOnly, KernelCacheMode.MetaDataOnly.ToString()));
            CBKernelCache.Items.Add(new KeyValuePair<int, string>((Int32)KernelCacheMode.DataAndMetaData, KernelCacheMode.DataAndMetaData.ToString()));
            CBKernelCache.SelectedIndex = 0;

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

            T = new Thread(() => service.Run());
            try
            {
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
                    service.Mount(CBFreeDrives.SelectedValue.ToString(), txtURL.Text, (Int32)CBMode.SelectedValue, txtLogin.Text, txtPassword.Password, (KernelCacheMode)Enum.ToObject(typeof(KernelCacheMode), Convert.ToInt32(CBKernelCache.SelectedValue)), Convert.ToBoolean(Convert.ToInt16(CBSyncOps.SelectedValue)), Convert.ToBoolean(Convert.ToInt16(CBPreloading.SelectedValue)));
                }
                catch ( Exception ex)
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
                this.AppConfiguration.Save();

                button1.Content = "Unmount";
                IsMounted = true;
                txtURL.IsEnabled = false;
                txtLogin.IsEnabled = false;
                txtPassword.IsEnabled = false;

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
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            service.Stop();
        }

        private void MenuOptions_Click(object sender, RoutedEventArgs e)
        {
            Options OptionWindow = new Options();
            OptionWindow.ShowDialog();
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void MenuDebug_Click(object sender, RoutedEventArgs e)
        {
            service.ShowDebug();
        }
    }
}