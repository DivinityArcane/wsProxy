using System;
using System.Threading;

namespace wsProxy
{
    class Program
    {
        public static Proxy _proxy = null;
        public static bool Running = true;

        /// <summary>
        /// The port we listen for connections on.
        /// </summary>
        private static int _listen_port         = 3901;

        /// <summary>
        /// The host we open the remote connections to.
        /// </summary>
        private static String _redirect_host = "chat.deviantart.com";

        /// <summary>
        /// The port we open the remote connections to.
        /// </summary>
        private static int _redirect_port       = 3900;

        static void Main(string[] args)
        {
            ConIO.Write("Starting proxy...");

            Thread _thread = new Thread(new ThreadStart(Init));
            _thread.Start();

            while (Running)
            {
                Thread.Sleep(150);
            }
        }

        public static void Init()
        {
            _proxy = new Proxy(_listen_port, _redirect_host, _redirect_port);
        }
    }
}
