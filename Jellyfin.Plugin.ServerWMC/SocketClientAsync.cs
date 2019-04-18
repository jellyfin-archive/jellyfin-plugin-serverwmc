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
    public class SocketClientAsync
    {
        static string[] _stringSeparators;
        static string _machineName;
        static private SemaphoreSlim _socketSemaphore = new SemaphoreSlim(1, 1);

        static IPAddress _ipAddr = null;
        static IPEndPoint _remoteEP = null;
        static byte[] _buffer;                  // the receive buffer
        
        static SocketClientAsync()
        {
            _stringSeparators = new string[] { "<EOL>" };
            _machineName = Environment.MachineName;
            _buffer = new byte[4096]; 
        }

        /// <summary>
        /// set server ip address and port
        /// </summary>
        public static void InitAddress(string serverIp, int port)
        {
            _ipAddr = Dns.GetHostAddresses(serverIp)[0];
            _remoteEP = new IPEndPoint(_ipAddr, port);
        }

        public static IPAddress IpAddr
        {
            get { return _ipAddr; }
        }

        /// <summary>
        /// get results from socket server using TPL
        /// </summary>
        /// <param name="command">command to send to server</param>
        /// <param name="streamId">stream Id to embed on command</param>
        /// <param name="cancelToken">for canceling asyc operation</param>
        /// <returns>array of string reponses from server</returns>
        public static async Task<string[]> GetVectorAsync(string command, CancellationToken cancelToken, string streamId = "")
        {
            Socket socket = null;
            SocketClientAsync asyncS = new SocketClientAsync();

            await _socketSemaphore.WaitAsync(cancelToken).ConfigureAwait(false);    // one at a time query of server

            try
            {
                // Create a TCP/IP socket. 
                socket = new Socket(_ipAddr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                // Connect to the remote endpoint
                await Task.Factory.FromAsync(socket.BeginConnect(_remoteEP, null, socket), socket.EndConnect);

                // build the request string
                string sendCommand = string.Format("MediaBrowser^@{1}@{2}|{0}<Client Quit>", command, _machineName, streamId);
                byte[] bytesToSend = Encoding.UTF8.GetBytes(sendCommand);

                // send the command string to server
                int bytesSent = await Task<int>.Factory.FromAsync(socket.BeginSend(bytesToSend, 0, bytesToSend.Length, 0, null, socket), socket.EndSend);
                //Debug.WriteLine("Sent {0} bytes to server.", bytesSent);

                // use this to accumulate server responses
                StringBuilder sb = new StringBuilder();

                // keep getting results from server until zero bytes are read
                int bytesRead;
                while ((bytesRead = await Task<int>.Factory.FromAsync(socket.BeginReceive(_buffer, 0, _buffer.Length, 0, null, socket), socket.EndReceive)) > 0)
                {
                    sb.Append(Encoding.UTF8.GetString(_buffer, 0, bytesRead));      // accumulate results in sb
                    //Debug.WriteLine("Bytes read: " + bytesRead);
                }

                // convert input string to results array
                string bigStr = sb.ToString();
                List<string> results = bigStr.Split(_stringSeparators, StringSplitOptions.None).ToList();

                // check/clean up results
                if (results.Last() == "<EOF>")
                    results.RemoveAt(results.Count - 1);                            // remove EOF
                else
                    throw new Exception("Error> ServerWMC response is incomplete"); // EOF not found

                // Release the socket.
                socket.Shutdown(SocketShutdown.Both);
                socket.Close();

                return results.ToArray();
            }
            catch (Exception e)
            {
                //Debug.WriteLine(e.ToString());
                throw e;                            // rethrow to let mbs handle
            }
            finally
            {
                _socketSemaphore.Release();
            }
        }

        /// <summary>
        /// synchronis version of GetVectorAsync
        /// </summary>
        public static string[] GetVector(string command)
        {
            return GetVector(command, "");
        }

        /// <summary>
        /// synchronis version of GetVectorAsync
        /// </summary>
        public static string[] GetVector(string command, string streamId)
        {
            Task<string[]> task = Task.Run(() => GetVectorAsync(command, CancellationToken.None, streamId));

            // Will block until the task is completed...
            return task.Result;
        }


        static string GetString(string request)
        {
            var result = GetVector(request);
            if (result != null)
                return result[0];
            return null;
        }

        static public bool GetBool(string request)
        {
            return GetString(request) == "True";
        }

        /// <summary>
        /// attempt to change the ip and port, if exception is thrown restore old values
        /// </summary>
        public static void ChangeAddress(string serverIP, int port)
        {
            IPAddress ipAddr = _ipAddr;         // save current values
            IPEndPoint remoteEP = _remoteEP;
            try
            {
                InitAddress(serverIP, port);
                string[] responses = GetVector("GetTestInt", "");
                //if (responses == null)   - just let throw error fail the method
                //    throw new Exception("ServerWMC connection failed");
            }
            catch  (Exception ex)
            {
                _ipAddr = ipAddr;               // restore values if connection attempt through an exception
                _remoteEP = remoteEP;
                throw ex;
            }
        }
    }
        
}
