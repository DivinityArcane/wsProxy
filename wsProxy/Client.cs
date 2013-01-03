using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace wsProxy
{
    public class Client
    {
        private TcpClient _local_client = null;
        private TcpClient _remote_client = null;
        private byte[] _local_buffer = new byte[8192];
        private byte[] _remote_buffer = new byte[8192];
        private String _overflow = String.Empty;
        private Queue<byte[]> _packet_queue = new Queue<byte[]>();
        private ManualResetEvent wait_event = new ManualResetEvent(true);
        private bool _can_run = true;

        private class WSHandshake
        {
            public bool Complete            = false;
            public bool ComputeKey          = false;
            public const String _magic      = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

            public String Key               = String.Empty;
            public String Secret            = String.Empty;
            public String Origin            = String.Empty;
            public byte Version             = 0;
        }

        private WSHandshake Handshake = new WSHandshake();

        public Client(TcpClient local, TcpClient remote, String host, int port)
        {
            _local_client   = local;
            _remote_client  = remote;

            _remote_client.BeginConnect(host, port, OnConnect, null);

            new Thread(new ThreadStart(ProcessQueue)).Start();
        }

        private void ProcessQueue()
        {
            while (_can_run)
            {
                wait_event.WaitOne();

                lock (_packet_queue)
                {
                    while (_packet_queue.Count > 0)
                    {
                        byte[] payload = _packet_queue.Dequeue();

                        SendLocal(payload);
                    }
                }

                wait_event.Reset();
            }
        }

        private void OnConnect(IAsyncResult result)
        {
            ConIO.Write("Connected to remote host: " + EndPoint(false), EndPoint());

            _remote_client.Client.BeginReceive(_remote_buffer, 0, 8192, SocketFlags.None, OnRemoteReceive, result);
            _local_client.Client.BeginReceive(_local_buffer, 0, 8192, SocketFlags.None, OnLocalReceive, result);
        }

        private void OnLocalReceive(IAsyncResult result)
        {
            try
            {
                int recv_len = _local_client.Client.EndReceive(result);

                if (recv_len > 0)
                {
                    byte[] data = new byte[recv_len];
                    Buffer.BlockCopy(_local_buffer, 0, data, 0, recv_len);

                    ConIO.Write("Got data from client to server.", EndPoint());

                    if (!Handshake.Complete)
                    {
                        String payload = Encoding.ASCII.GetString(data);

                        if (payload.StartsWith("GET") && payload.Contains("\r\n"))
                        {
                            String[] lines = payload.Split(new char[2] { '\r', '\n' });

                            foreach (String line in lines)
                            {
                                if (line.StartsWith("Origin: "))
                                    Handshake.Origin = line.Substring(8);
                                else if (line.StartsWith("Sec-WebSocket-Version: "))
                                    Handshake.Version = Convert.ToByte(line.Substring(24));
                                else if (line.StartsWith("Sec-WebSocket-Key: "))
                                {
                                    Handshake.Key = line.Substring(19);

                                    Handshake.ComputeKey = true;
                                }
                            }

                            if (Handshake.ComputeKey)
                            {
                                SHA1CryptoServiceProvider SHA1C = new SHA1CryptoServiceProvider();
                                byte[] _payload = SHA1C.ComputeHash(Encoding.ASCII.GetBytes(Handshake.Key + WSHandshake._magic));

                                Handshake.Secret = Convert.ToBase64String(_payload);

                                String packet = String.Format("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {0}\r\n\r\n", Handshake.Secret);

                                SendLocal(Encoding.ASCII.GetBytes(packet));

                                Handshake.Complete = true;
                            }
                        }
                    }
                    else
                    {
                        // Time to convert the packet from WebSocket format to regular data.
                        byte[] encoded = new byte[data.Length - 2];
                        Buffer.BlockCopy(data, 1, encoded, 0, data.Length - 2);

                        int len = data[1] & 127;
                        int pos = 0;

                        if (len == 126)
                            pos = 4;
                        else if (len == 127)
                            pos = 10;
                        else
                            pos = 2;

                        byte[] mask = new byte[4];
                        Buffer.BlockCopy(data, pos, mask, 0, 4);
                        pos += 4;

                        byte[] payload = new byte[data.Length - pos];

                        for (int i = pos, j = 0; i < data.Length; i++, j++)
                            payload[i - pos] = (byte)(data[i] ^ mask[j % 4]);

                        String packet = Uri.UnescapeDataString(Encoding.ASCII.GetString(payload)) + "\0";

                        // If you want to see the packets, uncomment this line.
                        //ConIO.Write(packet);

                        SendRemote(Encoding.ASCII.GetBytes(packet));
                    }

                    _local_buffer = new byte[8192];
                    _local_client.Client.BeginReceive(_local_buffer, 0, 8192, SocketFlags.None, OnLocalReceive, result);
                }
            }
            catch
            {
                ConIO.Warning("Client.OnLocalReceive", "Got exception. Dead client?");
                _remote_client.Close();
                _local_client.Close();
                _can_run = false;
            }
        }

        private void OnRemoteReceive(IAsyncResult result)
        {
            try
            {
                int recv_len = _remote_client.Client.EndReceive(result);

                if (recv_len > 0)
                {
                    byte[] data = new byte[recv_len];
                    Buffer.BlockCopy(_remote_buffer, 0, data, 0, recv_len);

                    ConIO.Write("Got data from server to client.", EndPoint());

                    String packet = Encoding.ASCII.GetString(data);

                    ProcessRemotePacket(packet);

                    _remote_buffer = new byte[8192];
                    _remote_client.Client.BeginReceive(_remote_buffer, 0, 8192, SocketFlags.None, OnRemoteReceive, result);
                }
            }
            catch
            {
                ConIO.Warning("Client.OnRemoteReceive", "Got exception. Dead client?");
                _remote_client.Close();
                _local_client.Close();
                _can_run = false;
            }
        }

        private void ProcessRemotePacket(String packet)
        {
            if (!String.IsNullOrWhiteSpace(_overflow))
            {
                packet = _overflow + packet;
                _overflow = String.Empty;
            }

            int p = 0;
            if ((p = packet.IndexOf('\0')) != -1)
            {
                String leftovers = String.Empty;

                if (packet.Length > p)
                    leftovers = packet.Substring(p + 1);

                packet = packet.Substring(0, p);

                // URLEncode it!
                packet = Uri.EscapeDataString(packet);

                // plague's OF proxy uses + for spaces, as does wsc.
                packet = packet.Replace("%20", "+");

                // If you want to see the packets, uncomment this line.
                //ConIO.Write(packet);

                byte[] data = Encoding.ASCII.GetBytes(packet);

                // Time to convert the packet to a WebSocket packet!
                byte fss = (byte)(data.Length <= 125 ? 2 : 4);
                byte[] payload = new byte[data.Length + fss];
                int pos = 0;

                payload[pos++] = 129;

                if (fss == 2)
                    payload[pos++] = (byte)data.Length;
                else
                {
                    // We should never get packets larger than 8192 (or 65535 for that matter.)
                    payload[pos++] = 126;
                    payload[pos++] = (byte)((data.Length >> 8) & 255);
                    payload[pos++] = (byte)((data.Length) & 255);
                }

                for (int i = 0; i < data.Length; pos++, i++)
                    payload[pos] = data[i];

                lock (_packet_queue)
                {
                    _packet_queue.Enqueue(payload);
                    wait_event.Set();
                }

                if (!String.IsNullOrEmpty(leftovers))
                    ProcessRemotePacket(leftovers);
            }
            else
            {
                _overflow = packet;
            }
        }

        public void SendRemote(byte[] payload)
        {
            try
            {
                _remote_client.Client.Send(payload, payload.Length, SocketFlags.None);
            }
            catch (Exception)
            {
                ConIO.Warning("Client.SendRemote", "Got exception. Dead client?");
                _remote_client.Close();
                _local_client.Close();
            }
        }

        public void SendLocal(byte[] payload)
        {
            try
            {
                _local_client.Client.Send(payload, payload.Length, SocketFlags.None);
            }
            catch (Exception)
            {
                ConIO.Warning("Client.SendLocal", "Got exception. Dead client?");
                _remote_client.Close();
                _local_client.Close();
            }
        }

        public String EndPoint(bool local = true)
        {
            if (local)
                return _local_client.Client.RemoteEndPoint.ToString();
            else
                return _remote_client.Client.RemoteEndPoint.ToString();
        }
    }
}
