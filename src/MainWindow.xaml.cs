using KS2Drive.FS;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;

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
            CBSyncOps.Items.Add(new KeyValuePair<int, string>(0, "Yes"));
            CBSyncOps.SelectedIndex = 0;

            T = new Thread(() => service.Run());
            T.Start();
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
                    service.Mount(CBFreeDrives.SelectedValue.ToString(), txtURL.Text, (Int32)CBMode.SelectedValue, txtLogin.Text, txtPassword.Password, (KernelCacheMode)Enum.ToObject(typeof(KernelCacheMode), Convert.ToInt32(CBKernelCache.SelectedValue)), Convert.ToBoolean(Convert.ToInt16(CBSyncOps.SelectedValue)));
                }
                catch ( Exception ex)
                {
                    MessageBox.Show(ex.Message);
                    return;
                }

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
    }
}