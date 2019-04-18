using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace wmc2mb
{
    class SocketClient
    {
        byte[] _buffer;
        string[] _stringSeparators;
        string _machineName;

        private SemaphoreSlim _socketSemaphore = new SemaphoreSlim(1, 1);

        public SocketClient()
        {
            _buffer = new byte[4096];
            _stringSeparators = new string[] { "<EOL>" };
            _machineName = Environment.MachineName;
        }

        public static void TestConnection(string serverIp, int port)
        {
            SocketClient testSocket = new SocketClient();
            string[] responses = testSocket.GetVector(serverIp, port, "GetTestInt", "");
            if (responses == null)
                throw new Exception("ServerWMC connection failed");
        }

        // todo: switch to real asym socket client
        public async Task<string[]> GetVectorAsync(string command, CancellationToken cancellationToken, string streamId = "")
        {
            await _socketSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return GetVector(command, streamId);
            }
            finally
            {
                _socketSemaphore.Release();
            }
            //var t1 = await Task.Run(() => GetVector(command, streamId));
            //return t1;
        }

        public string[] GetVector(string command, string streamId = "")
        {
            return GetVector(   Plugin.Instance.Configuration.ServerIP, Plugin.Instance.Configuration.ServerPort,
                                command, streamId);
        }

        private string[] GetVector(string serverIp, int port, string command, string streamId)
        {
            try
            {
                // Establish the remote endpoint for the socket.
                IPAddress ipAddr = Dns.GetHostAddresses(serverIp)[0];
                IPEndPoint remoteEP = new IPEndPoint(ipAddr, port);

                // Create a TCP/IP  socket.
                Socket sender = new Socket(ipAddr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                sender.Connect(remoteEP);                           // connect to server

                StringBuilder bigStr = new StringBuilder();

                // build the request string
                command = string.Format("MediaBrowser^@{1}@{2}|{0}<Client Quit>", command, _machineName, streamId);	    

                byte[] msg = Encoding.ASCII.GetBytes(command);      // Encode the server command


                int bytesSent = sender.Send(msg);                   // Send the data through the socket.

                int bytesRec = sender.Receive(_buffer);             // Receive the first xfer from the server

                while (bytesRec > 0)    // keep transferring and accumlating
                {
                    bigStr.Append(Encoding.UTF8.GetString(_buffer, 0, bytesRec).ToCharArray());
                    bytesRec = sender.Receive(_buffer);
                }

                // Release the socket.
                sender.Shutdown(SocketShutdown.Both);
                sender.Close();

                // convert input string to results array
                List<string> results = bigStr.ToString().Split(_stringSeparators, StringSplitOptions.None).ToList();

                if (results.Last() != "<EOF>")
                    throw new Exception("Error: ServerWMC response is incomplete");
                else
                    results.RemoveAt(results.Count - 1);        // remove EOF

                return results.ToArray();
            }
            catch (Exception e)
            {
                Console.WriteLine("SocketClient Error> " + e.ToString());
                throw e;                            // let mbs core handle error
            }
        }
        
        string GetString(string request)
        {
	        var result = this.GetVector(request);
            if (result != null)
	            return result[0];
            return null;
        }

        public bool GetBool(string request)
        {
	        return GetString(request) == "True";
        }

    }
}
