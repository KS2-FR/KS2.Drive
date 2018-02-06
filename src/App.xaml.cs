using KS2Drive.Log;
using Newtonsoft.Json;
using NLog.LayoutRenderers;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;

namespace KS2Drive
{
    public partial class App : Application
    {
        public Configuration AppConfiguration { get; set; }
        public String ConfigurationFolderPath { get; set; }
        public String ConfigurationFilePath { get; set; }
        public App()
        {
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

            ConfigurationFilePath = Path.Combine(ConfigurationFolderPath, "config.json");
            this.AppConfiguration = Configuration.Load(ConfigurationFilePath);

            Tools.LoadProxy(this.AppConfiguration);

            LayoutRenderer.Register<KS2Drive.Log.IndentationLayoutRenderer>("IndentationLayout");
        }
    }
}
