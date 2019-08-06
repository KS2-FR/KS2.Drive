//using KS2Drive.Log;
using KS2Drive.Config;
using Newtonsoft.Json;
using NLog.LayoutRenderers;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace KS2Drive
{
    public partial class App : Application
    {
        public ConfigurationManager AppConfiguration { get; set; }
        // Note: every configuration is mounted simultaneously, 
        // CurrentConfiguration is necessary for the configuration screen.
        public Configuration CurrentConfiguration { get; set; }
        public String ConfigurationFolderPath { get; set; }

        private Mutex UnicityMutex = null;

        public App()
        {
            bool MutexAcquisitionSuccess = false;
            UnicityMutex = new Mutex(true, "KS2.Drive", out MutexAcquisitionSuccess);
            if (!MutexAcquisitionSuccess)
            {
                MessageBox.Show("Another instance of this program is already runnning.", "KS² Drive");
                App.Current.Shutdown();
            }

            #region Loading configuration

            this.ConfigurationFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KS2Drive");
            if (!Directory.Exists(ConfigurationFolderPath))
            {
                try
                {
                    Directory.CreateDirectory(ConfigurationFolderPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                    return;
                }
            }
            
            AppConfiguration = ConfigurationManager.Load(Path.Combine(ConfigurationFolderPath, "config.json"));

            CurrentConfiguration = AppConfiguration.Configurations[0];
            
            //Tools.LoadProxy(CurrentConfiguration);
            #endregion

            //LayoutRenderer.Register<KS2Drive.Log.IndentationLayoutRenderer>("IndentationLayout");
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            UnicityMutex?.Dispose();
        }
    }
}
