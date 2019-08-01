using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KS2Drive.Config
{
    public class ConfigurationManager
    {
        private List<Configuration> configurations;

        public ConfigurationManager()
        {
            this.configurations = new List<Configuration>();
        }

        public void AddConfiguration(Configuration config)
        {
            this.configurations.Add(config);
        }

        public List<Configuration> GetConfigurations() => configurations;
    }
}

