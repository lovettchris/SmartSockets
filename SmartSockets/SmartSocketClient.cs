﻿// ------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LovettSoftware.SmartSockets
{
    /// <summary>
    /// This class wraps the Socket class providing some useful semantics like FindServerAsync which looks
    /// for the UDP message broadcast by the SmartSocketServer.  It also provides a useful SendAsync
    /// message that synchronously waits for a response from the server so that it is always clear
    /// which direction the traffic is flowing.  It also supports serializing custom message objects via
    /// the DataContractSerializer using known types provided in the SmartSocketTypeResolver.
    /// </summary>
    public class SmartSocketClient : IDisposable
    {
        private readonly Socket client;
        private readonly NetworkStream stream;
        private readonly SmartSocketServer server;
        private bool closed;
        private readonly SmartSocketTypeResolver resolver;
        private readonly DataContractSerializer serializer;

        public const string DisconnectMessageId = "DisconnectMessageId.3d9cd318-fcae-4a4f-ae63-34907be2700a";
        public const string ConnectedMessageId = "ConnectedMessageId.822280ed-26f5-4cdd-b45c-412e05d1005a";
        public const string MessageAck = "MessageAck.822280ed-26f5-4cdd-b45c-412e05d1005a";
        public const string ErrorMessageId = "ErrorMessageId.385ff3c1-84d8-491a-a8b3-e2a9e8f0e256";
        public const string OpenBackChannelMessageId = "OpenBackChannel.bd89da83-95c8-42e7-bf4e-6e7d0168754a";

        internal SmartSocketClient(SmartSocketServer server, Socket client, SmartSocketTypeResolver resolver)
        {
            this.client = client;
            this.stream = new NetworkStream(client);
            this.server = server;
            this.resolver = resolver;
            client.NoDelay = true;

            DataContractSerializerSettings settings = new DataContractSerializerSettings();
            settings.DataContractResolver = this.resolver;
            settings.PreserveObjectReferences = true;
            this.serializer = new DataContractSerializer(typeof(MessageWrapper), settings);
        }

        internal Socket Socket => this.client;

        public string Name { get; set; }

        /// <summary>
        /// Find a SmartSocketServer on the local network using UDP broadcast.  This will block
        /// waiting for a server to respond or until you cancel using the CancellationToken.
        /// </summary>
        /// <returns>The connected client or null if task is cancelled.</returns>
        public static async Task<SmartSocketClient> FindServerAsync(string serviceName, string clientName, SmartSocketTypeResolver resolver, CancellationToken token,
                                                                    string udpGroupAddress = "226.10.10.2", int udpGroupPort = 37992)
        {
            return await Task.Run(async () =>
            {
                string localHost = FindLocalHostName();
                if (localHost == null)
                {
                    return null;
                }
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var groupAddr = IPAddress.Parse(udpGroupAddress);
                        IPEndPoint remoteEP = new IPEndPoint(groupAddr, udpGroupPort);
                        UdpClient udpClient = new UdpClient(0);
                        MemoryStream ms = new MemoryStream();
                        BinaryWriter writer = new BinaryWriter(ms);
                        writer.Write(serviceName.Length);
                        writer.Write(serviceName);
                        byte[] bytes = ms.ToArray();
                        udpClient.Send(bytes, bytes.Length, remoteEP);

                        CancellationTokenSource receiveTaskSource = new CancellationTokenSource();
                        Task<UdpReceiveResult> receiveTask = udpClient.ReceiveAsync();
                        if (receiveTask.Wait(5000, receiveTaskSource.Token))
                        {
                            UdpReceiveResult result = receiveTask.Result;
                            IPEndPoint serverEP = result.RemoteEndPoint;
                            byte[] buffer = result.Buffer;
                            BinaryReader reader = new BinaryReader(new MemoryStream(buffer));
                            int len = reader.ReadInt32();
                            string addr = reader.ReadString();
                            string[] parts = addr.Split(':');
                            if (parts.Length == 2)
                            {
                                var a = IPAddress.Parse(parts[0]);
                                SmartSocketClient client = await ConnectAsync(new IPEndPoint(a, int.Parse(parts[1])), clientName, resolver);
                                if (client != null)
                                {
                                    client.ServerName = serviceName;
                                    client.Name = localHost;
                                    return client;
                                }
                            }
                        }
                        else
                        {
                            receiveTaskSource.Cancel();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Something went wrong with Udp connection: " + ex.Message);
                    }
                }
                return null;
            });
        }

        /// <summary>
        /// Create another socket that will allow the server to send messages to us any time.
        /// It is expected you will start a ReceiveAsync loop on this server object to process
        /// those messages.
        /// </summary>
        /// <param name="connectedHandler">An event handler to invoke when the server opens the back channel</param>
        /// <returns></returns>
        public async Task<SmartSocketServer> OpenBackChannel(EventHandler<SmartSocketClient> connectedHandler)
        {
            IPEndPoint ipe = (IPEndPoint)this.Socket.LocalEndPoint;
            // start a new server that does not use UDP.
            var server = SmartSocketServer.StartServer(this.Name, this.resolver, ipe.Address.ToString(), null, 0);
            server.ClientConnected += connectedHandler;
            int port = server.Port;
            // tell the server we've opened another channel and pass the "port" number
            var response = await this.SendReceiveAsync(new SocketMessage(OpenBackChannelMessageId, this.Name + ":" + port));
            if (response.Id == ErrorMessageId)
            {
                throw new InvalidOperationException(response.Message);
            }
            return server;
        }

        internal static async Task<SmartSocketClient> ConnectAsync(IPEndPoint serverEP, string clientName, SmartSocketTypeResolver resolver)
        {
            Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            bool connected = false;
            CancellationTokenSource src = new CancellationTokenSource();
            try
            {
                Task task = Task.Run(() =>
                {
                    try
                    {
                        client.Connect(serverEP);
                        connected = true;
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine("Connect exception: " + e.Message);
                    }
                }, src.Token);

                // give it 30 seconds to connect...
                if (!task.Wait(60000))
                {
                    src.Cancel();
                }
            }
            catch (TaskCanceledException)
            {
                // move on...
            }

            if (connected)
            {
                var result = new SmartSocketClient(null, client, resolver)
                {
                    Name = clientName,
                    ServerName = GetHostName(serverEP.Address)
                };
                SocketMessage response = await result.SendReceiveAsync(new SocketMessage(ConnectedMessageId, clientName));

                return result;
            }

            return null;
        }

        private static string GetHostName(IPAddress addr)
        {
            try
            {
                var entry = Dns.GetHostEntry(addr);
                if (!string.IsNullOrEmpty(entry.HostName))
                {
                    return entry.HostName;
                }
            }
            catch (Exception)
            {
                // this can fail if machines are in different domains.
            }

            return addr.ToString();
        }

        internal static string FindLocalHostName()
        {
            try
            {
                IPHostEntry e = Dns.GetHostEntry(IPAddress.Loopback);
                return e.HostName;
            }
            catch (Exception)
            {
                // ignore failures to do with DNS lookups
            }

            return null;
        }

        internal static List<string> FindLocalIpAddresses()
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    ni.SupportsMulticast && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                     && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                {
                    var props = ni.GetIPProperties();
                    if (props.IsDnsEnabled || props.IsDynamicDnsEnabled)
                    {
                        IPHostEntry e = Dns.GetHostEntry(IPAddress.Loopback);
                        List<string> ipAddresses = new List<string>();
                        foreach (var addr in e.AddressList)
                        {
                            ipAddresses.Add(addr.ToString());
                        }

                        return ipAddresses;
                    }
                }
            }

            return null;
        }

        public string ServerName { get; set; }

        public bool IsConnected => !this.closed;

        /// <summary>
        /// If OpenBackChannel is called, and the server supports it then this property will
        /// be defined when that channel is connected.
        /// </summary>
        public SmartSocketClient BackChannel { get; internal set; }

        /// <summary>
        /// This event is raised if a socket error is detected.
        /// </summary>
        public event EventHandler<Exception> Error;

        /// <summary>
        /// This even is raised if the socket is disconnected.
        /// </summary>
        public event EventHandler Disconnected;

        internal async void Close()
        {
            if (this.closed)
            {
                return;
            }
            try
            {
                await this.SendReceiveAsync(new SocketMessage(DisconnectMessageId, this.Name));

                using (this.client)
                {
                    this.client.Close();
                }

                this.closed = true;
            }
            catch (Exception)
            {
                // ignore failures on close.
            }
        }

        private void OnError(Exception ex)
        {
            Exception inner = ex;
            while (inner != null)
            {
                SocketException se = inner as SocketException;
                if (se != null && se.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // we're toast!
                    if (this.server != null)
                    {
                        this.server.RemoveClient(this);
                    }

                    this.closed = true;
                }

                if (ex is ObjectDisposedException)
                {
                    this.closed = true;
                }

                inner = inner.InnerException;
            }

            if (this.Error != null)
            {
                this.Error(this, ex);
            }
        }

        [DataContract]
        internal class MessageWrapper
        {
            [DataMember]
            public object Message { get; set; }
        }

        /// <summary>
        /// Send a message back to the client.
        /// </summary>
        /// <returns>The response message</returns>
        public async Task<SocketMessage> SendReceiveAsync(SocketMessage msg)
        {
            if (this.closed)
            {
                throw new SocketException((int)SocketError.NotConnected);
            }

            // must serialize this send/response sequence, cannot interleave them!
            using (await this.GetSendLock())
            {

                // get the buffer containing the serialized message.
                return await Task.Run(async () =>
                {
                    // Begin sending the data to the remote device.
                    try
                    {
                        await this.InternalSendAsync(msg);

                        SocketMessage response = await this.InternalReceiveAsync();
                        return response;
                    }
                    catch (Exception ex)
                    {
                        // is the socket dead?
                        this.OnError(ex);
                    }
                    return null;
                });
            }
        }

        /// <summary>
        /// Send a message to the client and do not wait for a response.
        /// </summary>
        /// <returns>The response message</returns>
        public async Task SendAsync(SocketMessage msg)
        {
            // must serialize this send/response sequence, cannot interleave them!
            using (await this.GetSendLock())
            {
                await this.InternalSendAsync(msg);
            }
        }

        public async Task InternalSendAsync(SocketMessage msg)
        {
            if (this.closed)
            {
                throw new SocketException((int)SocketError.NotConnected);
            }

            // get the buffer containing the serialized message.
            await Task.Run(() =>
            {
                // Begin sending the data to the remote device.
                try
                {
                    MemoryStream ms = new MemoryStream();
                    this.serializer.WriteObject(ms, new MessageWrapper() { Message = msg });

                    byte[] buffer = ms.ToArray();

                    BinaryWriter streamWriter = new BinaryWriter(this.stream, Encoding.UTF8, true);
                    streamWriter.Write(buffer.Length);
                    streamWriter.Write(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    // is the socket dead?
                    this.OnError(ex);
                }
            });
        }

        private void OnClosed()
        {
            this.closed = true;
            if (this.Disconnected != null)
            {
                this.Disconnected(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Receive one message from the socket.  This call blocks until a message has arrived.
        /// </summary>
        public async Task<SocketMessage> ReceiveAsync()
        {
            using (await this.GetSendLock())
            {
                return await InternalReceiveAsync();
            }
        }

        private async Task<SocketMessage> InternalReceiveAsync()
        {
            if (this.closed)
            {
                throw new SocketException((int)SocketError.NotConnected);
            }
            SocketMessage msg = null;
            try
            {
                using (BinaryReader streamReader = new BinaryReader(this.stream, Encoding.UTF8, true))
                {
                    int len = streamReader.ReadInt32();
                    byte[] block = streamReader.ReadBytes(len);

                    object result = this.serializer.ReadObject(new MemoryStream(block));
                    var wrapper = result as MessageWrapper;
                    if (wrapper != null && wrapper.Message is SocketMessage)
                    {
                        msg = (SocketMessage)wrapper.Message;
                        if (msg.Id == DisconnectMessageId)
                        {
                            // client is politely saying good bye...
                            this.OnClosed();
                        }
                        else if (msg.Id == ConnectedMessageId)
                        {
                            this.Name = msg.Sender;
                            await this.SendAsync(new SocketMessage(MessageAck, this.Name));
                        }
                        else if (msg.Id == OpenBackChannelMessageId && this.server != null)
                        {
                            // client is requesting a back channel.
                            await HandleBackchannelRequest(msg);
                        }
                    }
                }
            }
            catch (EndOfStreamException eos)
            {
                this.OnError(eos);
            }
            catch (System.IO.IOException ioe)
            {
                System.Net.Sockets.SocketException se = ioe.InnerException as System.Net.Sockets.SocketException;
                if (se.SocketErrorCode == SocketError.ConnectionReset)
                {
                    this.OnClosed();
                }
            }
            catch (Exception ex)
            {
                this.OnError(ex);
            }        

            return msg;
        }

        private async Task HandleBackchannelRequest(SocketMessage msg)
        {
            string[] parts = msg.Sender.Split(':');
            if (parts.Length == 2)
            {
                int port = 0;
                if (int.TryParse(parts[1], out port))
                {
                    bool rc = await this.server.OpenBackChannel(this, port);
                    if (rc)
                    {
                        await this.SendAsync(new SocketMessage(MessageAck, this.Name));
                        return;
                    }
                    else
                    {
                        await this.SendAsync(new SocketMessage(ErrorMessageId, this.Name)
                        {
                            Message = "Server is not expecting a back channel"
                        });
                        return;
                    }
                }
            }
            await this.SendAsync(new SocketMessage(ErrorMessageId, this.Name)
            {
                Message = "Valid port number was not found in backchannel message"
            });
        }

        public void Dispose()
        {
            this.Close();
            GC.SuppressFinalize(this);
        }

        ~SmartSocketClient()
        {
            this.Close();
        }

        private readonly SendLock Lock = new SendLock();

        private async Task<IDisposable> GetSendLock()
        {
            while (this.Lock.Locked)
            {
                await Task.Delay(100);
                lock (this.Lock)
                {
                    if (!this.Lock.Locked)
                    {
                        this.Lock.Locked = true;
                        return new ReleaseLock(this.Lock);
                    }
                }
            }

            return null;
        }

        internal class SendLock
        {
            public bool Locked { get; set; }
        }

        internal class ReleaseLock : IDisposable
        {
            private readonly SendLock Lock;

            public ReleaseLock(SendLock sendLock)
            {
                this.Lock = sendLock;
            }

            public void Dispose()
            {
                lock (this.Lock)
                {
                    this.Lock.Locked = false;
                }
            }

        }
    }
}