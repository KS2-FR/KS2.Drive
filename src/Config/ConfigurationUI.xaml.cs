using KS2Drive.Config;
using KS2Drive.FS;
using MahApps.Metro.Controls;
using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace KS2Drive.Config
{
    public partial class ConfigurationUI : MetroWindow
    {
        public Configuration AppConfiguration { get; set; }

        public ConfigurationUI()
        {
            this.DataContext = this;
            this.AppConfiguration = ((App)Application.Current).AppConfiguration;
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
            CBKernelCache.SelectedIndex = 2;

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
            CBPreloading.SelectedIndex = 1;

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
        }

        private void bt_Save_Click(object sender, RoutedEventArgs e)
        {
            if (String.IsNullOrEmpty(txtURL.Text))
            {
                MessageBox.Show("URL is mandatory");
                return;
            }

            try
            {
                var u = new Uri(txtURL.Text);
            }
            catch
            {
                MessageBox.Show("The selected URL is not valid");
                return;
            }

            if (String.IsNullOrEmpty(txtLogin.Text))
            {
                MessageBox.Show("Server login is mandatory");
                return;
            }

            if (String.IsNullOrEmpty(txtPassword.Password))
            {
                MessageBox.Show("Server password is mandatory");
                return;
            }

            //TODO : Add auto start : https://stackoverflow.com/questions/5089601/how-to-run-a-c-sharp-application-at-windows-startup?utm_medium=organic&utm_source=google_rich_qa&utm_campaign=google_rich_qa
            RegistryKey rkApp = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            if (chk_AutoStart.IsChecked == true)
            {
                rkApp.SetValue("KS2Drive", System.Reflection.Assembly.GetEntryAssembly().Location);
            }
            else
            {
                rkApp.DeleteValue("KS2Drive", false);
            }

            this.AppConfiguration.DriveLetter = CBFreeDrives.SelectedValue.ToString();
            this.AppConfiguration.ServerURL = txtURL.Text;
            this.AppConfiguration.ServerType = (Int32)CBMode.SelectedValue;
            this.AppConfiguration.ServerLogin = txtLogin.Text;
            this.AppConfiguration.ServerPassword = txtPassword.Password;
            this.AppConfiguration.KernelCacheMode = Convert.ToInt32(CBKernelCache.SelectedValue);
            this.AppConfiguration.SyncOps = Convert.ToBoolean(Convert.ToInt16(CBSyncOps.SelectedValue));
            this.AppConfiguration.PreLoading = Convert.ToBoolean(Convert.ToInt16(CBPreloading.SelectedValue));
            this.AppConfiguration.FlushMode = Convert.ToInt32(CBFlush.SelectedValue);

            try
            {
                this.AppConfiguration.Save();
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot save configuration : {ex.Message}");
            }

            this.AppConfiguration.IsConfigured = true;

            Tools.LoadProxy(this.AppConfiguration);
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            this.AppConfiguration.ProxyPassword = ProxyPassword.Password;
        }
    }
}
