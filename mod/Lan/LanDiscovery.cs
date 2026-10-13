using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RepoIOS.Lan
{
    /// <summary>
    /// Finding games on the local network: the host broadcasts a small UDP beacon every second, joining
    /// devices listen for it. (Joining by IP address works without it.)
    /// </summary>
    static class LanDiscovery
    {
        public const int Port = 47777;
        const string Magic = "REPO-LAN-1";

        public sealed class Game
        {
            public string Address, Host, Version;
            public int Port, Players, MaxPlayers;
            public DateTime LastSeen;
        }

        // ---- host side ---------------------------------------------------------------------------------
        static Thread s_Beacon;
        static volatile bool s_BeaconOn;
        public static Func<string> BeaconText;   // null = nothing to announce right now

        public static void StartBeacon()
        {
            if (s_BeaconOn) return;
            s_BeaconOn = true;
            s_Beacon = new Thread(BeaconLoop) { IsBackground = true, Name = "LAN beacon" };
            s_Beacon.Start();
        }

        public static void StopBeacon() => s_BeaconOn = false;

        public static string Beacon(string host, int port, int players, int max, string version) =>
            string.Join("\n", Magic, Clean(host), port, players, max, Clean(version));

        static string Clean(string s) => (s ?? "").Replace('\n', ' ');

        static void BeaconLoop()
        {
            UdpClient udp = null;
            int errors = 0;
            try
            {
                udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                while (s_BeaconOn)
                {
                    string text = null;
                    try { text = BeaconText?.Invoke(); } catch { }
                    if (text != null)
                    {
                        var data = Encoding.UTF8.GetBytes(text);
                        foreach (var target in BroadcastTargets())
                        {
                            try { udp.Send(data, data.Length, new IPEndPoint(target, Port)); }
                            catch (Exception e)
                            {
                                if (errors++ < 5) Log.Warn($"LAN beacon to {target}: {e.Message}");
                            }
                        }
                    }
                    Thread.Sleep(1000);
                }
            }
            catch (Exception e)
            {
                Log.Warn("LAN beacon stopped: " + e.Message);
            }
            finally
            {
                udp?.Close();
            }
        }

        static List<IPAddress> s_Targets;
        static DateTime s_TargetsTime;

        static List<IPAddress> BroadcastTargets()
        {
            if (s_Targets != null && (DateTime.UtcNow - s_TargetsTime).TotalSeconds < 10) return s_Targets;
            var list = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        var ip = ua.Address.GetAddressBytes();
                        var mask = ua.IPv4Mask.GetAddressBytes();
                        if (mask.All(b => b == 0)) continue;
                        var bc = new byte[4];
                        for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                        var a = new IPAddress(bc);
                        if (!list.Contains(a)) list.Add(a);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("LAN interfaces: " + e.Message);
            }
            if (!list.Contains(IPAddress.Loopback)) list.Add(IPAddress.Loopback);   // a second copy on the same PC (testing)
            s_Targets = list;
            s_TargetsTime = DateTime.UtcNow;
            return list;
        }

        // ---- joining side --------------------------------------------------------------------------------
        static Thread s_Listener;
        static volatile bool s_Listening;
        static UdpClient s_ListenUdp;
        static readonly Dictionary<string, Game> s_Games = new Dictionary<string, Game>();
        public static string ListenError;

        public static void StartListening()
        {
            if (s_Listening) return;
            s_Listening = true;
            ListenError = null;
            s_Listener = new Thread(ListenLoop) { IsBackground = true, Name = "LAN discovery" };
            s_Listener.Start();
        }

        public static void StopListening()
        {
            s_Listening = false;
            try { s_ListenUdp?.Close(); } catch { }
        }

        public static List<Game> Games
        {
            get
            {
                lock (s_Games)
                {
                    var now = DateTime.UtcNow;
                    foreach (var k in s_Games.Where(kv => (now - kv.Value.LastSeen).TotalSeconds > 4).Select(kv => kv.Key).ToList())
                        s_Games.Remove(k);
                    return s_Games.Values.OrderBy(g => g.Host).ToList();
                }
            }
        }

        static void ListenLoop()
        {
            try
            {
                var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                udp.EnableBroadcast = true;
                s_ListenUdp = udp;
                Log.Info($"LAN discovery listening on UDP {Port}");
                while (s_Listening)
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data;
                    try { data = udp.Receive(ref from); }
                    catch (SocketException) when (!s_Listening) { break; }
                    catch (ObjectDisposedException) { break; }
                    var parts = Encoding.UTF8.GetString(data).Split('\n');
                    if (parts.Length < 6 || parts[0] != Magic) continue;
                    var g = new Game
                    {
                        Address = from.Address.ToString(),
                        Host = parts[1],
                        Port = int.TryParse(parts[2], out int port) ? port : LanServer.DefaultPort,
                        Players = int.TryParse(parts[3], out int pl) ? pl : 0,
                        MaxPlayers = int.TryParse(parts[4], out int mx) ? mx : 0,
                        Version = parts[5],
                        LastSeen = DateTime.UtcNow,
                    };
                    lock (s_Games)
                    {
                        if (!s_Games.ContainsKey(g.Address + ":" + g.Port))
                            Log.Info($"LAN found game: {g.Host} at {g.Address}:{g.Port} ({g.Players}/{g.MaxPlayers}, {g.Version})");
                        s_Games[g.Address + ":" + g.Port] = g;
                    }
                }
            }
            catch (Exception e)
            {
                ListenError = e.Message;
                Log.Warn("LAN discovery: " + e);
            }
            finally
            {
                s_Listening = false;
            }
        }

        // ---- helpers -----------------------------------------------------------------------------------------
        /// This device's address on the local network (what other players type in to join by IP).
        public static string LocalAddress()
        {
            try
            {
                // iOS: en0 is the Wi-Fi interface
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Name != "en0" || ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork) return ua.Address.ToString();
                }
            }
            catch { }
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(new IPEndPoint(IPAddress.Parse("10.255.255.255"), 9));   // picks the LAN route, sends nothing
                    var a = ((IPEndPoint)s.LocalEndPoint).Address;
                    if (!IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any)) return a.ToString();
                }
            }
            catch { }
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                            return ua.Address.ToString();
                }
            }
            catch { }
            return "?";
        }
    }
}
