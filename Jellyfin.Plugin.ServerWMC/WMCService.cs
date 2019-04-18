using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using System.Threading;
using System.Reflection;
using System.Diagnostics;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Dto;
using System.Net;
using System.Xml.Linq;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.IO;

// to update package enter:
//      Install-Package MediaBrowser.Server.Core

namespace wmc2mb
{
    public class WMCService : ILiveTvService, IDisposable
    {
        // ALSO DON"T FORGET TO UP THE ASSEMBLY REV
        private int _requiredBuild = 1123;  // set to the serverwmc build is needed to work
        private int _newestBuild = 1190;    // this value no longer needs to be updated, newest version is now read from AutoUpdate.xml
                                            // see GetNewestSWMCBuild

        #region general
        private string _clientVersion;
        private string _clientOS = "windows " + Environment.OSVersion.Version;
        IFileSystem _fileSystem;
        Timer _recTimer = null;             // timer to periodically ping serverwmc to let it know its still up
        int _idStreamInt = 0;               // use to generate stream Id
        string _serverWMC_version = "unknown";
        private int _serverWMC_build = 0;
        private ILogger _logger;

        private readonly string HTTP = @"http://";

        //readonly string getStreamINFO = "IncludeStreamInfo";
        readonly string streamInfoHEADER = "StreamInfo:";

        /// <summary>
        /// Gets the instance.
        /// </summary>
        /// <value>The instance.</value>
        public static WMCService Instance { get; private set; }

        public bool _serverWMCisDown = false;       // keep track of whether serverwmc was up last time we checked (see timer)
        public string _recTVPath = null;            // store the path to the rec TV folder received from swmc

        private string _linuxPath;
        public DateTimeOffset LastRecordingChange = DateTimeOffset.MinValue;

        /// <summary>
        /// constructor
        /// </summary>
        public WMCService(IHttpClient httpClient, IFileSystem fileSystem, ILogManager logManager)
        {
            Instance = this;
            _logger = logManager.GetLogger(this.Name);      // start logger
            _clientVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();

            SocketClientAsync.InitAddress(Plugin.Instance.Configuration.ServerIP, Plugin.Instance.Configuration.ServerPort);    // set ip and port
            _logger.Info("Config IP: {0} ({1}), Config Port: {2}",
                                            Plugin.Instance.Configuration.ServerIP,
                                            SocketClientAsync.IpAddr,
                                            Plugin.Instance.Configuration.ServerPort);

            //Thread.Sleep(20000);

            _linuxPath = null;  // linux path obsoleted in .net core 

            //if (Environment.OSVersion.Platform == PlatformID.Unix)
            //{
            //    if (string.IsNullOrEmpty(_linuxPath))
            //        _logger.Error("Running in Linux, but linux path to RecTV directory is not set in plugin configuration page");
            //    else
            //        _logger.Info("Running in Linux, Linux path to mounted RecTV directory: {0}", _linuxPath);
            //}

            // see if a force transcode profile was set by sneaking it into the Linux path field
            //if (_linuxPath != null && _linuxPath.Contains('|'))
            //{
            //    if (_linuxPath.Split('|').Length > 1)
            //    {
            //        string forceProfile = _linuxPath.Split('|')[1];
            //        _linuxPath = _linuxPath.Split('|')[0];
            //        _logger.Debug("Forcing transcode profile: " + forceProfile);
            //        _logger.Debug("Recovered Linux path: " + _linuxPath);
            //    }
            //}

            // read newest serverwmc build from autoupdate site
            var newBuild = GetNewestSWMCBuild();
            if (newBuild == 0)
                _logger.Info("Using last hardcoded build value: " + _newestBuild);
            else
            {
                _newestBuild = newBuild;
                _logger.Info("Newest ServerWMC build value found: " + _newestBuild);
            }

            _fileSystem = fileSystem;                       // for file access, not used currently

            // timer to signal server that mbs is alive, this is needed because serverwmc needs to get be polled by the client
            // occasionally to know that its still alive
            _recTimer = new Timer(state =>
                    {
                        string[] responses = null;
                        try
                        {
                            string request = string.Format("GetServiceStatus|{0}|{1}", _clientVersion, _clientOS);
                            responses = SocketClientAsync.GetVector(request);
                        }
                        catch 
                        {
                            responses = null;
                        }

                        if (responses != null)  // response != null when server is up
                        {
                            // if server was down last time we checked but now it is up, trigger a data source change -  removed triggered epg data refresh too often
                            // this should handle the case where MBS starts up BEFORE ServerWMC
                            //if (_serverWMCisDown)
                            //{
                            //    DataSourceChange();
                            //}
                            if (_recTVPath == null)     // if rec tv path has not been set yet, set it now that server is up
                            {
                                var statusResponses = SocketClientAsync.GetVector("GetServerVersion");
                                if (statusResponses.Length > 2)
                                {
                                    _recTVPath = statusResponses[2];        // store the recording tv path if we got one
                                }
                            }
                        }
                        _serverWMCisDown = responses == null;   // store this state of server

                    }, null, 0, (int)new TimeSpan(0, 0, 30).TotalMilliseconds);
        }

        /// <summary>
        /// return true if current server is the REQUIRED version
        /// </summary>
        bool IsServerVersionOK
        {
            get 
            { 
                if (_serverWMC_build <= 0)
                    return false;
                else
                    return _serverWMC_build >= _requiredBuild; 
            }
        }

        string ForcedProfile
        {
            get
            {
                // see if a force transcode profile was set by sneaking it into the Linux path field
                if (Plugin.Instance.Configuration.LinuxPath != null && Plugin.Instance.Configuration.LinuxPath.Contains('|'))
                {
                    if (Plugin.Instance.Configuration.LinuxPath.Split('|').Length > 1)
                    {
                        return Plugin.Instance.Configuration.LinuxPath.Split('|')[1];
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// return true if current server is the up to date
        /// </summary>
        bool IsServerUpTodate
        {
            get 
            {
                var build = GetNewestSWMCBuild();
                if (build > 0)
                    _newestBuild = build;
                if (_serverWMC_build <= 0)
                    return false;
                else
                    return _serverWMC_build >= _newestBuild;
            }
        }


        public int GetNewestSWMCBuild()
        {
            var uri = new Uri(@"https://serverwmc.github.io/updates/AutoUpdate.xml");
            try
            {
                var xml = System.Xml.Linq.XDocument.Load(uri.AbsoluteUri);
                var updateElements = xml.Root.Elements("Update");
                int buildMax = 0;
                foreach (XElement x in updateElements)
                {
                    if (x.Attribute("Channels").Value.Contains("Stable"))
                    {
                        var versionStr = x.Attribute("NewVersion").Value;                   // if this is a stable update type
                        var build = int.Parse(versionStr.Split(new char[] { '.' })[2]);
                        if (build > buildMax)                                               // get stable version with highest build number
                            buildMax = build;
                    }
                }

                // if no build number was found get the version without looking for 'Stable' build type
                if (buildMax == 0)
                {
                    var versionStr = xml.Root.Element("Update").Attribute("NewVersion").Value;
                    buildMax = int.Parse(versionStr.Split(new char[] { '.' })[2]);
                }

                return buildMax;
            }
            catch (Exception ex)
            {
                _logger.Error("Failed check for checking for newest ServerWMC version number: " + ex);
                return 0;
            }
        }


        /// <summary>
        /// Gets the addon name.
        /// </summary>
        public string Name
        {
            get 
            {
                return "ServerWMC";
            }
        }


        public string HomePageUrl
        {
            get { return "http://bit.ly/serverwmc"; }
        }

        /// <summary>
        /// async version of server status check
        /// </summary>
        /// <returns>return true if server is down</returns>
        async Task<bool> isServerDown(CancellationToken cancellationToken)
        {
            string[] responses;
            string request = string.Format("GetServiceStatus|{0}|{1}", _clientVersion, _clientOS);
            responses = await SocketClientAsync.GetVectorAsync(request, cancellationToken);

            // if server is up, keep reading version until the required version of swmc is found
            if (responses != null && !IsServerVersionOK)     
            {
                responses = await SocketClientAsync.GetVectorAsync("GetServerVersion", cancellationToken);
                _serverWMC_version = responses[0];
                _serverWMC_build = int.Parse(responses[1]);
                if (responses.Length > 2)
                {
                    _recTVPath = responses[2];        // store the recording tv path if we got one
                }
            }

            return responses == null;       // if server is down, responses is null
        }

        /// <summary>
        /// should get called when plugin is disposed of by MBS
        /// </summary>
        public void Dispose()
        {
            _recTimer.Dispose();
            AddonGoingDown();
        }

        /// <summary>
        /// tell serverwmc at current configuration IP address that this client is going down
        /// </summary>
        public static void AddonGoingDown()
        {
            if (Instance != null)
                SocketClientAsync.GetBool("ClientGoingDown");
        }

        /// <summary>
        /// prepare a xfer string to send to server, fields are delimited by pipe char, null args are replace with empty strings
        /// </summary>
        private string XferString(params object[] args)
        {
            StringBuilder sb = new StringBuilder();
            foreach (object o in args)
            {
                sb.Append(o ?? string.Empty).Append('|');
            }
            sb.Remove(sb.Length - 1, 1);    // remove last pipe char
            return sb.ToString();
        }

        /// <summary>
        /// convert server padding string val (minutes) to mbs int seconds
        /// </summary>
        private int GetPaddingSeconds(string inPad)
        {
            return (int)(60 * double.Parse(inPad));
        }

        /// <summary>
        /// convert mbs seconds padding to server string val in minutes
        /// </summary>
        private string XferPadding(int inPadSeconds)
        {
            return (((double)inPadSeconds)/60).ToString();
        }

        /// <summary>
        /// return true if MBS is running in Linux AND if user has set the linux path to recTV on configuration
        /// </summary>
        bool useLinuxPath
        {
            get
            {
                return Environment.OSVersion.Platform == PlatformID.Unix && !string.IsNullOrEmpty(_linuxPath);
            }
        }

        /// <summary>
        /// if running in linux and the linux path to the rec tv folder is set, convert RecTV path to a linux path
        /// otherwise return output unchanged
        /// </summary>
        private string TVPath(string inPath)
        {
            if (useLinuxPath && inPath != null && !string.IsNullOrEmpty(_recTVPath) && inPath.StartsWith(_recTVPath))
            {
                // get relative path to data by removing win/UNC recTV path, also replace win slash with linux slash
                string relativePath = inPath.Remove(0, _recTVPath.Length).Replace('\\', '/');   
                // concat the config linux path to rec tv with the relative path
                string newPath = string.Format("{0}/{1}", _linuxPath.TrimEnd('/'), relativePath.TrimStart('/'));
                return newPath;
            }
            else
                return inPath;
        }

        // test for returned error vector from server, handle accompanying messages if any
        bool IsServerError(string[] results, string message = null)
        {
            if (results.Contains("error"))
            {
                if (message != null)
                    throw new Exception(message);
                else if (results.Contains("30020"))                     // string ID '30020' => all tuners for this channel are busy
                    throw new LiveTvConflictException();
                else
                    throw new Exception("ServerWMC Backend error");
            }
            else
                return false;
        }


        string EntryId(string programId)
        {
            return programId.Split(new char[] { '-' })[0];             // extract entryId from combo Id
        }

        // unique program Id is a concat of ScheduleEntry and Channel Id
        string ProgramId(string entryId, string channelId)
        {
            if (channelId == string.Empty || entryId == string.Empty)   // if either are empty make output empty
                return string.Empty;
            else
                return string.Format("{0}-{1}", entryId, channelId);
        }

        bool IsProgramType(int ptype, ProgramType testType)
        {
            return (ptype & (int)testType) != 0;
        }

        ProgramType GetProgramType(ProgramInfo p)
        {
            ProgramType ptype = ProgramType.none;
            if (p != null)
            {
                if (p.IsHD == true) ptype |= ProgramType.IsHD;
                if (p.IsLive) ptype |= ProgramType.IsLive;
                if (p.IsPremiere) ptype |= ProgramType.IsPremiere;
                if (p.IsRepeat) ptype |= ProgramType.IsRepeat;
                if (p.IsSeries) ptype |= ProgramType.IsSeries;
                if (p.IsMovie) ptype |= ProgramType.IsMovie;
                if (p.IsSports) ptype |= ProgramType.IsSports;
                if (p.IsKids) ptype |= ProgramType.IsKids;
                if (p.IsNews) ptype |= ProgramType.IsNews;
            }
            return ptype;
        }

        string CreateStreamId()
        {
            return _idStreamInt++.ToString();
        }

        string GetRating(TVRatingWMC wr)
        {
            string m;
            switch (wr)
            {
                case TVRatingWMC.UsaY:
                    m = "TV-Y";
                    break;
                case TVRatingWMC.UsaY7:
                    m = "TV-Y7";
                    break;
                case TVRatingWMC.UsaG:
                    m = "G";
                    break;
                case TVRatingWMC.UsaPG:
                    m = "PG";
                    break;
                case TVRatingWMC.UsaTV14:
                    m = "TV-14";
                    break;
                case TVRatingWMC.UsaMA:
                    m = "TV-MA";
                    break;
                case TVRatingWMC.DeAll:
                    m = "DE-FSK0";
                    break;
                case TVRatingWMC.De6:
                    m = "DE-FSK6+";
                    break;
                case TVRatingWMC.De12:
                    m = "DE-FSK12+";
                    break;
                case TVRatingWMC.De16:
                    m = "DE-FSK16+";
                    break;
                case TVRatingWMC.DeAdults:
                    m = "DE-FSK18+";
                    break;
                case TVRatingWMC.FrAll:
                    m = "FR-U";
                    break;
                case TVRatingWMC.Fr10:
                    m = "FR-10";
                    break;
                case TVRatingWMC.Fr12:
                    m = "FR-12";
                    break;
                case TVRatingWMC.Fr16:
                    m = "FR-16";
                    break;
                case TVRatingWMC.Fr18:
                    m = "FR-18";
                    break;
                case TVRatingWMC.KrAll:
                    m = "KR-ALL";
                    break;
                case TVRatingWMC.Kr7:
                    m = "KR-7";
                    break;
                case TVRatingWMC.Kr12:
                    m = "KR-12";
                    break;
                case TVRatingWMC.Kr15:
                    m = "KR-15";
                    break;
                case TVRatingWMC.Kr19:
                    m = "KR-19";
                    break;
                case TVRatingWMC.GB_UC:
                    m = "GB-U";
                    break;
                case TVRatingWMC.GbU:
                    m = "GB-U";
                    break;
                case TVRatingWMC.GbPG:
                    m = "GB-PG";
                    break;
                case TVRatingWMC.Gb12:
                    m = "GB-12";
                    break;
                case TVRatingWMC.Gb15:
                    m = "GB-15";
                    break;
                case TVRatingWMC.Gb18:
                    m = "GB-18";
                    break;
                case TVRatingWMC.GbR18:
                    m = "GB-R18";
                    break;
                default:
                    m = string.Empty;
                    break;
            }
            return m;
        }


        /// <summary>
        /// convert input transfer string containing media info to a MediaStream object
        /// </summary>
        /// <param name="str">input string contain propNam-propVal pairs, delimited by commas</param>
        /// <returns>new MediaStream based on input string data</returns>
        MediaStream GetMediaStream(string str)
        {
            var slist = str.Split(',');
            var ms = new MediaStream();
            var msType = ms.GetType();
            for (int i = 0; i < slist.Count(); i += 2)
            {
                try
                {
                    string propName = slist[i];                     // get prop name from input
                    string propVal = slist[i + 1];                  // get adjacent prop value from input

                    if (propVal != string.Empty)                    // only set property if prop value is not empty string
                    {
                        var prop = msType.GetProperty(propName);    // get mediastream property by name
                        if (prop != null)
                        {
                            if (prop.PropertyType == typeof(MediaStreamType))                                   // need to handle enums separately
                                ms.Type = (MediaStreamType)Enum.Parse(typeof(MediaStreamType), propVal);
                            else
                            {
                                Type tp = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;   // handles nullable int, float, etc
                                prop.SetValue(ms, Convert.ChangeType(propVal, tp));                             // set prop value, changing type if necessary
                            }
                        }
                        else
                            _logger.Error("GetMediaStream> Prop name: " + propName + " not found");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error("GetMediaStream> Media stream parse error: {0}", ex);
                }
            }
            return ms;
        }

        #endregion

        #region channels


        public async Task<MediaSourceInfo> GetChannelStream(string channelId, string transcodeProfileId, CancellationToken cancellationToken)
        {
            if (!IsServerVersionOK && await isServerDown(cancellationToken))
                throw new Exception("ServerWMC is down");

            string streamId = CreateStreamId();
            string[] responses;
            string profileCmd = "";

            if (ForcedProfile != null)                                  // if a forced profile was set always use it (for debugging)
            {
                transcodeProfileId = ForcedProfile;
            }
            
            if (!string.IsNullOrEmpty(transcodeProfileId))              // use transcode profile name (if any)
                profileCmd = "TranscodeProfile:" + transcodeProfileId;

            responses = await SocketClientAsync.GetVectorAsync(XferString("OpenLiveStream", channelId, /*getStreamINFO,*/ profileCmd),
                                                                cancellationToken, streamId);


            if (!IsServerError(responses) && responses[0] != String.Empty)  // if serverwmc was able to open the stream
            {
                var strm = responses[0];                                // get stream (either ts file path or url)
                bool isTS = strm.EndsWith(".ts");                       // detect path type (if file path)
                bool isUrl = strm.StartsWith(@"http://");               // true if strm file is dlna address or wcf url
                bool isDlna = isUrl && !strm.Contains("serverwmc");     // true if input url is dlna address

                if (!isUrl)
                    strm = TVPath(strm);                    // convert to linux path (if necessary)

                if (isUrl)                  // if stream file path is a url (user has set swmc to use urls with wcf service)
                {
                    // if swmc version is greater >= 1234 do nothing when stream is a url, because for this case the stream 
                    // will be validated when the VideoFileStream is first read, if it never gets read from the stream will be purged
                    // also send StreamFileSize if stream is a dlna path (just for safty sake)
                    if (_serverWMC_build < 1234 || !strm.Contains("serverwmc"))
                    {
                        await SocketClientAsync.GetVectorAsync(XferString("StreamFileSize", 1d), cancellationToken, streamId);
                    }
                }
                else if (_fileSystem.FileExists(strm)) // if the path is a file path
                {
                    // give serverWMC and response so that it knows the stream  was found
                    // if there is a problem accessing it, the mbs core will take care of it
                    await SocketClientAsync.GetVectorAsync(XferString("StreamFileSize", 1d), cancellationToken, streamId);
                }
                else    // tell server file was not accesible and throw an error
                {
                    _logger.Error("GetChannelStream> stream file not found: {0}", strm);
                    // tell server stream did not start
                    await SocketClientAsync.GetVectorAsync(XferString("StreamStartError", strm), cancellationToken, streamId);
                    throw new Exception("ServerWMC: Stream file not not found: " + strm);
                }

                List<MediaStream> mstreams = new List<MediaStream>();

                MediaSourceInfo mediaInfo;

                if (isUrl && transcodeProfileId != null && strm.Contains(transcodeProfileId))   // if the address returned uses the transcode profile requested
                {
                    mediaInfo = GetMediaSource(transcodeProfileId);                             // get mediainfo using the profile name
                    mediaInfo.Id = streamId;
                    mediaInfo.Path = strm;
                    mediaInfo.IgnoreDts = true;
                }
                else                                                                            // for ALL other cases, see if swmc sent us a/v stream info
                {
                    // see if responses contain any stream info, if it does fill out the media stream fields
                    // using GetMediaStream and matching properties to names
                    // NOTE in dlna mode this stream info will not be returned so mstreams will be empty
                    foreach (string res in responses)
                    {
                        if (res.StartsWith(streamInfoHEADER))
                        {
                            mstreams.Add(GetMediaStream(res.Replace(streamInfoHEADER, "")));
                        }
                    }

                    // get video stream
                    MediaStream vidStream = mstreams.FirstOrDefault(ms => ms.Type == MediaStreamType.Video);

                    // get default audio stream
                    MediaStream mainAudio;
                    if (_serverWMC_build < 1238)                    // in older builds swmc does not order audio list, in that case order by # of channels
                        mainAudio = mstreams.Where(ms => ms.Type == MediaStreamType.Audio).OrderByDescending(ms => ms.Channels).FirstOrDefault();
                    else                                            // newer builds pre-order by prefered language then by # of channels
                        mainAudio = mstreams.FirstOrDefault(ms => ms.Type == MediaStreamType.Audio);

                    // fill out any parameters we have (common for both files and urls)
                    mediaInfo = new MediaSourceInfo
                    {
                        // general
                        Id = streamId,
                        Path = strm,
                        MediaStreams = mstreams,        // never set MediaStreams to null, if no steam info was returned leave it empty
                        DefaultAudioStreamIndex = (mainAudio != null) ? (int?)mainAudio.Index : null,
                        RequiresOpening = true,
                        RequiresClosing = true,
                        IgnoreDts = true,
                        Bitrate = (vidStream != null) ? (int?)((double)vidStream.BitRate * 1.1) : null,  // don't know if this is useful
                    };
                }

                // handle general differences between files and urls
                if (isUrl)
                {
                    mediaInfo.Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.Http;
                    mediaInfo.ReadAtNativeFramerate = false;            // false for urls
                    if (!isDlna)                                        // don't set container type for dlna addresses
                        mediaInfo.Container = "ts"; 
                }
                else 
                {
                    mediaInfo.Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.File;
                    mediaInfo.ReadAtNativeFramerate = true;             // true for files
                    mediaInfo.Container = isTS ? "ts" : "wtv";
                }

                return mediaInfo;       // return the stream
            } // if serverwmc was able to open the stream


            // if we got to here something went wrong, so shutdown the stream
            await CloseLiveStream(streamId, cancellationToken);

            return null; // starting stream failed
        }

        public async Task CloseLiveStream(string id, System.Threading.CancellationToken cancellationToken)
        {
            string[] responses = await SocketClientAsync.GetVectorAsync(XferString("CloseLiveStream"), cancellationToken, id);

            //if (!IsServerError(responses) && responses[0] != String.Empty)
            //{
            //    var tsFile = responses[0];
            //}

        }

        public async Task RecordLiveStream(string id, CancellationToken cancellationToken)
        {
            string[] responses = await SocketClientAsync.GetVectorAsync(XferString("RecordLiveStream"), cancellationToken, id);

            if (!IsServerError(responses) && responses[0] != String.Empty)
            {
                PVR_TIMER_STATE rState = (PVR_TIMER_STATE)Enum.Parse(typeof(PVR_TIMER_STATE), responses[0]);
                string title = responses[1];

                if (rState == PVR_TIMER_STATE.PVR_TIMER_STATE_ERROR)
                    throw new Exception("ServerWMC: Not able to start recording");

            }
        }

        public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(System.Threading.CancellationToken cancellationToken)
        {
            if (await isServerDown(cancellationToken))
                throw new Exception("ServerWMC is down");

            if (!IsServerVersionOK)                                     // only check for required server version here and status
                throw new Exception("ServerWMC needs to be updated");

            var channels = new List<ChannelInfo>();

            string[] responses = await SocketClientAsync.GetVectorAsync("GetChannels", cancellationToken);

            try
            {
                foreach (string response in responses)
                {
                    var v = response.Split('|');				// split to unpack string
                    ChannelInfo mCh = new ChannelInfo();
                    mCh.Id = v[0];
                    mCh.ChannelType = bool.Parse(v[1]) ? ChannelType.Radio : ChannelType.TV;
                    //xChannel.iChannelNumber = atoi(v[2].c_str());
                    //STRCPY(xChannel.strChannelName, v[3].c_str());
                    //xChannel.iEncryptionSystem = Str2Bool(v[4]);
                    if (v[5].StartsWith(HTTP))
                        mCh.ImageUrl = v[5];
                    else
                        mCh.ImagePath = TVPath(v[5]);
                    mCh.HasImage = false; //v[5] != null;        
                    //xChannel.bIsHidden = Str2Bool(v[6]);
                    mCh.Number = v[7];
                    mCh.Name = v[8];
                    channels.Add(mCh);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("GetChannelsAsync> " + ex);
                throw ex;
            }

            return channels;
        }

        // not used
        public Task<ImageStream> GetChannelImageAsync(string channelId, System.Threading.CancellationToken cancellationToken)
        {
            throw new NotImplementedException();

        //    string[] responses = await _socketClient.GetVectorAsync(XferString("GetChannelImage", channelId));

        //    if (!IsServerError(responses) && responses[0] != String.Empty)
        //    {
        //        var file = responses[0];
        //        var extension = Path.GetExtension(file);

        //        return new StreamResponseInfo
        //        {
        //            Stream = _fileSystem.GetFileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read),
        //            MimeType = "image/" + extension
        //        };
        //    }
        //    return null;
        }
       
        #endregion

        #region timers

        public async Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(System.Threading.CancellationToken cancellationToken, ProgramInfo prog)
        {
            string[] responses = await SocketClientAsync.GetVectorAsync(XferString("GetNewTimerDefaults", GetProgramType(prog)), cancellationToken);

            if (!IsServerError(responses))
            {
                // convert default daylist received from backend
                int idays = int.Parse(responses[6]);
                var dayList = new List<DayOfWeek>();
                for (int i = 0; i < 8; i++)
                {
                    uint shiftMe = (int)DaysOfWeek.Sunday;
                    if ((idays & (shiftMe << i)) != 0)
                        dayList.Add((DayOfWeek)Enum.ToObject(typeof(DayOfWeek), i));
                }

                // older versions of the server don't send over 'anytime' flag, so only use if its there
                bool anyTime = (responses.Length > 7) ? bool.Parse(responses[7]) : false;

                return new SeriesTimerInfo
                {
                    PrePaddingSeconds = GetPaddingSeconds(responses[0]),  
                    PostPaddingSeconds = GetPaddingSeconds(responses[1]),
                    IsPrePaddingRequired = bool.Parse(responses[2]),
                    IsPostPaddingRequired = bool.Parse(responses[3]),
                    RecordAnyChannel = bool.Parse(responses[4]),
                    RecordNewOnly = bool.Parse(responses[5]),
                    Days = dayList,  // field [6]
                    RecordAnyTime = anyTime  // field [7]
                };
            }
            return null;
        }

        public async Task CreateTimerAsync(TimerInfo info, System.Threading.CancellationToken cancellationToken)
        {

            string entryId = EntryId(info.ProgramId);                      // extract entryId from combo Id

            if (DateTimeOffset.UtcNow >= info.EndDate)
            {
                _logger.Error("CreateTimerAsync> requested program '{0}' has already aired;  EndTime(UTC): {1},  CurrentTime(UTC): {2}", info.Name, info.EndDate, DateTimeOffset.UtcNow);
                throw new Exception("ServerWMC: Can't record: program occurs in the past");
            }

            string[] responses = await SocketClientAsync.GetVectorAsync(
                                XferString (    "SetTimer",
                                                -1,                                         // => epg based timer
                                                info.ChannelId,
                                                Utilities.ToTime_t(info.StartDate),
                                                Utilities.ToTime_t(info.EndDate),
                                                (int)PVR_TIMER_STATE.PVR_TIMER_STATE_NEW,
                                                info.Name,                                  
                                                0,                                          // xbmc's priority - not used
                                                XferPadding(info.PrePaddingSeconds),
                                                XferPadding(info.PostPaddingSeconds),
                                                false,                                      // => not a series timer
                                                entryId,                                    // SchedulEntry ID
                                                info.IsPrePaddingRequired,
                                                info.IsPostPaddingRequired
                                            ), cancellationToken);

            if (!IsServerError(responses))
            {
                LastRecordingChange = DateTimeOffset.UtcNow;

                _logger.Info("CreateTimerAsync> recording added for timer '{0}', status {1}", info.Name, info.Status);

		        if (responses.Length > 1)								        // if there is extra results sent from server...
		        {
                    foreach (string result in responses)
			        {
                        string[] splitResult = result.Split(new char[]{'|'});   // split to unpack extra info on each result

				        if (splitResult[0] == "recordingNow")					// recording is active now
				        {
                            // todo: refresh recording screen somehow
				        }
				        else if (splitResult[0] == "recordingChannel")			// service picked a different channel for timer
				        {
                            _logger.Info("CreateTimerAsync> timer channel changed by wmc to '{0}'", splitResult[1]);
				        }
				        else if (splitResult[0] == "recordingTime")				// service picked a different start time for timer
				        {
                            _logger.Info("CreateTimerAsync> timer start time changed by wmc to '{0}'", splitResult[1]);
				        }
				        else if (splitResult[0] == "increasedEndTime")			// end time has been increased on an instant record
                        {
                            _logger.Info("CreateTimerAsync> instant record end time increased by '{0}' minutes", splitResult[1]);
				        }
			        }
		        }
            }
        }

        public async Task CreateSeriesTimerAsync(SeriesTimerInfo info, System.Threading.CancellationToken cancellationToken)
        {
            int iDays = 0;
            foreach (DayOfWeek day in info.Days)
            {
                DaysOfWeek wDay = (DaysOfWeek)Enum.Parse(typeof(DaysOfWeek), day.ToString());
                iDays |= (int)wDay;
            }

            string entryId = EntryId(info.ProgramId);                           // extract entryId from combo Id

            string[] responses = await SocketClientAsync.GetVectorAsync(
                                XferString(     "CreateSeriesTimer",
                                                info.Id,                            
                                                info.Name,
                                                info.ChannelId,
                                                entryId,                         // ScheduleEntry Id
                                                Utilities.ToTime_t(info.StartDate),
                                                Utilities.ToTime_t(info.EndDate),
                                                XferPadding(info.PrePaddingSeconds),
                                                XferPadding(info.PostPaddingSeconds),
                                                info.IsPrePaddingRequired,
                                                info.IsPostPaddingRequired,
                                                info.RecordAnyChannel,
                                                info.RecordNewOnly,
                                                info.RecordAnyTime,          
                                                iDays
                                            ), cancellationToken); 

            if (!IsServerError(responses))
            {
                LastRecordingChange = DateTimeOffset.UtcNow;
            }
        }

        public async Task<IEnumerable<TimerInfo>> GetTimersAsync(System.Threading.CancellationToken cancellationToken)
        {
            var timers = new List<TimerInfo>();

            string[] responses = await SocketClientAsync.GetVectorAsync("GetTimers", cancellationToken);

            try
            {
                foreach (string response in responses)
                {
                    TimerInfo mTmr = new TimerInfo();

                    var v = response.Split('|');				// split to unpack returned fields

                    mTmr.Id = v[0];
                    mTmr.ChannelId = v[1];
                    mTmr.StartDate = Utilities.ToDateTime(v[2]);
                    mTmr.EndDate = Utilities.ToDateTime(v[3]);
                    PVR_TIMER_STATE pState = (PVR_TIMER_STATE)Enum.Parse(typeof(PVR_TIMER_STATE), v[4]);    // get xbmc style recording state

                    mTmr.Name = v[5];
                    //mTmr.strDirectory v[6]
                    mTmr.Overview = v[7];
                    //xTmr.iPriority = atoi(v[8].c_str());				// rec priority
                    //xTmr.bIsRepeating = Str2Bool(v[9].c_str());			// repeating rec 
                    //xTmr.iEpgUid = atoi(v[10].c_str());					// epg ID 
                    mTmr.PrePaddingSeconds = GetPaddingSeconds(v[11]);     // requested padding
                    mTmr.PostPaddingSeconds = GetPaddingSeconds(v[12]);

                    //xTmr.iGenreType = atoi(v[13].c_str());				// genre ID
                    //xTmr.iGenreSubType = atoi(v[14].c_str());			// sub genre ID

                    // mb3 only fields
                    mTmr.ProgramId = ProgramId(v[15], mTmr.ChannelId);
                    mTmr.SeriesTimerId = v[16];                             // the id of the series timer this timer is part of (empty string if not part of series)
                    mTmr.IsPrePaddingRequired = bool.Parse(v[17]);      // 'required' padding 
                    mTmr.IsPostPaddingRequired = bool.Parse(v[18]);


                    switch (pState)  // translate to mb3 record state
                    {
                        case PVR_TIMER_STATE.PVR_TIMER_STATE_NEW:
                            mTmr.Status = RecordingStatus.New;
                            break;
                        case PVR_TIMER_STATE.PVR_TIMER_STATE_SCHEDULED:
                            mTmr.Status = RecordingStatus.New;
                            break;
                        case PVR_TIMER_STATE.PVR_TIMER_STATE_RECORDING:
                            mTmr.Status = RecordingStatus.InProgress;
                            break;
                        case PVR_TIMER_STATE.PVR_TIMER_STATE_COMPLETED:
                            mTmr.Status = RecordingStatus.Completed;
                            break;
                        case PVR_TIMER_STATE.PVR_TIMER_STATE_ABORTED:
                            mTmr.Status = RecordingStatus.Cancelled;
                            break;
                        case PVR_TIMER_STATE.PVR_TIMER_STATE_CANCELLED:
                            mTmr.Status = RecordingStatus.Cancelled;
                            break;
                        case PVR_TIMER_STATE.PVR_TIMER_STATE_CONFLICT_OK:
                            mTmr.Status = RecordingStatus.ConflictedOk;
                            break;
                        case PVR_TIMER_STATE.PVR_TIMER_STATE_CONFLICT_NOK:
                            mTmr.Status = RecordingStatus.ConflictedNotOk;
                            break;
                        case PVR_TIMER_STATE.PVR_TIMER_STATE_ERROR:
                            mTmr.Status = RecordingStatus.Error;
                            break;
                    }

                    timers.Add(mTmr);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("GetTimersAsync> " + ex);
                throw ex;
            }


            return timers;
        }

        public async Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(System.Threading.CancellationToken cancellationToken)
        {
            var timers = new List<SeriesTimerInfo>();

            string[] responses = await SocketClientAsync.GetVectorAsync("GetSeriesTimers", cancellationToken);

            try
            {
                foreach (string response in responses)
                {
                    var v = response.Split('|');				// split to unpack returned fields
                    SeriesTimerInfo sTmr = new SeriesTimerInfo
                    {
                        Id = v[0],
                        Name = v[1],
                        ChannelId = v[2],
                        ProgramId = ProgramId(v[3], v[2]),
                        Overview = v[4],
                        StartDate = Utilities.ToDateTime(v[5]),
                        EndDate = Utilities.ToDateTime(v[6]),
                        PrePaddingSeconds = GetPaddingSeconds(v[7]),
                        PostPaddingSeconds = GetPaddingSeconds(v[8]),
                        IsPrePaddingRequired = bool.Parse(v[9]),
                        IsPostPaddingRequired = bool.Parse(v[10]),
                        Priority = int.Parse(v[11]),
                        RecordNewOnly = bool.Parse(v[12]),
                        RecordAnyChannel = bool.Parse(v[13]),
                        RecordAnyTime = bool.Parse(v[14])
                    };

                    int idays = int.Parse(v[15]);

                    // convert from wmc DaysOfWeek to mb3 Days list
                    sTmr.Days = new List<DayOfWeek>();
                    for (int i = 0; i < 8; i++)
                    {
                        uint shiftMe = (int)DaysOfWeek.Sunday;
                        if ((idays & (shiftMe << i)) != 0)
                            sTmr.Days.Add((DayOfWeek)Enum.ToObject(typeof(DayOfWeek), i));
                    }

                    timers.Add(sTmr);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("GetSeriesTimersAsync> " + ex);
                throw ex;
            }

            return timers;
        }

        public async Task CancelTimerAsync(string timerId, System.Threading.CancellationToken cancellationToken)
        {
            string[] responses = await SocketClientAsync.GetVectorAsync(XferString("CancelTimer", timerId), cancellationToken);

            if (!IsServerError(responses))
            {
                LastRecordingChange = DateTimeOffset.UtcNow;
            }
        }

        public async Task CancelSeriesTimerAsync(string timerId, System.Threading.CancellationToken cancellationToken)
        {
            string[] responses = await SocketClientAsync.GetVectorAsync(XferString("CancelSeriesTimer", timerId), cancellationToken);

            if (!IsServerError(responses))
            {
                LastRecordingChange = DateTimeOffset.UtcNow;
            }
        }

        public async Task UpdateTimerAsync(TimerInfo info, System.Threading.CancellationToken cancellationToken)
        {
            string entryId = EntryId(info.ProgramId);                 // extract entryId from combo Id

            string[] responses = await SocketClientAsync.GetVectorAsync(
                                XferString(     "UpdateTimer",
                                                info.Id,
                                                info.Name,
                                                info.ChannelId,
                                                entryId,                              // ScheduleEntry Id
                                                Utilities.ToTime_t(info.StartDate),
                                                Utilities.ToTime_t(info.EndDate),
                                                XferPadding(info.PrePaddingSeconds),
                                                XferPadding(info.PostPaddingSeconds),
                                                info.IsPrePaddingRequired,
                                                info.IsPostPaddingRequired,
                                                info.Priority
                                            ), cancellationToken);

            if (!IsServerError(responses))
            {
                LastRecordingChange = DateTimeOffset.UtcNow;
            }
        }

        public async Task UpdateSeriesTimerAsync(SeriesTimerInfo info, System.Threading.CancellationToken cancellationToken)
        {
            int iDays = 0;
            foreach (DayOfWeek day in info.Days)
            {
                DaysOfWeek wDay = (DaysOfWeek)Enum.Parse(typeof(DaysOfWeek), day.ToString());
                iDays |= (int)wDay;
            }

            string entryId = EntryId(info.ProgramId);

            string[] responses = await SocketClientAsync.GetVectorAsync(
                                XferString(     "UpdateSeriesTimer",
                                                info.Id,
                                                info.Name,
                                                info.ChannelId,
                                                entryId,                                // ScheduleEntry Id
                                                Utilities.ToTime_t(info.StartDate),
                                                Utilities.ToTime_t(info.EndDate),
                                                XferPadding(info.PrePaddingSeconds),
                                                XferPadding(info.PostPaddingSeconds),
                                                info.IsPrePaddingRequired,
                                                info.IsPostPaddingRequired,
                                                info.RecordAnyChannel,
                                                info.RecordNewOnly,
                                                info.RecordAnyTime,          
                                                iDays,
                                                info.Priority
                                            ), cancellationToken);

            if (!IsServerError(responses))
            {
                LastRecordingChange = DateTimeOffset.UtcNow;
            }
        }

        #endregion

        #region recordings

        public async Task<MediaSourceInfo> GetRecordingStream(string recordingId, string transcodeProfileId, System.Threading.CancellationToken cancellationToken)
        {
            if (!IsServerVersionOK && await isServerDown(cancellationToken))
                throw new Exception("ServerWMC is down");

            string streamId = CreateStreamId();

            if (!string.IsNullOrEmpty(transcodeProfileId))
                transcodeProfileId = "TranscodeProfile:" + transcodeProfileId;

            string[] responses = await SocketClientAsync.GetVectorAsync(    XferString("OpenRecordingStream", recordingId, transcodeProfileId),
                                                                            cancellationToken, streamId);

            // old version where we requested streamINFO, the new version of emby get the stream info itself
            //string[] responses = await SocketClientAsync.GetVectorAsync(XferString("OpenRecordingStream", recordingId, getStreamINFO, transcodeProfileId),
            //                                                                cancellationToken, streamId);

            //System.Threading.Thread.Sleep(15000);         // wait to build up some data, since growing file is not detected

            if (!IsServerError(responses) && responses[0] != String.Empty)
            {
                string strmFile = responses[0];                 // get file to stream (unix friendly)

                // get runlength in ticks
                long d;
                long? runTimeTicks = (responses[3] != "" && long.TryParse(responses[3], out d)) ? (long?)d : null;

                bool isTS = strmFile.EndsWith(".ts");           // true if path returned in to a ts file, false if wtv or url
                bool isUrl = strmFile.StartsWith(HTTP);         // true if path is a url

                if (!isUrl)
                    strmFile = TVPath(strmFile);                // correct for possible unix mount paths

                if (isUrl || _fileSystem.FileExists(strmFile)) 
                {
                    // give serverWMC and response so that it knows the stream  was found
                    // if there is a problem accessing it, the mbs core will take care of it
                    await SocketClientAsync.GetVectorAsync(XferString("StreamFileSize", 1d), cancellationToken, streamId);
                }
                else    // tell server file was not accesible and throw an error
                {
                    _logger.Error("GetRecordingStream> stream file not found: {0}", strmFile);
                    // tell server stream did not start
                    await SocketClientAsync.GetVectorAsync(XferString("StreamStartError", strmFile), cancellationToken, streamId);   
                    throw new Exception("ServerWMC: Stream file not not found: " + strmFile);
                }

                var mstreams = new List<MediaStream>();

                // see if responses contain stream info
                //foreach (string res in responses)
                //{
                //    if (res.StartsWith(streamInfoHEADER))
                //        mstreams.Add(GetMediaStream(res.Replace(streamInfoHEADER, "")));
                //}

                //MediaStream vidStream = mstreams.FirstOrDefault(ms => ms.Type == MediaStreamType.Video);

                //// get default audio stream
                //MediaStream mainAudio;
                //if (_serverWMC_build < 1238)                    // in older builds swmc does not order audio list, in that case order by # of channels
                //    mainAudio = mstreams.Where(ms => ms.Type == MediaStreamType.Audio).OrderByDescending(ms => ms.Channels).FirstOrDefault();
                //else                                            // newer builds pre-order by prefered language then by # of channels
                //    mainAudio = mstreams.FirstOrDefault(ms => ms.Type == MediaStreamType.Audio);    // the first audio channel will be the preferred audio

                // set common media info parameters
                var mediaInfo = new MediaSourceInfo
                {
                    Id = streamId,
                    Path = strmFile,
                    RunTimeTicks = runTimeTicks,
                    RequiresOpening = true,
                    RequiresClosing = true,
                    MediaStreams = mstreams,
                    IgnoreDts = true
                    //DefaultAudioStreamIndex = (mainAudio != null) ? (int?)mainAudio.Index : null,       // these are now obsoleted since swmc doesn't return stream info
                    //Bitrate = (vidStream != null) ? (int?)((double)vidStream.BitRate * 1.1) : null,     // these are now obsoleted since swmc doesn't return stream info
                };

                if (isUrl)
                {
                    mediaInfo.Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.Http;
                    mediaInfo.ReadAtNativeFramerate = false;  
                    mediaInfo.Container = "ts";     // urls are always ts (should be okay even if using dlna too)
                    mediaInfo.IgnoreDts = true;
                }
                else
                {
                    mediaInfo.Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.File;
                    mediaInfo.ReadAtNativeFramerate = true;
                    mediaInfo.Container = isTS ? "ts" : "wtv";
                    mediaInfo.IgnoreDts = true;
                }

                return mediaInfo;
            }

            return null;
        }
    

        public async Task DeleteRecordingAsync(string recordingId, System.Threading.CancellationToken cancellationToken)
        {
            string[] responses = await SocketClientAsync.GetVectorAsync(XferString("DeleteRecording", recordingId), cancellationToken);

            if (!IsServerError(responses))
            {
            }
        }


        public async Task<IEnumerable<RecordingInfo>> GetRecordingsAsync(System.Threading.CancellationToken cancellationToken)
        {
            return new List<RecordingInfo>();
        }

        public async Task<IEnumerable<MyRecordingInfo>> GetAllRecordingsAsync(System.Threading.CancellationToken cancellationToken)
        {
            var recordings = new List<MyRecordingInfo>();

            char[] dlim = { ';' };

            string[] responses = await SocketClientAsync.GetVectorAsync("GetRecordings", cancellationToken);

            try
            {
                foreach (string response in responses)
                {
                    MyRecordingInfo mRec = new MyRecordingInfo();

                    var v = response.Split('|');                // split to unpack string


                    //   if (long.Parse(v[10]) < 0)
                    //       Debug.WriteLine("");

                    mRec.Id = v[0];
                    mRec.Name = v[1];
                    if (TVPath(v[2]).StartsWith(HTTP))
                        mRec.Url = v[2];
                    else
                        mRec.Path = TVPath(v[2]);

                    //STRCPY(xRec.strDirectory, v[3].c_str());
                    mRec.EpisodeTitle = v[4];
                    mRec.Overview = v[5];
                    //ChannelName = v[6];
                    if (v[7] != string.Empty)
                    {
                        if (v[7].StartsWith(HTTP))          // if path is web url
                            mRec.ImageUrl = v[7];
                        else                                // otherwise assume unc path
                            mRec.ImagePath = TVPath(v[7]);
                    }
                    mRec.HasImage = false;// v[7] != string.Empty;
                    //STRCPY(xRec.strThumbnailPath, v[8].c_str());
                    mRec.StartDate = Utilities.ToDateTime(v[9]);
                    mRec.EndDate = mRec.StartDate.AddSeconds(int.Parse(v[10]));
                    //xRec.iPriority = atoi(v[11].c_str());
                    //xRec.iLifetime = atoi(v[12].c_str());
                    //xRec.iGenreType = atoi(v[13].c_str());
                    //xRec.iGenreSubType = atoi(v[14].c_str());
                    //xRec.ilastPlayPosition = atoi(v[15].c_str());
                    RecordingState_WMC pState = (RecordingState_WMC)Enum.Parse(typeof(RecordingState_WMC), v[16]);    // get xbmc style recording state
                    mRec.ChannelId = v[17];
                    mRec.ProgramId = ProgramId(v[18], mRec.ChannelId);
                    mRec.SeriesTimerId = v[19];
                    mRec.Audio = (ProgramAudio)Enum.Parse(typeof(ProgramAudio), v[20]);
                    mRec.OriginalAirDate = Utilities.ToDateTime(v[21]);
                    mRec.Genres = v[22].Split(dlim, StringSplitOptions.RemoveEmptyEntries).ToList();

                    // set episode flags from ProgramType
                    int pType = int.Parse(v[23]);
                    mRec.IsRepeat = IsProgramType(pType, ProgramType.IsRepeat);
                    mRec.IsHD = IsProgramType(pType, ProgramType.IsHD);
                    mRec.IsSeries = IsProgramType(pType, ProgramType.IsSeries);
                    mRec.IsMovie = IsProgramType(pType, ProgramType.IsMovie);
                    mRec.IsSports = IsProgramType(pType, ProgramType.IsSports);
                    mRec.IsKids = IsProgramType(pType, ProgramType.IsKids);
                    mRec.IsLive = IsProgramType(pType, ProgramType.IsLive);
                    mRec.IsNews = IsProgramType(pType, ProgramType.IsNews);
                    mRec.IsPremiere = IsProgramType(pType, ProgramType.IsPremiere);


                    // set Status based on wmc enum
                    switch (pState)
                    {
                        case RecordingState_WMC.None:
                            mRec.Status = RecordingStatus.Error;   // not sure about this one
                            break;
                        case RecordingState_WMC.Scheduled:
                            mRec.Status = RecordingStatus.New;
                            break;
                        case RecordingState_WMC.Initializing:
                            mRec.Status = RecordingStatus.InProgress;
                            break;
                        case RecordingState_WMC.Recording:
                            mRec.Status = RecordingStatus.InProgress;
                            break;
                        case RecordingState_WMC.Recorded:
                            mRec.Status = RecordingStatus.Completed;
                            break;
                        case RecordingState_WMC.Deleted:
                            mRec.Status = RecordingStatus.Cancelled;
                            break;
                    }

                    recordings.Add(mRec);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("GetRecordingsAsync> " + ex.Message);
                throw ex;
            }

            return recordings;
        }

        // this function not called since all recordinginfo has 'hasImage' set to false;
        public Task<ImageStream> GetRecordingImageAsync(string recordingId, System.Threading.CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
        #endregion

        #region epg


        public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTimeOffset startDateUtc, DateTimeOffset endDateUtc, CancellationToken cancellationToken)
        {
            var programs = new List<ProgramInfo>();

            if (!IsServerVersionOK && await isServerDown(cancellationToken))
                throw new Exception("ServerWMC is down");

            char[] dlim = { ';' };

            string[] responses = await SocketClientAsync.GetVectorAsync(string.Format("GetEntries|{0}|{1}|{2}",
                                                                        channelId,
                                                                        Utilities.ToTime_t(startDateUtc),
                                                                        Utilities.ToTime_t(endDateUtc)
                                                                        ), cancellationToken);

            try
            {
                foreach (string response in responses)
                {
                    ProgramInfo mProg = new ProgramInfo();

                    var v = response.Split('|');				// split to unpack string

                    // ProgramId = "entryId-channelId" to ensure uniqueness
                    mProg.Id = ProgramId(v[0], v[16]); 
                    mProg.Name = v[1];
                    //mProg.ChannelName = v[2];
                    mProg.StartDate = Utilities.ToDateTime(v[3]);
                    mProg.EndDate = Utilities.ToDateTime(v[4]);
                    var tStart = mProg.StartDate.ToLocalTime();
                    var tEnd = mProg.EndDate.ToLocalTime();

                    mProg.ShortOverview = v[5];
                    mProg.Overview = v[6];
                    //mProg.Plot = Utilities.ToDateTime(v[6]);
                    mProg.OriginalAirDate = Utilities.ToDateTime(v[7]);
                    mProg.OfficialRating = GetRating((TVRatingWMC)Enum.Parse(typeof(TVRatingWMC), v[8]));   // tv rating
                    mProg.CommunityRating = 1;// float.Parse(v[9]);      // setting equal to star rating not working
                    mProg.SeasonNumber = Utilities.ToInt(v[10], true);   //10  epg123 returns this field as nonZero
                    mProg.EpisodeNumber = Utilities.ToInt(v[11], true);   //11  epg123 returns this field as nonZero
                    //mProg.Genre  //12
                    //mProg.SubGenre  //13
                    //mProg.ImageUrl = v[14];
                    if (v[14] != string.Empty)
                    {
                        if (v[14].StartsWith(HTTP))             // if path is web url
                            mProg.ImageUrl = v[14];
                        else                                    // otherwise assume unc path
                            mProg.ImagePath = TVPath(v[14]);
                    }

                    mProg.HasImage = false;// v[14] != "";  
                    mProg.EpisodeTitle = v[15];

                    // mb3 only
                    mProg.ChannelId = v[16];
                    mProg.Audio = (ProgramAudio)Enum.Parse(typeof(ProgramAudio), v[17]);
                    mProg.Genres = v[18].Split(dlim, StringSplitOptions.RemoveEmptyEntries).ToList();

                    int pType = int.Parse(v[19]);
                    mProg.IsRepeat = IsProgramType(pType, ProgramType.IsRepeat);
                    mProg.IsHD = IsProgramType(pType, ProgramType.IsHD);
                    mProg.IsSeries = IsProgramType(pType, ProgramType.IsSeries);
                    mProg.IsMovie = IsProgramType(pType, ProgramType.IsMovie);
                    mProg.IsSports = IsProgramType(pType, ProgramType.IsSports);
                    mProg.IsKids = IsProgramType(pType, ProgramType.IsKids);
                    mProg.IsLive = IsProgramType(pType, ProgramType.IsLive);
                    mProg.IsNews = IsProgramType(pType, ProgramType.IsNews);
                    mProg.IsPremiere = IsProgramType(pType, ProgramType.IsPremiere);  

                    if (v.Count() > 20)  // really old versions of serverwmc won't return fields above 19
                    {
                        mProg.ProductionYear = Utilities.ToInt(v[23], true);
                    }

                    programs.Add(mProg);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("GetProgramsAsync> " + ex.Message);
                throw ex;
            }

            return programs;
        }

        // not called, since hasImage is set false on all program info
        public Task<ImageStream> GetProgramImageAsync(string programId, string channelId, System.Threading.CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region events

        public event EventHandler DataSourceChanged;
        static public void DataSourceChange()
        {
            if (Instance != null && Instance.DataSourceChanged != null)
                Instance.DataSourceChanged(Instance, EventArgs.Empty);
        }

        public event EventHandler<RecordingStatusChangedEventArgs> RecordingStatusChanged;
        //static public void RecordingStatusChanged(RecordingStatusChangedEventArgs rArg)
        //{
        //    if (Instance != null && Instance.RecordingStatusChanged != null)
        //    {
        //        Instance.RecordingStatusChanged(Instance, null);
        //    }
        //}
        #endregion

        #region tuners & status
        public async Task<LiveTvServiceStatusInfo> GetStatusInfoAsync(CancellationToken cancellationToken)
        {
            bool serverDown = await isServerDown(cancellationToken);
            bool isRecFolderFound = false;
            string recTvPath = null;

            string status = serverDown ? "ServerWMC not connected" : string.Format("ServerWMC({0}) is Up", _serverWMC_version);

            if (!serverDown)    // if server is not down, check the server version again
            {
                string[] responses = await SocketClientAsync.GetVectorAsync("GetServerVersion", cancellationToken);
                _serverWMC_version = responses[0];              // get version string
                _serverWMC_build = int.Parse(responses[1]);     // get build number
                if (responses.Length > 2)
                {
                    recTvPath = responses[2];                   // store the recording tv path if we got one
                    _recTVPath = recTvPath;
                }
            }

            // check if the REQUIRED verson of swmc is not running
            if (!serverDown && !IsServerVersionOK)
                status = "This plugin requires the newest version of ServerWMC, please update";

            #region get the tuner status
            var tunerInfoList = new List<LiveTvTunerInfo>();
            try
            {
                if (!serverDown && IsServerVersionOK)
                {
                    string[] responses = await SocketClientAsync.GetVectorAsync("GetTunerStatus", cancellationToken);

                    if (!IsServerError(responses))
                    {
                        foreach (string response in responses)
                        {
                            var v = response.Split('|');				// split to unpack string

                            LiveTvTunerInfo tInfo = new LiveTvTunerInfo();
                            tInfo.Name = v[0];
                            tInfo.Id = v[1];
                            tInfo.SourceType = v[2];
                            tInfo.Status = (LiveTvTunerStatus)Enum.Parse(typeof(LiveTvTunerStatus), v[3]);
                            tInfo.ProgramName = v[4];
                            tInfo.RecordingId = v[5];
                            tInfo.ChannelId = v[6];
                            tInfo.Clients = v[7].Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();

                            tunerInfoList.Add(tInfo);

                            //if (tInfo.Status == LiveTvTunerStatus.LiveTv)  // test stream record
                            //    await RecordLiveStream((_idStreamInt-1).ToString(), CancellationToken.None);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("GetStatusInfoAsync::TunerStatus> " + ex.Message);
                throw ex;
            }
            #endregion

            // if we received a path to the recTV folder from the server, check it here to make sure its accessible
            // recTvPath will be empty string if urls are being used
            if (!string.IsNullOrEmpty(recTvPath))
            {
                if (Directory.Exists(TVPath(recTvPath)))
                    isRecFolderFound = true;
                else
                {
                    if (recTvPath.StartsWith(@"\\"))
                        status = string.Format("Recorded TV folder ‘{0}’ not accessible.  Please make sure the Recorded TV folder used by WMC for new recordings is a shared folder.  Once done, the network path to this folder must be set in ServerWMC (see ‘Folders’ tab in ServerWMC).", recTvPath);
                    else if (Environment.OSVersion.Platform == PlatformID.Unix)
                        if (string.IsNullOrEmpty(_linuxPath))
                            status = string.Format("Linux path to Recorded TV folder is not set in configuration.");
                        else
                            status = string.Format("Linux path to Recorded TV folder ‘{0}’ failed.", _linuxPath);
                    else
                        status = string.Format("Recorded TV folder ‘{0}’ not accessible", recTvPath);
                }
            }
            else if (recTvPath == "")       // if recTvPath was returned empty, we are return url addresses, so there is no rec tv folder to check
            {
                isRecFolderFound = true;    // folder does not effect status
            }

            return new LiveTvServiceStatusInfo
            {
                StatusMessage = status,                     // status error is only shown in mbs if 'Unavailable' is set below
                Version = _serverWMC_version,   
                HasUpdateAvailable = !IsServerUpTodate,     // true if an update is available
                Status = (serverDown || !IsServerVersionOK || !isRecFolderFound) ? LiveTvServiceStatus.Unavailable : LiveTvServiceStatus.Ok,
                Tuners = tunerInfoList
            };
        }

        public async Task ResetTuner(string id, CancellationToken cancellationToken)
        {
            string request = XferString("ResetTunerByDeviceId", id);
            string[] responses = await SocketClientAsync.GetVectorAsync(request, cancellationToken);
        }

        #endregion // tuners



        //public async Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
        //{
        //    throw new NotImplementedException();
        //}

        public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        {
            if (await isServerDown(cancellationToken))
                throw new Exception("ServerWMC is down");

            if (_serverWMC_build < 1191)                       // serverversion after 1190 supports transcode query
                throw new NotImplementedException();

            // get transcode profile names that are available for this channel for the tuner device, for now only si-dust profiles are possible
            string[] responses = await SocketClientAsync.GetVectorAsync(XferString("GetTranscodeProfiles", channelId), cancellationToken);

            if (responses.Length > 0)
            {
                var list = new List<MediaSourceInfo>();

                foreach (string profileName in responses)           // for all the profile names returned get their MediaSouceInfo
                {
                    list.Add(GetMediaSource(profileName));
                }
                return list;
            }

            throw new NotImplementedException();
        }


        public async Task<List<MediaSourceInfo>> GetRecordingStreamMediaSources(string recordingId, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        // based on: https://github.com/MediaBrowser/Emby/blob/dev/MediaBrowser.Server.Implementations/LiveTv/TunerHosts/HdHomerun/HdHomerunHost.cs#L229-L371
        private MediaSourceInfo GetMediaSource(string profile)
        {
            int? width = null;
            int? height = null;
            bool isInterlaced = true;
            var videoCodec = "mpeg2video";
            int? videoBitrate = null;

            if (string.Equals(profile, "mobile", StringComparison.OrdinalIgnoreCase))
            {
                width = 1280;
                height = 720;
                isInterlaced = false;
                videoCodec = "h264";
                videoBitrate = 2000000;
            }
            else if (string.Equals(profile, "heavy", StringComparison.OrdinalIgnoreCase))
            {
                width = 1920;
                height = 1080;
                isInterlaced = false;
                videoCodec = "h264";
                videoBitrate = 8000000;
            }
            else if (string.Equals(profile, "internet720", StringComparison.OrdinalIgnoreCase))
            {
                width = 1280;
                height = 720;
                isInterlaced = false;
                videoCodec = "h264";
                videoBitrate = 5000000;
            }
            else if (string.Equals(profile, "internet540", StringComparison.OrdinalIgnoreCase))
            {
                width = 1280;
                height = 720;
                isInterlaced = false;
                videoCodec = "h264";
                videoBitrate = 2500000;
            }
            else if (string.Equals(profile, "internet480", StringComparison.OrdinalIgnoreCase))
            {
                width = 848;
                height = 480;
                isInterlaced = false;
                videoCodec = "h264";
                videoBitrate = 2000000;
            }
            else if (string.Equals(profile, "internet360", StringComparison.OrdinalIgnoreCase))
            {
                width = 640;
                height = 360;
                isInterlaced = false;
                videoCodec = "h264";
                videoBitrate = 1500000;
            }
            else if (string.Equals(profile, "internet240", StringComparison.OrdinalIgnoreCase))
            {
                width = 432;
                height = 240;
                isInterlaced = false;
                videoCodec = "h264";
                videoBitrate = 1000000;
            }

            //var url = GetApiUrl(info, true) + "/auto/v" + channelId;

            //if (!string.IsNullOrWhiteSpace(profile) && !string.Equals(profile, "native", StringComparison.OrdinalIgnoreCase))
            //{
            //    url += "?transcode=" + profile;
            //}

            var mediaSource = new MediaSourceInfo
            {
                //Path = url,
                Protocol = MediaProtocol.Http,
                MediaStreams = new List<MediaStream>
                        {
                            new MediaStream
                            {
                                Type = MediaStreamType.Video,
                                // Set the index to -1 because we don't know the exact index of the video stream within the container
                                Index = -1,
                                IsInterlaced = isInterlaced,
                                Codec = videoCodec,
                                Width = width,
                                Height = height,
                                BitRate = videoBitrate
                                
                            },
                            new MediaStream
                            {
                                Type = MediaStreamType.Audio,
                                // Set the index to -1 because we don't know the exact index of the audio stream within the container
                                Index = -1,
                                Codec = "ac3",
                                BitRate = 128000
                            }
                        },
                RequiresOpening = true,     // important and different than original version
                RequiresClosing = true,     // important and different than original version
                BufferMs = 1000,
                Container = "ts",
                Id = profile
            };

            return mediaSource;
        }
    }

    #region enums

    // set this enum to match swmc's ProgramType enum
    public enum ProgramType
    {
        none = 0,
        IsRepeat = 1,
        IsSeries = 2,
        IsHD = 4,
        IsMovie = 8,
        IsSports = 16,
        IsKids = 32,
        IsLive = 64,
        IsNews = 128,
        IsPremiere = 256,
        IsFinale = 512,
        Is3D = 1024,                    
        IsEducational = 2048,
        IsSubjectToBlackout = 4096
    }

    // timer status from xbmc
    enum PVR_TIMER_STATE
    {
        PVR_TIMER_STATE_NEW = 0, /*!< @brief a new, unsaved timer */
        PVR_TIMER_STATE_SCHEDULED = 1, /*!< @brief the timer is scheduled for recording */
        PVR_TIMER_STATE_RECORDING = 2, /*!< @brief the timer is currently recordings */
        PVR_TIMER_STATE_COMPLETED = 3, /*!< @brief the recording completed successfully */
        PVR_TIMER_STATE_ABORTED = 4, /*!< @brief recording started, but was aborted */
        PVR_TIMER_STATE_CANCELLED = 5, /*!< @brief the timer was scheduled, but was canceled */
        PVR_TIMER_STATE_CONFLICT_OK = 6, /*!< @brief the scheduled timer conflicts with another one, but will be recorded */
        PVR_TIMER_STATE_CONFLICT_NOK = 7, /*!< @brief the scheduled timer conflicts with another one and won't be recorded */
        PVR_TIMER_STATE_ERROR = 8  /*!< @brief the timer is scheduled, but can't be recorded for some reason */
    }

    // recording status from wmc
    enum RecordingState_WMC
    {
        None = 0,
        Scheduled = 1,
        Initializing = 2,
        Recording = 3,
        Recorded = 4,
        Deleted = 5,
    }

    // days of week from wmc
    public enum DaysOfWeek
    {
        None = 0,
        Sunday = 1,
        Monday = 2,
        Tuesday = 4,
        Wednesday = 8,
        Thursday = 16,
        Friday = 32,
        Saturday = 64,
        All = 127,
    }

    public enum TVRatingWMC
    {
        Unknown = 0,
        UsaY = 1,
        UsaY7 = 2,
        UsaG = 3,
        UsaPG = 4,
        UsaTV14 = 5,
        UsaMA = 6,

        DeAll = 7,
        De6 = 8,
        De12 = 9,
        De16 = 10,
        DeAdults = 11,

        FrAll = 12,
        Fr10 = 13,
        Fr12 = 14,
        Fr16 = 15,
        Fr18 = 16,

        KrAll = 17,
        Kr7 = 18,
        Kr12 = 19,
        Kr15 = 20,
        Kr19 = 21,

        GB_UC = 22,
        GbU = 23,
        GbPG = 24,
        Gb12 = 25,
        Gb15 = 26,
        Gb18 = 27,
        GbR18 = 28,

    }


#endregion
}
