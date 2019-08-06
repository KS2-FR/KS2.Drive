using KS2Drive.FS;
using MahApps.Metro.Controls;
using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Deployment.Application;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KS2Drive.Config
{
    public partial class ConfigurationUI : MetroWindow
    {
        private String configurationFolderPath;
        private MainWindow main;

        public ConfigurationManager AppConfiguration { get; set; }
        public Configuration CurrentConfiguration { get; set; }

        private bool? CustomProxy
        {
            get
            {
                return rb_CustomProxy.IsChecked;
            }
        }
        public ConfigurationUI(MainWindow main)
        {
            this.main = main;
            this.DataContext = this;
            this.configurationFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KS2Drive");

            this.AppConfiguration = ((App)Application.Current).AppConfiguration;
            this.CurrentConfiguration = ((App)Application.Current).CurrentConfiguration;
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

            CBMountAsNetworkDrive.SelectedValuePath = "Key";
            CBMountAsNetworkDrive.DisplayMemberPath = "Value";
            CBMountAsNetworkDrive.Items.Add(new KeyValuePair<int, string>(0, "No"));
            CBMountAsNetworkDrive.Items.Add(new KeyValuePair<int, string>(1, "Yes"));
            CBMountAsNetworkDrive.SelectedIndex = 0;

            //Reload values from config
            this.AppConfiguration = ((App)Application.Current).AppConfiguration;

            EnterConfigurationData();
        }

        private void InitDriveNameButtons()
        {
            // Deze moet helaas volgens mij in elke methode apart omdat er anders bij het opstarten van het configuratiescherm een nullpointer komt.
            Button[] BTDriveNames = new Button[] { btDriveName1, btDriveName2, btDriveName3, btDriveName4 };

            for (int i = 0; i < 4; i++) {
                BTDriveNames[i].Click -= bt_AddConfiguration_Click;
                BTDriveNames[i].Click -= bt_SwitchCurrentConfiguration_Click;
            }

            foreach (Button b in BTDriveNames) b.Opacity = 0;

            int configurationCount = AppConfiguration.Configurations.Count;

            if (configurationCount > 4) MessageBox.Show("Cannot have more than four drives.");

            for (int i = 0; i < configurationCount; i++)
            {
                InitButton(BTDriveNames[i], i);
            }

            if (configurationCount < 4)
            {
                BTDriveNames[configurationCount].HorizontalContentAlignment = HorizontalAlignment.Center;
                BTDriveNames[configurationCount].Content = "+";
                BTDriveNames[configurationCount].Opacity = 0.5;
                BTDriveNames[configurationCount].Click += bt_AddConfiguration_Click;
                BTDriveNames[configurationCount].Background = new SolidColorBrush(Color.FromRgb(247, 247, 247));
            }
        }

        private void InitButton(Button b, int i)
        {
            b.Content = AppConfiguration.Configurations[i].Name;
            b.Opacity = 1;
            b.Click += bt_SwitchCurrentConfiguration_Click;
            b.Tag = AppConfiguration.Configurations[i];
            
            if (AppConfiguration.Configurations[i] == CurrentConfiguration)
            {
                b.Background = new SolidColorBrush(Color.FromRgb(96, 182, 229));
            } else
            {
                b.Background = new SolidColorBrush(Color.FromRgb(247, 247, 247));
            }
        }

        private void EnterConfigurationData()
        {
            InitDriveNameButtons();

            if (!String.IsNullOrEmpty(this.CurrentConfiguration.Name)) txtDriveName.Text = this.CurrentConfiguration.Name;

            if (!String.IsNullOrEmpty(this.CurrentConfiguration.DriveLetter)) CBFreeDrives.SelectedIndex = CBFreeDrives.Items.IndexOf(this.CurrentConfiguration.DriveLetter[0]) == -1 ? 0 : CBFreeDrives.Items.IndexOf(this.CurrentConfiguration.DriveLetter[0]);
            if (!String.IsNullOrEmpty(this.CurrentConfiguration.ServerURL)) txtURL.Text = this.CurrentConfiguration.ServerURL;

            var ServerTypeMatchingItem = CBMode.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(this.CurrentConfiguration.ServerType));
            if (!ServerTypeMatchingItem.Equals(default(KeyValuePair<int, string>))) CBMode.SelectedItem = ServerTypeMatchingItem;

            if (!String.IsNullOrEmpty(this.CurrentConfiguration.ServerLogin)) txtLogin.Text = this.CurrentConfiguration.ServerLogin;
            if (!String.IsNullOrEmpty(this.CurrentConfiguration.ServerPassword)) txtPassword.Password = this.CurrentConfiguration.ServerPassword;

            var KernelCacheMatchingItem = CBKernelCache.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(this.CurrentConfiguration.KernelCacheMode));
            if (!KernelCacheMatchingItem.Equals(default(KeyValuePair<int, string>))) CBKernelCache.SelectedItem = KernelCacheMatchingItem;

            var FlushMatchingItem = CBFlush.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(this.CurrentConfiguration.FlushMode));
            if (!FlushMatchingItem.Equals(default(KeyValuePair<int, string>))) CBFlush.SelectedItem = FlushMatchingItem;

            var SyncOpsMatchingItem = CBSyncOps.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(Convert.ToInt32(this.CurrentConfiguration.SyncOps)));
            if (!SyncOpsMatchingItem.Equals(default(KeyValuePair<int, string>))) CBSyncOps.SelectedItem = SyncOpsMatchingItem;

            var PreloadingMatchingItem = CBPreloading.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(Convert.ToInt32(this.CurrentConfiguration.PreLoading)));
            if (!PreloadingMatchingItem.Equals(default(KeyValuePair<int, string>))) CBPreloading.SelectedItem = PreloadingMatchingItem;

            var MountAsNetworkDriveMatchingItem = CBMountAsNetworkDrive.Items.Cast<KeyValuePair<int, string>>().FirstOrDefault(x => x.Key.Equals(Convert.ToInt32(this.CurrentConfiguration.MountAsNetworkDrive)));
            if (!MountAsNetworkDriveMatchingItem.Equals(default(KeyValuePair<int, string>))) CBMountAsNetworkDrive.SelectedItem = MountAsNetworkDriveMatchingItem;
            
            chk_AutoMount.IsChecked = CurrentConfiguration.AutoMount;
            chk_AutoStart.IsChecked = CurrentConfiguration.AutoStart;

            if (this.CurrentConfiguration.HTTPProxyMode == 0) rb_NoProxy.IsChecked = true;
            if (this.CurrentConfiguration.HTTPProxyMode == 1) rb_DefaultProxy.IsChecked = true;
            if (this.CurrentConfiguration.HTTPProxyMode == 2) rb_CustomProxy.IsChecked = true;

            ProxyRequiresAuthentication.IsChecked = this.CurrentConfiguration.UseProxyAuthentication;
            ProxyURL.Text = this.CurrentConfiguration.ProxyURL;
            ProxyLogin.Text = this.CurrentConfiguration.ProxyLogin;
            ProxyPassword.Password = this.CurrentConfiguration.ProxyPassword;

            //Look for certificate
            if (this.CurrentConfiguration.UseClientCertForAuthentication) Chk_UserClientCert.IsChecked = false;
            if (!String.IsNullOrEmpty(this.CurrentConfiguration.CertSerial) && !String.IsNullOrEmpty(this.CurrentConfiguration.CertStoreLocation) && !String.IsNullOrEmpty(this.CurrentConfiguration.CertStoreName))
            {
                var FoundCertificate = Tools.FindCertificate(this.CurrentConfiguration.CertStoreName, this.CurrentConfiguration.CertStoreLocation, this.CurrentConfiguration.CertSerial);
                if (FoundCertificate != null)
                {
                    txt_ClientCertSubject.Text = FoundCertificate.Subject;
                    if (this.CurrentConfiguration.UseClientCertForAuthentication) Chk_UserClientCert.IsChecked = true;
                }
            }

            // Prevent unsaved changes message when starting up the configuration screen
            tb_Status.Text = "";
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

            //From : https://stackoverflow.com/questions/5089601/how-to-run-a-c-sharp-application-at-windows-startup
            RegistryKey rkApp = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            if (chk_AutoStart.IsChecked == true)
            {
                if (ApplicationDeployment.IsNetworkDeployed) //If running from Click-Once link, autostart the Click-Once bootstrap
                {
                    rkApp.SetValue("KS2Drive", Environment.GetFolderPath(Environment.SpecialFolder.Programs) + @"\KS2\KS2.WorkflowClient.appref-ms");
                }
                else //If portable, autostart the program itself
                {
                    rkApp.SetValue("KS2Drive", System.Reflection.Assembly.GetEntryAssembly().Location);
                }
            }
            else
            {
                rkApp.DeleteValue("KS2Drive", false);
            }
            rkApp.Close();

            // Update the button text
            GetDriveNameButtonByDriveName(this.CurrentConfiguration.Name).Content = txtDriveName.Text;

            this.CurrentConfiguration.Name = txtDriveName.Text;

            if (CurrentConfiguration.Path != Path.Combine(configurationFolderPath, this.CurrentConfiguration.Name.ToLower() + "-config.json"))
            {
                File.Delete(CurrentConfiguration.Path);
            }

            this.CurrentConfiguration.Path = Path.Combine(configurationFolderPath, this.CurrentConfiguration.Name.ToLower() + "-config.json");
            this.CurrentConfiguration.DriveLetter = CBFreeDrives.SelectedValue.ToString();
            this.CurrentConfiguration.ServerURL = txtURL.Text;
            this.CurrentConfiguration.ServerType = (Int32)CBMode.SelectedValue;

            this.CurrentConfiguration.AutoMount = chk_AutoMount.IsChecked.Value;
            this.CurrentConfiguration.AutoStart = chk_AutoStart.IsChecked.Value;

            this.CurrentConfiguration.ServerLogin = txtLogin.Text;
            this.CurrentConfiguration.ServerPassword = txtPassword.Password;
            this.CurrentConfiguration.KernelCacheMode = Convert.ToInt32(CBKernelCache.SelectedValue);
            this.CurrentConfiguration.SyncOps = Convert.ToBoolean(Convert.ToInt16(CBSyncOps.SelectedValue));
            this.CurrentConfiguration.PreLoading = Convert.ToBoolean(Convert.ToInt16(CBPreloading.SelectedValue));
            this.CurrentConfiguration.FlushMode = Convert.ToInt32(CBFlush.SelectedValue);
            this.CurrentConfiguration.MountAsNetworkDrive = Convert.ToBoolean(CBMountAsNetworkDrive.SelectedValue);
            this.CurrentConfiguration.UseClientCertForAuthentication = Chk_UserClientCert.IsChecked.Value;

            if (rb_NoProxy.IsChecked.Value) this.CurrentConfiguration.HTTPProxyMode = 0;
            if (rb_DefaultProxy.IsChecked.Value) this.CurrentConfiguration.HTTPProxyMode = 1;
            if (rb_CustomProxy.IsChecked.Value) this.CurrentConfiguration.HTTPProxyMode = 2;

            this.CurrentConfiguration.UseProxyAuthentication = ProxyRequiresAuthentication.IsChecked.Value;
            this.CurrentConfiguration.ProxyURL = ProxyURL.Text;
            this.CurrentConfiguration.ProxyLogin = ProxyLogin.Text;
            this.CurrentConfiguration.ProxyPassword = ProxyPassword.Password;

            try
            {
                AppConfiguration.Save();
                tb_Status.Text = "Configuration saved";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot save configuration : {ex.Message}");
            }

            this.CurrentConfiguration.IsConfigured = true;

            Tools.LoadProxy(this.CurrentConfiguration);
        }

        private void UnsavedChangesMessage(object sender, TextChangedEventArgs args) => UnsavedChangesMessage();
        private void UnsavedChangesMessage(object sender, SelectionChangedEventArgs args) => UnsavedChangesMessage();
        private void UnsavedChangesMessage(object sender, RoutedEventArgs args) => UnsavedChangesMessage();

        private void UnsavedChangesMessage() => tb_Status.Text = "There are unsaved changes";

        private Button GetDriveNameButtonByDriveName(String name)
        {
            Button[] BTDriveNames = new Button[] { btDriveName1, btDriveName2, btDriveName3, btDriveName4 };

            foreach (Button b in BTDriveNames) if ((string)b.Content == name) return b;

            return null;
        }

        private void bt_UserClientCertSelect_Click(object sender, RoutedEventArgs e)
        {
            var SeachScope = ((Button)sender).Content.ToString();

            String StoreLocationAsString = SeachScope.Substring(0, SeachScope.IndexOf("."));
            String StoreNameAsString = SeachScope.Substring(SeachScope.IndexOf(".") + 1);

            StoreName StoreNameParsed;
            StoreLocation StoreLocationParsed;

            if (Enum.TryParse(StoreNameAsString, out StoreNameParsed) && Enum.TryParse(StoreLocationAsString, out StoreLocationParsed))
            {
                X509Store store = new X509Store(StoreNameParsed, StoreLocationParsed);
                store.Open(OpenFlags.ReadOnly);
                X509Certificate2Collection sel = X509Certificate2UI.SelectFromCollection(store.Certificates, null, null, X509SelectionFlag.SingleSelection);
                store.Close();

                if (sel.Count > 0)
                {
                    this.CurrentConfiguration.CertStoreName = StoreNameAsString;
                    this.CurrentConfiguration.CertStoreLocation = StoreLocationAsString;
                    this.CurrentConfiguration.CertSerial = sel[0].SerialNumber;
                    txt_ClientCertSubject.Text = sel[0].Subject;
                }
            }
        }
        
        private void bt_SwitchCurrentConfiguration_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).CurrentConfiguration = (Configuration)((Button)sender).Tag;
            this.CurrentConfiguration = ((App)Application.Current).CurrentConfiguration;
            EnterConfigurationData();
        }

        private void bt_AddConfiguration_Click(object sender, RoutedEventArgs e)
        {
            Button button = (Button)sender;
            button.Content = "Drive " + (AppConfiguration.Configurations.Count + 1);
            button.Opacity = 1;
            button.Click -= bt_AddConfiguration_Click;
            button.Click += bt_SwitchCurrentConfiguration_Click;

            DriveNameBorder.Opacity = 1;

            Configuration config = new Configuration();
            config.Name = "Drive " + (AppConfiguration.Configurations.Count + 1);
            config.Path = Path.Combine(configurationFolderPath, config.Name.ToLower() + "-config.json");

            this.AppConfiguration.Configurations.Add(config);
            ((App)Application.Current).AppConfiguration = this.AppConfiguration;

            InitButton(button, AppConfiguration.Configurations.Count - 1);

            Button[] BTDriveNames = new Button[] { btDriveName1, btDriveName2, btDriveName3, btDriveName4 };
            int configurationCount = AppConfiguration.Configurations.Count;

            if (configurationCount < 4)
            {
                BTDriveNames[configurationCount].HorizontalContentAlignment = HorizontalAlignment.Center;
                BTDriveNames[configurationCount].Content = "+";
                BTDriveNames[configurationCount].Opacity = 0.5;
                BTDriveNames[configurationCount].Click += bt_AddConfiguration_Click;
            }
        }

        private void bt_mountConfiguration_Click(object sender, RoutedEventArgs e)
        {
            main.MountDrives(false);
            this.Close();
        }

        private void bt_removeConfiguration_Click(object sender, RoutedEventArgs e)
        {
            AppConfiguration.Configurations.Remove(CurrentConfiguration);
            AppConfiguration.Save();
            File.Delete(CurrentConfiguration.Path);
            CurrentConfiguration = AppConfiguration.Configurations[0];

            ((App)Application.Current).AppConfiguration = this.AppConfiguration;
            ((App)Application.Current).CurrentConfiguration = this.CurrentConfiguration;

            EnterConfigurationData();
            InitDriveNameButtons();
        }
    }
}