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
    /// <summary>
    /// WPF application entry point. Enforces a single running instance and loads the
    /// user configuration (and proxy settings) from %LocalAppData%\KS2Drive\config.json.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Application-wide configuration shared by the main window and the configuration dialog.
        /// </summary>
        public Configuration AppConfiguration { get; set; }

        /// <summary>
        /// Folder holding the configuration file (%LocalAppData%\KS2Drive).
        /// </summary>
        public String ConfigurationFolderPath { get; set; }

        /// <summary>
        /// Full path of the JSON configuration file.
        /// </summary>
        public String ConfigurationFilePath { get; set; }

        /// <summary>
        /// Named mutex used to prevent more than one instance of the application from running.
        /// </summary>
        private Mutex UnicityMutex = null;

        /// <summary>
        /// Initializes the application: acquires the single-instance mutex (exiting the process if another
        /// instance owns it), ensures the configuration folder exists (exiting the process if it cannot be created),
        /// loads the configuration and applies the configured proxy.
        /// </summary>
        public App()
        {
            bool MutexAcquisitionSuccess = false;
            UnicityMutex = new Mutex(true, "KS2.Drive", out MutexAcquisitionSuccess);
            if (!MutexAcquisitionSuccess)
            {
                MessageBox.Show("Another instance of this program is already running.", "KS² Drive");
                //Exit right away: Shutdown() would let the startup continue (configuration loading, main window, WinFSP service...)
                UnicityMutex.Dispose();
                Environment.Exit(0);
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
                    //Without a configuration the main window cannot start: exit instead of crashing on a null configuration
                    MessageBox.Show($"Cannot create the configuration folder {ConfigurationFolderPath} : {ex.Message}\n\nKS² Drive will now close.", "KS² Drive", MessageBoxButton.OK, MessageBoxImage.Error);
                    UnicityMutex.Dispose();
                    Environment.Exit(1);
                }
            }

            ConfigurationFilePath = Path.Combine(ConfigurationFolderPath, "config.json");
            this.AppConfiguration = Configuration.Load(ConfigurationFilePath);

            Tools.LoadProxy(this.AppConfiguration);

            #endregion

            //LayoutRenderer.Register<KS2Drive.Log.IndentationLayoutRenderer>("IndentationLayout");
        }

        /// <summary>
        /// Releases the single-instance mutex when the application exits.
        /// </summary>
        private void Application_Exit(object sender, ExitEventArgs e)
        {
            UnicityMutex?.Dispose();
        }
    }
}
