using System;
using System.Net;
using System.Threading;
using Hazel.Udp;

namespace Hazel.App
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            using (UdpConnection connection = new UdpClientConnection(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 22023)))
            {
                ManualResetEvent e = new ManualResetEvent(false);

                //Whenever we receive data print the number of bytes and how it was sent
                connection.DataReceived += eventArgs =>
                {
                    Console.WriteLine("Received data");
                };

                //When the end point disconnects from us then release the main thread and exit
                connection.Disconnected += (sender, eventArgs) =>
                {
                    Console.WriteLine("Disconnected");

                    e.Set();
                };

                //Connect to a server
                connection.Connect(new byte[] {0xFF, 0xFF, 0xFF, 0xFF});

                //Wait until the end point disconnects from us
                e.WaitOne();
            }
        }
    }
}