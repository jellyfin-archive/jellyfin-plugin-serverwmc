using System;
using System.Collections.Generic;       // for new way to doing webpages
using Jellyfin.Plugin.ServerWMC.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ServerWMC
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public override string Name => "ServerWMC";
        public override Guid Id => Guid.Parse("1fc322a1-af2e-49a5-b2eb-a89b4240f700");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin Instance { get; private set; }

        /// <summary>
        /// Gets the plugin description.
        /// </summary>
        /// <value>The description.</value>
        public override string Description
        {
            get
            {
                return "Provides pvr functions using using ServerWMC to control a WMC backend.";
            }
        }

        public override void UpdateConfiguration(MediaBrowser.Model.Plugins.BasePluginConfiguration configuration)
        {
            PluginConfiguration pc = configuration as PluginConfiguration;

            SocketClientAsync.ChangeAddress(pc.ServerIP, pc.ServerPort);            // throws exception if can't connect with new data thus skipping save
            bool changedIp = (pc.ServerIP != Configuration.ServerIP);               // true if ip was changed
            bool changedPort = (pc.ServerPort != Configuration.ServerPort);         // true if ip was changed

            if (changedIp || changedPort)
                WMCService.AddonGoingDown();                                        // tell current serverwmc at old IP, this jellyfin server is going down

            base.UpdateConfiguration(configuration);                                // update to new config data

            // if Ip chaged, tell core to change datasource
            if (changedIp || changedPort)                                           //  tell jellyfin server to load new data source
            {
                WMCService.DataSourceChange();
            }
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = this.Name,
                    EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.html", GetType().Namespace)
                }
            };
        }
    }
}
