using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;
using wmc2mb.Configuration;
using MediaBrowser.Model.Plugins;
using System.Collections.Generic;       // for new way to doing webpages
using MediaBrowser.Model.Logging;
using System.IO;
using MediaBrowser.Model.Drawing;

namespace wmc2mb
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        /// <summary>
        /// gets the name of the blugin
        /// </summary>
        public override string Name
        {
            get { return "ServerWMC"; }
        }

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

        private Guid _id = new Guid("817527c1-5f78-4763-acb1-59fa0d64854a");
        public override Guid Id
        {
            get { return _id; }
        }

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.jpg");
        }

        public ImageFormat ThumbImageFormat
        {
            get
            {
                return ImageFormat.Jpg;
            }
        }

        /// <summary>
        /// Gets the instance.
        /// </summary>
        /// <value>The instance.</value>
        public static Plugin Instance { get; private set; }

        public override void UpdateConfiguration(MediaBrowser.Model.Plugins.BasePluginConfiguration configuration)
        {
            PluginConfiguration pc = configuration as PluginConfiguration;

            SocketClientAsync.ChangeAddress(pc.ServerIP, pc.ServerPort);            // throws exception if can't connect with new data thus skipping save
            bool changedIp = (pc.ServerIP != Configuration.ServerIP);               // true if ip was changed
            bool changedPort = (pc.ServerPort != Configuration.ServerPort);         // true if ip was changed

            if (changedIp || changedPort)
                WMCService.AddonGoingDown();                                        // tell current serverwmc at old IP, this mbs is going down

            base.UpdateConfiguration(configuration);                                // update to new config data

            // if Ip chaged, tell core to change datasource
            if (changedIp || changedPort)                                           //  tell mbs to load new data source
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
                    Name = "ServerWMC",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
                }
            };
        }
    }
}
