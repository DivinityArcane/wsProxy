using System;
using System.Net;
using System.Net.Sockets;

namespace wsProxy
{
    public class Proxy
    {
        private TcpListener _listen = null;
        private String _raddr       = null;
        private int _rport          = 0;

        public Proxy(int port, String redirect_addr, int redirect_port)
        {
            _raddr  = redirect_addr;
            _rport  = redirect_port;
            _listen = new TcpListener(IPAddress.Any, port);

            _listen.Start(5);
            _listen.BeginAcceptTcpClient(OnConnect, null);

            ConIO.Write(String.Format("Proxy started. Listening on port {0}", port));
        }

        private void OnConnect(IAsyncResult result)
        {
            TcpClient local_client = _listen.EndAcceptTcpClient(result);

            if (local_client == null)
            {
                ConIO.Warning("Proxy.OnConnect", "Got a null client.");
                _listen.BeginAcceptTcpClient(OnConnect, null);
                return;
            }

            TcpClient remote_client = new TcpClient();

            Client _client = new Client(local_client, remote_client, _raddr, _rport);

            ConIO.Write("New client: " + _client.EndPoint());

            _listen.BeginAcceptTcpClient(OnConnect, null);
        }
    }
}
