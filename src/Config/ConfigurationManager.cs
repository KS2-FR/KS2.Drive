using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

        public bool IsConfigured()
        {
            foreach(Configuration config in Configurations) if (!config.IsConfigured) return false;

            return true;
        }

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

