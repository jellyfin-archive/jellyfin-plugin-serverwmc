using MediaBrowser.Model.Plugins;

namespace wmc2mb.Configuration
{
    /// <summary>
    /// Class PluginConfiguration
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets ServerWMC IP Address
        /// </summary>
        /// <value>ip address string</value>
        public string ServerIP { get; set; }

        /// <summary>
        /// Gets or sets ServerWMC Port.
        /// </summary>
        public int ServerPort { get; set; }

        /// <summary>
        /// Gets or sets the Linux path to rec tv folder
        /// </summary>
        public string LinuxPath { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginConfiguration" /> class.
        /// </summary>
        public PluginConfiguration()
            : base()
        {
            ServerIP = "localhost";
            ServerPort = 9080;
            LinuxPath = "";
        }
    }
}