using KS2Drive.Log;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace KS2Drive.Config
{
    public class ConfigurationManager
    {

        public List<Configuration> Configurations { get; set; }
        public String Path { get; set; }

        public ConfigurationManager(String path)
        {
            this.Path = path;
            this.Configurations = new List<Configuration>();
        }

        public void AddConfiguration(Configuration config)
        {
            this.Configurations.Add(config);
        }

        /// <summary>
        /// Save the configuration.
        /// This saves a list of the paths of the individual configurations from ConfigurationManager.Configurations,
        /// and saves each configuration individually.
        /// </summary>
        public void Save()
        {
            StreamWriter file = new StreamWriter(Path);

            foreach (Configuration config in Configurations)
            {
                config.Save();
                file.WriteLine(config.Path);
            }

            file.Flush();
            file.Close();
        }

        /// <summary>
        /// The configuration manager is configured iff each configuration is configured.
        /// </summary>
        /// <returns></returns>
        public bool IsConfigured()
        {
            foreach(Configuration config in Configurations) if (!config.IsConfigured) return false;

            return true;
        }

        /// <summary>
        /// Read the configuration lsit from the file,
        /// load each configuration and put in in Configurations.
        /// </summary>
        /// <param name="path">The path of the list of configurations.</param>
        /// <returns>The ConfigurationManager</returns>
        public static ConfigurationManager Load(String path)
        {
            ConfigurationManager manager = new ConfigurationManager(path);

            StreamReader reader = new StreamReader(path);

            String line;

            while ((line = reader.ReadLine()) != null)
            {
                if (line == "") break;
                manager.AddConfiguration(Configuration.Load(line));
            }

            reader.Close();
            return manager;
        }
    }
}

