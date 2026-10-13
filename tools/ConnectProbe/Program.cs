using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LiteNetLib;

// Opens one connection to a Schedule I game server at the network layer, holds it, and leaves.
// usage: dotnet run --project tools/ConnectProbe -- <ip> <port> [seconds to stay, default 20]
// Prints what the server's network layer said and when. It never logs in and never becomes a player.
internal static class Program
{
    private sealed class Listener : INetEventListener
    {
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public bool Connected;
        public bool Ended;
        public int BytesFromServer;
        public int PacketsFromServer;

        private void Say(string text)
        {
            Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " (+" + Clock.Elapsed.TotalSeconds.ToString("0.000") + "s) " + text);
        }

        public void OnPeerConnected(NetPeer peer)
        {
            Connected = true;
            Say("the server accepted the connection");
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            Ended = true;
            Say("the connection ended: " + info.Reason + (info.SocketErrorCode != SocketError.Success ? " (" + info.SocketErrorCode + ")" : ""));
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            Say("network error: " + socketError);
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            PacketsFromServer++;
            BytesFromServer += reader.AvailableBytes;
            if (PacketsFromServer <= 3)
            {
                Say("the server sent " + reader.AvailableBytes + " bytes (" + deliveryMethod + ")");
            }

            reader.Recycle();
        }

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            request.Reject();
        }

        public void Note(string text)
        {
            Say(text);
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int port))
        {
            Console.WriteLine("usage: ConnectProbe <ip> <port> [seconds to stay]");
            return 2;
        }

        int stay = args.Length > 2 && int.TryParse(args[2], out int s) ? Math.Max(1, Math.Min(120, s)) : 20;
        Listener listener = new Listener();
        NetManager client = new NetManager(listener);
        if (!client.Start())
        {
            Console.WriteLine("could not open a local socket");
            return 1;
        }

        listener.Note("connecting to " + args[0] + ":" + port + " (staying " + stay + "s)");
        // the game's own client connects with an empty key
        client.Connect(args[0], port, string.Empty);

        Stopwatch held = null;
        while (!listener.Ended)
        {
            client.PollEvents();
            Thread.Sleep(15);
            if (listener.Connected && held == null)
            {
                held = Stopwatch.StartNew();
            }

            if (held != null && held.Elapsed.TotalSeconds >= stay)
            {
                listener.Note("leaving after " + stay + "s; the server sent " + listener.PacketsFromServer + " packets, " + listener.BytesFromServer + " bytes");
                client.DisconnectAll();
                break;
            }

            if (held == null && listener.Clock.Elapsed.TotalSeconds > 15)
            {
                listener.Note("no answer in 15s");
                break;
            }
        }

        client.PollEvents();
        client.Stop();
        return listener.Connected ? 0 : 1;
    }
}
