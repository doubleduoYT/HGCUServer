using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using LiteNetLib;

public static class HGPlazaServer
{
    private const int V106FullStateSyncMaxPayload = 32768;

    private const int MaxInboundPacketBytes = 131072;
    private const int MaxPlayerSaveBytes = 12 * 1024 * 1024;
    private const int MaxPlayerSaveChunkBytes = 24 * 1024;
    private static readonly bool AllowLegacyUuidAdmins = string.Equals(Environment.GetEnvironmentVariable("HG_ALLOW_LEGACY_UUID_ADMIN"), "1", StringComparison.Ordinal);
    private const int LegacyProtocol = 9000;
    private const int ExplorationProtocol = 9000;
    private const string ConnectionKey = "CU-HG-MP-9.0.0";
    private const byte ModeInternet = 1;
    private const byte ModePlaza = 2;
    private const byte ModeExploration = 3;
    private const string SceneName = "SampleScene";
    private const int ExplorationRespawnSeconds = 180;

    private static readonly int SuspendGraceSeconds = ReadEnvInt("HG_SUSPEND_GRACE_SECONDS", 900, 60, 86400);
    private static readonly int TransportDisconnectTimeoutMs = ReadEnvInt("HG_TRANSPORT_TIMEOUT_MS", 90000, 15000, 600000);

    private enum MessageType : byte
    {
        Hello = 1,
        Welcome = 2,
        PlayerState = 3,
        PlayerStateRelay = 4,
        PlayerLeft = 5,
        SceneChange = 6,
        Chat = 7,
        Error = 8,
        Extension = 9,
        RoomListRequest = 10,
        RoomListResponse = 11
    }

    private sealed class ClientInfo
    {
        public int Id;
        public string Name = "Player";
        public string Platform = string.Empty;
        public NetPeer Peer;
        public bool Welcomed;
        public byte Mode;
        public int Protocol;
        public string PlaceKey = string.Empty;
        public string PlaceLabel = string.Empty;
        public string PlayerUuid = string.Empty;
        public byte[] LastPose;
        public float X;
        public float Y;
        public bool Dead;
        public long RespawnAtUnix;
        public bool HasDeathPosition;
        public float DeathX;
        public float DeathY;
        public bool RespawnAtLayerStart;
        public bool ExplorationAdmin;
        public int SaveRevision;
        public int SaveExpectedChunks;
        public int SaveTotalBytes;
        public bool SaveRejected;
        public bool ExplorationWorldReady;

        public string SessionToken = string.Empty;
        public bool Suspended;
        public bool IntentionalQuit;
        public DateTime LastPacketUtc = DateTime.UtcNow;
        public DateTime DetachedUtc = DateTime.MinValue;
        public DateTime SuspendedAtUtc = DateTime.MinValue;
        public bool MapCaptureReady;
        public readonly SortedDictionary<int, byte[]> SaveChunks = new SortedDictionary<int, byte[]>();
    }

    private sealed class WorldStore
    {
        public string PlaceKey;
        public int Seed;
        public int Revision;
        public int BiomeDepth;
        public bool Dirty;
        public DateTime SaveAfterUtc = DateTime.MinValue;
        public readonly Dictionary<string, string> Blocks = new Dictionary<string, string>();
        public readonly Dictionary<string, string> Objects = new Dictionary<string, string>();
        public readonly Dictionary<string, string> Entities = new Dictionary<string, string>();
    }

    private sealed class DirectGuest
    {
        public int Id;
        public string Token = string.Empty;
        public string Name = "Player";
        public ClientInfo Gateway;
        public DateTime LastSeenUtc = DateTime.UtcNow;
    }

    private sealed class MapCaptureRequest
    {
        public string Id;
        public string PlaceKey;
        public int PlayerId;
        public DateTime ExpiresUtc;
    }

    private sealed class MapCastSession
    {
        public string Id;
        public string PlaceKey;
        public int Width;
        public int Height;
        public int Fps;
        public int Quality;
        public int AuthorityId = -1;
    }

    private sealed class MapImageAssembly
    {
        public string Kind;
        public string Id;
        public int Sequence;
        public int SenderId;
        public int TotalBytes;
        public int ChunkCount;
        public DateTime CreatedUtc = DateTime.UtcNow;
        public readonly SortedDictionary<int, byte[]> Chunks = new SortedDictionary<int, byte[]>();
    }

    private static readonly Dictionary<NetPeer, ClientInfo> ByPeer = new Dictionary<NetPeer, ClientInfo>();
    private static readonly Dictionary<int, ClientInfo> ById = new Dictionary<int, ClientInfo>();
    private static readonly Dictionary<string, int> AuthorityByPlace = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, WorldStore> WorldByPlace = new Dictionary<string, WorldStore>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ExplorationAdmins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, DirectGuest> DirectGuests = new Dictionary<int, DirectGuest>();

    private static readonly Dictionary<string, MapCaptureRequest> MapCaptureRequests = new Dictionary<string, MapCaptureRequest>(StringComparer.Ordinal);
    private static readonly Dictionary<string, MapCastSession> MapCastSessions = new Dictionary<string, MapCastSession>(StringComparer.Ordinal);
    private static readonly Dictionary<string, MapImageAssembly> MapImageAssemblies = new Dictionary<string, MapImageAssembly>(StringComparer.Ordinal);

    private static EventBasedNetListener listener;
    private static NetManager manager;
    private static int port = 19132;
    private static int nextId = 1;
    private static bool running = true;
    private static int plazaSeed;

    private static string DataRoot;
    private static string ExplorationRoot;
    private static string PlayerRoot;
    private static string BridgeRoot;
    private static string LogRoot;
    private static string InboxFile;
    private static string ServerInboxOffsetFile;
    private static string OutboxFile;
    private static string EventsFile;
    private static string StatusFile;
    private static string ExplorationAdminsFile;
    private static string MapResultsRoot;
    private static string MapCastRoot;
    private static long inboxOffset;
    private static DateTime nextStatusWrite = DateTime.MinValue;
    private static DateTime nextSessionSweep = DateTime.MinValue;

    public static int Main(string[] args)
    {
        if (args != null && args.Length > 0)
        {
            int parsed;
            if (int.TryParse(args[0], out parsed) && parsed > 0 && parsed <= 65535) port = parsed;
        }

        DataRoot = ResolveDataRoot(args);
        ExplorationRoot = Path.Combine(DataRoot, "exploration");
        PlayerRoot = Path.Combine(ExplorationRoot, "players");
        BridgeRoot = Path.Combine(DataRoot, "bridge");
        LogRoot = Path.Combine(DataRoot, "logs");
        InboxFile = Path.Combine(BridgeRoot, "discord_inbox.tsv");
        ServerInboxOffsetFile = Path.Combine(BridgeRoot, "server_inbox.offset");
        OutboxFile = Path.Combine(BridgeRoot, "discord_outbox.tsv");
        EventsFile = Path.Combine(LogRoot, "events.log");
        StatusFile = Path.Combine(BridgeRoot, "server_status.tsv");
        ExplorationAdminsFile = Path.Combine(ExplorationRoot, "admins.txt");
        MapResultsRoot = Path.Combine(BridgeRoot, "map_results");
        MapCastRoot = Path.Combine(BridgeRoot, "map_cast");

        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(ExplorationRoot);
        Directory.CreateDirectory(PlayerRoot);
        Directory.CreateDirectory(BridgeRoot);
        Directory.CreateDirectory(MapResultsRoot);
        Directory.CreateDirectory(MapCastRoot);
        Directory.CreateDirectory(LogRoot);
        Touch(InboxFile); Touch(OutboxFile); Touch(EventsFile); Touch(StatusFile); Touch(ExplorationAdminsFile);
        LoadBridgeInboxOffset();
        LoadExplorationAdmins();

        plazaSeed = new Random().Next(100000, int.MaxValue);
        LoadWorld("plaza", false);
        LoadWorld("exploration", true);

        Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            running = false;
        };

        listener = new EventBasedNetListener();
        listener.ConnectionRequestEvent += delegate(ConnectionRequest request) { request.AcceptIfKey(ConnectionKey); };
        listener.PeerConnectedEvent += OnPeerConnected;
        listener.PeerDisconnectedEvent += OnPeerDisconnected;
        listener.NetworkReceiveEvent += OnNetworkReceive;
        listener.NetworkErrorEvent += delegate(IPEndPoint endpoint, SocketError error)
        {
            Console.WriteLine("[NET] socket error · " + error);
        };

        manager = new NetManager(listener);
        manager.AutoRecycle = true;
        manager.IPv6Enabled = false;
        manager.DisconnectTimeout = TransportDisconnectTimeoutMs;
        manager.UpdateTime = 15;
        manager.ChannelsCount = 2;
        manager.ReuseAddress = true;
        manager.UnconnectedMessagesEnabled = false;
        manager.NatPunchEnabled = false;
        manager.BroadcastReceiveEnabled = false;

        if (!manager.Start(port))
        {
            Console.Error.WriteLine("[HG Server] UDP " + port + " 포트를 열 수 없습니다.");
            return 2;
        }

        Console.WriteLine("[HG Server] Protocol 9000 · legacy Plaza compatible · Exploration EXP1 enabled");
        Console.WriteLine("[HG Server] data=" + DataRoot);
        Console.WriteLine("[HG Server] UDP :" + port + " · relay-only");
        Console.WriteLine("[HG Server] suspend grace=" + SuspendGraceSeconds + "s · transport timeout=" + TransportDisconnectTimeoutMs + "ms");
        Console.WriteLine("[HG Server] map capture + mapcast bridge enabled");
        Console.WriteLine("[HG Server] v9.8 full-state sync + v10.6 nearby compatibility enabled");
        Console.WriteLine("[HG Server] v9.10.0 security and persistence maintenance enabled");
        Console.WriteLine("[HG Server] legacy UUID admin auto-restore=" + (AllowLegacyUuidAdmins ? "enabled" : "disabled"));
        AppendEvent("server_start", "server", "Server", "Server started");
        WriteStatus();

        while (running)
        {
            manager.PollEvents();
            PollBridgeInbox();
            SweepLogicalSessions();
            MaintainMapCaptureBridge();
            FlushDirtyWorlds(false);
            if (DateTime.UtcNow >= nextStatusWrite)
            {
                nextStatusWrite = DateTime.UtcNow.AddSeconds(1);
                WriteStatus();
            }
            Thread.Sleep(10);
        }

        FlushDirtyWorlds(true);
        AppendEvent("server_stop", "server", "Server", "Server stopped");
        WriteStatus();
        try { manager.Stop(true); } catch { }
        return 0;
    }

    private static string ParseSessionToken(string platformWire)
    {
        if (string.IsNullOrEmpty(platformWire)) return string.Empty;
        int i = platformWire.LastIndexOf("|S:", StringComparison.Ordinal);
        if (i < 0 || i + 3 >= platformWire.Length) return string.Empty;
        string token = platformWire.Substring(i + 3).Trim();
        if (token.Length > 32) token = token.Substring(0, 32);
        for (int n = 0; n < token.Length; n++)
            if (!char.IsLetterOrDigit(token[n]) && token[n] != '-' && token[n] != '_') return string.Empty;
        return token;
    }

    private static string StripSessionTag(string platformWire)
    {
        if (string.IsNullOrEmpty(platformWire)) return string.Empty;
        int i = platformWire.LastIndexOf("|S:", StringComparison.Ordinal);
        return i < 0 ? platformWire : platformWire.Substring(0, i);
    }

    private static ClientInfo FindSuspendedSession(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        foreach (ClientInfo c in ById.Values)
            if (c.Welcomed && c.Suspended && string.Equals(c.SessionToken, token, StringComparison.Ordinal)) return c;
        return null;
    }

    private static bool TryResumeSuspended(ClientInfo provisional, string token, string name, string platform,
        byte mode, int protocol, string playerUuid)
    {
        ClientInfo old = FindSuspendedSession(token);
        if (old == null || provisional == null || provisional.Peer == null) return false;
        if (old.Mode != mode || !string.Equals(old.PlaceKey, provisional.PlaceKey, StringComparison.OrdinalIgnoreCase))
            return false;
        if (mode == ModeExploration && !string.Equals(old.PlayerUuid, playerUuid, StringComparison.OrdinalIgnoreCase))
            return false;

        NetPeer newPeer = provisional.Peer;
        int provisionalId = provisional.Id;
        ById.Remove(provisionalId);

        NetPeer previousPeer = old.Peer;
        if (previousPeer != null && previousPeer != newPeer)
        {
            ByPeer.Remove(previousPeer);
            try { previousPeer.Disconnect(); } catch { }
        }

        ByPeer[newPeer] = old;
        old.Peer = newPeer;
        old.Name = name;
        old.Platform = platform;
        old.Protocol = protocol;
        old.Suspended = false;
        old.IntentionalQuit = false;
        old.DetachedUtc = DateTime.MinValue;
        old.SuspendedAtUtc = DateTime.MinValue;
        old.LastPacketUtc = DateTime.UtcNow;
        if (old.Mode == ModeExploration)
            old.ExplorationAdmin = old.ExplorationAdmin || (AllowLegacyUuidAdmins && ExplorationAdmins.Contains(old.PlayerUuid));

        newPeer.Send(BuildWelcome(old), DeliveryMethod.ReliableOrdered);
        SendPersistentWorld(old);
        SendExistingPoses(old);
        if (old.Mode == ModeExploration)
        {
            WorldStore ew = GetWorld("exploration", true);
            SendExtension(old.Id, "EXPMETA|" + ew.BiomeDepth + "|" + ew.Revision);
            SendExplorationAdminSnapshot(old);
        }

        EnsureAuthority(old.PlaceKey);
        RestoreResumedToAuthoritySimulation(old);
        BroadcastExtensionToPlace(old.PlaceKey, "APPRESUME|" + old.Id, newPeer, old.Id);
        BroadcastExtensionToPlace(old.PlaceKey, "DAUTH|" + GetAuthority(old.PlaceKey), null, -1);
        SendCurrentRosterToAuthority(old.PlaceKey);
        SendExtension(old.Id, "APPRESUMED|" + old.Id);
        WriteStatus();
        Console.WriteLine("[RESUME] " + old.PlaceLabel + " #" + old.Id + " " + old.Name + " · logical session restored");
        return true;
    }

    private static bool HandleAppSessionControl(ClientInfo c, string payload)
    {
        if (c == null || string.IsNullOrEmpty(payload)) return false;
        string[] p = payload.Split('|');
        if (p.Length == 0) return false;
        string op = p[0];
        if (op != "APPSUSPEND" && op != "APPRESUME" && op != "APPQUIT") return false;

        string token = p.Length >= 2 ? p[1] : string.Empty;
        if (!string.IsNullOrEmpty(c.SessionToken) && !string.Equals(token, c.SessionToken, StringComparison.Ordinal))
            return true;
        c.LastPacketUtc = DateTime.UtcNow;

        if (op == "APPSUSPEND")
        {
            if (c.Suspended) return true;
            c.Suspended = true;
            c.IntentionalQuit = false;
            c.SuspendedAtUtc = DateTime.UtcNow;
            if (c.Mode == ModeExploration) SavePlayerMeta(c, true);
            BroadcastExtensionToPlace(c.PlaceKey, "APPSUSPEND|" + c.Id, c.Peer, c.Id);
            int auth;
            if (AuthorityByPlace.TryGetValue(c.PlaceKey, out auth) && auth == c.Id)
                AuthorityByPlace.Remove(c.PlaceKey);
            EnsureAuthority(c.PlaceKey);
            RemoveSuspendedFromAuthoritySimulation(c);
            BroadcastExtensionToPlace(c.PlaceKey, "DAUTH|" + GetAuthority(c.PlaceKey), null, -1);
            SendCurrentRosterToAuthority(c.PlaceKey);
            WriteStatus();
            return true;
        }

        if (op == "APPRESUME")
        {
            c.Suspended = false;
            c.IntentionalQuit = false;
            c.DetachedUtc = DateTime.MinValue;
            c.SuspendedAtUtc = DateTime.MinValue;
            SendPersistentWorld(c);
            SendExistingPoses(c);
            EnsureAuthority(c.PlaceKey);
            RestoreResumedToAuthoritySimulation(c);
            BroadcastExtensionToPlace(c.PlaceKey, "APPRESUME|" + c.Id, c.Peer, c.Id);
            BroadcastExtensionToPlace(c.PlaceKey, "DAUTH|" + GetAuthority(c.PlaceKey), null, -1);
            SendCurrentRosterToAuthority(c.PlaceKey);
            SendExtension(c.Id, "APPRESUMED|" + c.Id);
            WriteStatus();
            return true;
        }

        c.Suspended = false;
        c.IntentionalQuit = true;
        try { if (c.Peer != null) c.Peer.Disconnect(); } catch { }
        return true;
    }

    private static void SweepLogicalSessions()
    {
        DateTime now = DateTime.UtcNow;
        if (now < nextSessionSweep) return;
        nextSessionSweep = now.AddSeconds(2);

        List<ClientInfo> expireSuspended = new List<ClientInfo>();
        List<ClientInfo> dropActive = new List<ClientInfo>();
        foreach (ClientInfo c in ById.Values)
        {
            if (c == null || !c.Welcomed) continue;
            if (c.Suspended)
            {

                DateTime since = c.SuspendedAtUtc != DateTime.MinValue ? c.SuspendedAtUtc : c.DetachedUtc;
                if (since == DateTime.MinValue)
                {
                    c.SuspendedAtUtc = now;
                    since = now;
                }
                if ((now - since).TotalSeconds >= SuspendGraceSeconds)
                    expireSuspended.Add(c);
            }
            else if (c.Peer != null && (now - c.LastPacketUtc).TotalSeconds >= 60)
                dropActive.Add(c);
        }

        foreach (ClientInfo c in dropActive)
            try { c.Peer.Disconnect(); } catch { }

        foreach (ClientInfo c in expireSuspended)
        {
            NetPeer stalePeer = c.Peer;
            if (stalePeer != null)
            {

                ByPeer.Remove(stalePeer);
                c.Peer = null;
                c.IntentionalQuit = true;
                try { stalePeer.Disconnect(); } catch { }
            }
            ById.Remove(c.Id);
            if (c.Mode == ModeExploration) SavePlayerMeta(c, true);
            BroadcastToPlace(c.PlaceKey, BuildPlayerLeft(c.Id), DeliveryMethod.ReliableOrdered, null, -1);
            AppendEvent("leave", c.PlaceKey, c.PlaceLabel, c.Name + " left (suspended session expired)");
            int auth;
            if (AuthorityByPlace.TryGetValue(c.PlaceKey, out auth) && auth == c.Id) AuthorityByPlace.Remove(c.PlaceKey);
            EnsureAuthority(c.PlaceKey);
            SendCurrentRosterToAuthority(c.PlaceKey);
        }
        if (expireSuspended.Count > 0) WriteStatus();
    }

    private static int ReadEnvInt(string name, int fallback, int min, int max)
    {
        try
        {
            string raw = Environment.GetEnvironmentVariable(name);
            int value;
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out value))
                return Math.Max(min, Math.Min(max, value));
        }
        catch { }
        return fallback;
    }

    private static string ResolveDataRoot(string[] args)
    {
        string env = Environment.GetEnvironmentVariable("HG_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env.Trim());
        if (args != null && args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])) return Path.GetFullPath(args[1].Trim());
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
    }

    private static void OnPeerConnected(NetPeer peer)
    {
        ClientInfo c = new ClientInfo { Id = nextId++, Peer = peer, LastPacketUtc = DateTime.UtcNow };
        ByPeer[peer] = c;
        ById[c.Id] = c;
        Console.WriteLine("[CONNECT] pending #" + c.Id + " · endpoint hidden");
    }

    private static void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        RemoveDirectGuestsForPeer(peer);
        ClientInfo c;
        if (!ByPeer.TryGetValue(peer, out c)) return;
        ByPeer.Remove(peer);

        if (c.Welcomed && c.Suspended && !c.IntentionalQuit && !string.IsNullOrEmpty(c.SessionToken))
        {
            c.Peer = null;
            c.DetachedUtc = DateTime.UtcNow;
            if (c.Mode == ModeExploration) SavePlayerMeta(c, true);
            int auth;
            if (AuthorityByPlace.TryGetValue(c.PlaceKey, out auth) && auth == c.Id)
                AuthorityByPlace.Remove(c.PlaceKey);
            EnsureAuthority(c.PlaceKey);
            BroadcastExtensionToPlace(c.PlaceKey, "DAUTH|" + GetAuthority(c.PlaceKey), null, -1);
            WriteStatus();
            Console.WriteLine("[SUSPEND] retained " + c.PlaceLabel + " #" + c.Id + " " + c.Name + " · transport detached");
            return;
        }

        ById.Remove(c.Id);
        if (c.Welcomed)
        {
            if (c.Mode == ModeExploration)
            {
                SavePlayerMeta(c, true);
                if (c.ExplorationAdmin) BroadcastExtensionToPlace("exploration", "ADM|" + c.Id + "|0", null, -1);
            }
            BroadcastToPlace(c.PlaceKey, BuildPlayerLeft(c.Id), DeliveryMethod.ReliableOrdered, null, -1);
            AppendEvent("leave", c.PlaceKey, c.PlaceLabel, c.Name + " left");
            if (c.Mode == ModeInternet && WelcomedCount(c.PlaceKey) == 0)
                AppendEvent("room_end", c.PlaceKey, c.PlaceLabel, "Room ended");
            Console.WriteLine("[LEAVE] " + c.PlaceLabel + " #" + c.Id + " " + c.Name + " · " + info.Reason);
        }

        int currentAuthority;
        if (AuthorityByPlace.TryGetValue(c.PlaceKey, out currentAuthority) && currentAuthority == c.Id)
            AuthorityByPlace.Remove(c.PlaceKey);
        EnsureAuthority(c.PlaceKey);
        BroadcastExtensionToPlace(c.PlaceKey, "DAUTH|" + GetAuthority(c.PlaceKey), null, -1);
        SendCurrentRosterToAuthority(c.PlaceKey);
        WriteStatus();
    }

    private static void OnNetworkReceive(NetPeer peer, NetPacketReader netReader, byte channel, DeliveryMethod deliveryMethod)
    {
        ClientInfo c;
        if (!ByPeer.TryGetValue(peer, out c)) return;
        c.LastPacketUtc = DateTime.UtcNow;
        byte[] bytes;
        try { bytes = netReader.GetRemainingBytes(); } catch { return; }
        if (bytes == null || bytes.Length < 1 || bytes.Length > MaxInboundPacketBytes) return;

        try
        {
            using (MemoryStream ms = new MemoryStream(bytes, false))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                MessageType type = (MessageType)reader.ReadByte();

                if (type == MessageType.RoomListRequest) HandleRoomListRequest(c, reader);
                else if (type == MessageType.Hello) HandleHello(c, reader);
                else if (c.Welcomed && type == MessageType.PlayerState) HandlePlayerState(c, bytes);
                else if (c.Welcomed && type == MessageType.Extension) HandleExtension(c, ReadSafeString(reader, 32768));
            }
        }
        catch (Exception ex) { Console.WriteLine("[PACKET] #" + c.Id + " " + ex.Message); }
    }

    private static void HandleRoomListRequest(ClientInfo c, BinaryReader reader)
    {
        int requestedProtocol = LegacyProtocol;
        try
        {
            if (reader.BaseStream.Position + 4 <= reader.BaseStream.Length)
                requestedProtocol = reader.ReadInt32();
        }
        catch { }
        if (requestedProtocol != LegacyProtocol || c == null || c.Peer == null) return;
        try { c.Peer.Send(BuildRoomListResponse(), DeliveryMethod.ReliableOrdered); } catch { }
    }

    private static void HandleHello(ClientInfo c, BinaryReader reader)
    {
        if (c.Welcomed) return;
        int protocol = reader.ReadInt32();
        string name = NormalizeName(ReadSafeString(reader, 24));
        byte mode = reader.ReadByte();
        string platformWire = ReadSafeString(reader, 32);
        string sessionToken = ParseSessionToken(platformWire);
        string platform = StripSessionTag(platformWire);
        string extra1 = string.Empty;
        string extra2 = string.Empty;
        if (reader.BaseStream.Position < reader.BaseStream.Length) extra1 = ReadSafeString(reader, 80);
        if (reader.BaseStream.Position < reader.BaseStream.Length) extra2 = ReadSafeString(reader, 80);
        string uuid = mode == ModeExploration ? NormalizeUuid(extra1) : string.Empty;
        string capability = mode == ModeExploration ? extra2 : string.Empty;

        string roomName = mode == ModeInternet ? NormalizeRoom(!string.IsNullOrWhiteSpace(extra2) ? extra2 : extra1) : string.Empty;

        if (mode == ModePlaza)
        {

            if (protocol != LegacyProtocol)
            {
                Reject(c, "Plaza requires Protocol 9000."); return;
            }
            c.PlaceKey = "plaza";
            c.PlaceLabel = "Plaza";
        }
        else if (mode == ModeExploration)
        {
            if (protocol != ExplorationProtocol || !string.Equals(capability, "EXP1", StringComparison.Ordinal))
            {
                Reject(c, "Exploration requires the new HG Multiplayer client."); return;
            }
            if (string.IsNullOrWhiteSpace(uuid))
            {
                Reject(c, "Exploration player identity is missing."); return;
            }
            foreach (ClientInfo existing in ById.Values)
            {
                if (existing == c || !existing.Welcomed || existing.Mode != ModeExploration) continue;
                if (existing.Suspended && !string.IsNullOrEmpty(sessionToken) &&
                    string.Equals(existing.SessionToken, sessionToken, StringComparison.Ordinal)) continue;
                if (string.Equals(existing.PlayerUuid, uuid, StringComparison.OrdinalIgnoreCase))
                {
                    Reject(c, "This Exploration character is already connected."); return;
                }
            }
            c.PlaceKey = "exploration";
            c.PlaceLabel = "Exploration";
        }
        else if (mode == ModeInternet)
        {
            if (protocol != ExplorationProtocol)
            {
                Reject(c, "Internet room protocol mismatch."); return;
            }
            if (string.IsNullOrWhiteSpace(roomName)) roomName = "HG Room";
            c.PlaceKey = "room:" + roomName;
            c.PlaceLabel = roomName;
        }
        else { Reject(c, "Unsupported multiplayer mode."); return; }

        if (TryResumeSuspended(c, sessionToken, name, platform, mode, protocol, uuid)) return;

        c.Name = name;
        c.Platform = platform;
        c.Mode = mode;
        c.Protocol = protocol;
        c.PlayerUuid = uuid;
        c.SessionToken = sessionToken;
        c.Suspended = false;
        c.IntentionalQuit = false;
        c.LastPacketUtc = DateTime.UtcNow;
        c.ExplorationAdmin = c.Mode == ModeExploration && AllowLegacyUuidAdmins && ExplorationAdmins.Contains(uuid);
        c.Welcomed = true;
        c.Peer.Send(BuildWelcome(c), DeliveryMethod.ReliableOrdered);
        if (c.Mode == ModeExploration)
        {
            WorldStore ew = GetWorld("exploration", true);
            SendExtension(c.Id, "EXPMETA|" + ew.BiomeDepth + "|" + ew.Revision);
            SendExplorationAdminSnapshot(c);
            if (c.ExplorationAdmin) BroadcastExtensionToPlace("exploration", "ADM|" + c.Id + "|1", null, -1);
        }
        else SendPersistentWorld(c);
        SendExistingPoses(c);

        bool hadAuthority = GetAuthority(c.PlaceKey) > 0;
        EnsureAuthority(c.PlaceKey);
        BroadcastExtensionToPlace(c.PlaceKey, "DAUTH|" + GetAuthority(c.PlaceKey), null, -1);
        int authority = GetAuthority(c.PlaceKey);
        if (authority > 0 && c.Id != authority)
            SendExtension(authority, "DSJOIN|" + c.Id + "|" + E(c.Name));
        else if (!hadAuthority && c.Id == authority)
            SendCurrentRosterToAuthority(c.PlaceKey);

        if (c.Mode == ModeExploration) SendPlayerSave(c);
        if (c.Mode == ModeInternet && WelcomedCount(c.PlaceKey) == 1)
            AppendEvent("room_create", c.PlaceKey, c.PlaceLabel, "Room created");
        AppendEvent("join", c.PlaceKey, c.PlaceLabel, c.Name + " joined");
        Console.WriteLine("[JOIN] " + c.PlaceLabel + " #" + c.Id + " " + c.Name + " · users=" + WelcomedCount(c.PlaceKey));
        WriteStatus();
    }

    private static void Reject(ClientInfo c, string message)
    {
        try { c.Peer.Send(BuildError(message), DeliveryMethod.ReliableOrdered); } catch { }
        try { c.Peer.Disconnect(); } catch { }
    }

    private static void HandlePlayerState(ClientInfo c, byte[] bytes)
    {
        if (c == null || c.Suspended) return;
        if (bytes.Length <= 1) return;
        int len = bytes.Length - 1;
        c.LastPose = new byte[len];
        Buffer.BlockCopy(bytes, 1, c.LastPose, 0, len);
        if (len >= 8)
        {
            try
            {
                using (BinaryReader r = new BinaryReader(new MemoryStream(c.LastPose, false)))
                {
                    c.X = r.ReadSingle(); c.Y = r.ReadSingle();
                }
            }
            catch { }
        }
        BroadcastToPlace(c.PlaceKey, BuildStateRelay(c.Id, c.Name, c.LastPose), DeliveryMethod.Unreliable, c.Peer, -1);
    }

    private static void HandleExtension(ClientInfo sender, string payload)
    {
        if (string.IsNullOrEmpty(payload)) return;
        if (HandleAppSessionControl(sender, payload)) return;
        if (sender.Suspended) return;
        if (payload == "MAPCAPREADY|1")
        {
            sender.MapCaptureReady = true;
            return;
        }
        if (HandleMapImagePayload(sender, payload)) return;

        if (sender.Mode == ModeExploration)
        {
            if (HandleExplorationPersistence(sender, payload)) return;
        }

        if (HandleDirectGuestExtension(sender, payload)) return;
        LogGameExtension(sender, payload);

        int authority = GetAuthority(sender.PlaceKey);
        if (sender.Id == authority)
        {
            const string ex = "DHOSTBCASTEX|";
            const string bc = "DHOSTBCAST|";
            const string to = "DHOSTTO|";
            if (payload.StartsWith(ex, StringComparison.Ordinal))
            {
                int split = payload.IndexOf('|', ex.Length);
                int exceptId;
                if (split > ex.Length && int.TryParse(payload.Substring(ex.Length, split - ex.Length), out exceptId))
                {
                    string actual = payload.Substring(split + 1);
                    if (sender.Mode != ModeExploration || sender.ExplorationWorldReady)
                        RememberWorldPayload(sender.PlaceKey, actual);
                    ObserveAuthoritativePayload(sender.PlaceKey, actual);
                    BroadcastExtensionToPlace(sender.PlaceKey, actual, sender.Peer, exceptId);
                }
                return;
            }
            if (payload.StartsWith(bc, StringComparison.Ordinal))
            {
                string actual = payload.Substring(bc.Length);
                if (sender.Mode != ModeExploration || sender.ExplorationWorldReady)
                    RememberWorldPayload(sender.PlaceKey, actual);
                ObserveAuthoritativePayload(sender.PlaceKey, actual);
                BroadcastExtensionToPlace(sender.PlaceKey, actual, sender.Peer, -1);
                return;
            }
            if (payload.StartsWith(to, StringComparison.Ordinal))
            {
                int split = payload.IndexOf('|', to.Length);
                int target;
                if (split > to.Length && int.TryParse(payload.Substring(to.Length, split - to.Length), out target))
                {
                    string actual = payload.Substring(split + 1);
                    if (!TrySendDirectGuestTarget(target, sender.PlaceKey, actual))
                        SendExtensionIfPlace(target, sender.PlaceKey, actual);
                }
                return;
            }
        }

        if (payload.StartsWith("WORLDREADY", StringComparison.Ordinal)) SendExistingPoses(sender);
        EnsureAuthority(sender.PlaceKey);
        authority = GetAuthority(sender.PlaceKey);
        if (authority > 0)
            SendExtension(authority, "DSREQ|" + sender.Id + "|" + payload);
    }

    private static bool HandleDirectGuestExtension(ClientInfo sender, string payload)
    {
        if (sender == null || string.IsNullOrEmpty(payload)) return false;

        if (payload.StartsWith("DPEERJOIN|", StringComparison.Ordinal))
        {
            string[] p = payload.Split('|'); int id;
            if (p.Length < 4 || !int.TryParse(p[2], out id) || !ValidDirectGuestId(id)) return true;
            DirectGuest g;
            if (!DirectGuests.TryGetValue(id, out g)) { g = new DirectGuest(); g.Id = id; DirectGuests[id] = g; }
            g.Token = p[1]; g.Gateway = sender; g.Name = SafeB64Text(p[3], 24, "Player"); g.LastSeenUtc = DateTime.UtcNow;
            BroadcastExtensionToPlace(sender.PlaceKey, "DPEERJOIN|" + id + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(g.Name)), null, -1);
            Console.WriteLine("[DIRECT JOIN] " + sender.PlaceLabel + " #" + id + " " + g.Name + " via #" + sender.Id);
            return true;
        }

        if (payload.StartsWith("DPEERPOSE|", StringComparison.Ordinal))
        {
            string[] p = payload.Split('|'); int id;
            if (p.Length < 5 || !int.TryParse(p[2], out id)) return true;
            DirectGuest g; if (!TryGetDirectGuest(sender, p[1], id, out g)) return true;
            g.LastSeenUtc = DateTime.UtcNow; g.Name = SafeB64Text(p[3], 24, g.Name);
            BroadcastExtensionToPlace(sender.PlaceKey, "DPEERPOSE|" + id + "|" + p[3] + "|" + p[4], null, -1);
            return true;
        }

        if (payload.StartsWith("DPEEREXT|", StringComparison.Ordinal))
        {
            string[] p = payload.Split('|'); int id;
            if (p.Length < 4 || !int.TryParse(p[2], out id)) return true;
            DirectGuest g; if (!TryGetDirectGuest(sender, p[1], id, out g)) return true;
            g.LastSeenUtc = DateTime.UtcNow;
            string inner;
            try { inner = Encoding.UTF8.GetString(Convert.FromBase64String(p[3])); } catch { return true; }
            if (inner.Length > 32768) inner = inner.Substring(0, 32768);

            string[] ip = inner.Split('|');
            if (ip.Length >= 2 && ip[0] == "CHAT") AppendBridge("chat", sender.PlaceKey, sender.PlaceLabel, g.Name, D(ip[1]));
            else if (ip.Length >= 2 && ip[0] == "SPEECH") AppendBridge("speech", sender.PlaceKey, sender.PlaceLabel, g.Name, D(ip[1]));

            EnsureAuthority(sender.PlaceKey); int authority = GetAuthority(sender.PlaceKey);
            if (authority > 0) SendExtension(authority, "DSREQ|" + id + "|" + inner);
            return true;
        }

        if (payload.StartsWith("DPEERLEFT|", StringComparison.Ordinal))
        {
            string[] p = payload.Split('|'); int id;
            if (p.Length < 3 || !int.TryParse(p[2], out id)) return true;
            DirectGuest g; if (TryGetDirectGuest(sender, p[1], id, out g)) RemoveDirectGuest(g, true);
            return true;
        }
        return false;
    }

    private static bool ValidDirectGuestId(int id) { return id >= 1500000000 && id < 1900000000; }

    private static bool TryGetDirectGuest(ClientInfo gateway, string token, int id, out DirectGuest g)
    {
        g = null; DirectGuest found;
        if (!DirectGuests.TryGetValue(id, out found) || found == null || found.Gateway != gateway) return false;
        if (!string.Equals(found.Token, token ?? string.Empty, StringComparison.Ordinal)) return false;
        g = found; return true;
    }

    private static bool TrySendDirectGuestTarget(int id, string place, string actual)
    {
        DirectGuest g;
        if (!DirectGuests.TryGetValue(id, out g) || g == null || g.Gateway == null || !g.Gateway.Welcomed || g.Gateway.Suspended ||
            !string.Equals(g.Gateway.PlaceKey, place, StringComparison.OrdinalIgnoreCase)) return false;
        SendExtension(g.Gateway.Id, "DPEERTO|" + id + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(actual ?? string.Empty)));
        return true;
    }

    private static void RemoveDirectGuestsForPeer(NetPeer peer)
    {
        if (peer == null || DirectGuests.Count == 0) return;
        List<DirectGuest> remove = new List<DirectGuest>();
        foreach (DirectGuest g in DirectGuests.Values) if (g != null && g.Gateway != null && g.Gateway.Peer == peer) remove.Add(g);
        foreach (DirectGuest g in remove) RemoveDirectGuest(g, true);
    }

    private static void RemoveDirectGuest(DirectGuest g, bool broadcast)
    {
        if (g == null) return; DirectGuests.Remove(g.Id);
        if (broadcast && g.Gateway != null) BroadcastExtensionToPlace(g.Gateway.PlaceKey, "DPEERLEFT|" + g.Id, null, -1);
        Console.WriteLine("[DIRECT LEAVE] #" + g.Id + " " + g.Name);
    }

    private static string SafeB64Text(string value, int max, string fallback)
    {
        try { string x = Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty)); if (x.Length > max) x = x.Substring(0, max); return string.IsNullOrWhiteSpace(x) ? fallback : x; }
        catch { return fallback; }
    }

private static bool HandleExplorationPersistence(ClientInfo c, string payload)
    {
        string[] p = payload.Split('|');
        if (p.Length == 0) return false;
        string op = p[0];
        if (op == "EXPMETAREADY")
        {
            c.ExplorationWorldReady = true;
            SendPersistentWorld(c);
            return true;
        }
        if (op == "EXPSAVEBEGIN" && p.Length >= 4)
        {
            int rev, chunks;
            if (!int.TryParse(p[1], out rev) || !int.TryParse(p[2], out chunks)) return true;
            c.SaveRevision = rev;
            c.SaveRejected = chunks < 0 || chunks > 2048;
            c.SaveExpectedChunks = c.SaveRejected ? 0 : chunks;
            c.SaveTotalBytes = 0;
            c.SaveChunks.Clear();
            float x, y;
            if (float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) c.X = x;
            if (p.Length >= 5 && float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) c.Y = y;
            return true;
        }
        if (op == "EXPSAVECHUNK" && p.Length >= 4)
        {
            int rev, index;
            if (c.SaveRejected || !int.TryParse(p[1], out rev) || rev != c.SaveRevision || !int.TryParse(p[2], out index)) return true;
            if (index < 0 || index >= c.SaveExpectedChunks) return true;
            try
            {
                byte[] data = Convert.FromBase64String(p[3]);
                if (data.Length > MaxPlayerSaveChunkBytes) { c.SaveRejected = true; c.SaveChunks.Clear(); return true; }
                byte[] old;
                int prior = c.SaveChunks.TryGetValue(index, out old) ? old.Length : 0;
                if (c.SaveTotalBytes - prior + data.Length > MaxPlayerSaveBytes)
                {
                    c.SaveRejected = true;
                    c.SaveChunks.Clear();
                    return true;
                }
                c.SaveTotalBytes = c.SaveTotalBytes - prior + data.Length;
                c.SaveChunks[index] = data;
            }
            catch { }
            return true;
        }
        if (op == "EXPSAVEEND" && p.Length >= 2)
        {
            int rev;
            if (!int.TryParse(p[1], out rev) || rev != c.SaveRevision) return true;
            CommitPlayerSave(c);
            return true;
        }
        if (op == "EXPRESPAWN")
        {
            long now = UnixNow();
            if (!c.Dead || c.RespawnAtUnix <= 0 || now < c.RespawnAtUnix) return true;

            bool useLayerStart = c.RespawnAtLayerStart;
            float respawnX = c.HasDeathPosition ? c.DeathX : c.X;
            float respawnY = c.HasDeathPosition ? c.DeathY : c.Y;
            if (!useLayerStart) { c.X = respawnX; c.Y = respawnY; }
            c.Dead = false; c.RespawnAtUnix = 0;
            c.HasDeathPosition = false;
            c.RespawnAtLayerStart = false;
            SavePlayerMeta(c, true);
            SendExtension(c.Id, "ACTION|" + c.Id + "|REVIVE||1");
            if (useLayerStart) SendExtension(c.Id, "EXPRESPAWNSTART");
            else SendExtension(c.Id, "EXPRESPAWNOK|" + respawnX.ToString("R", CultureInfo.InvariantCulture) + "|" + respawnY.ToString("R", CultureInfo.InvariantCulture));
            string deadPacket = "PDEAD|" + c.Id + "|0";
            BroadcastExtensionToPlace(c.PlaceKey, deadPacket, null, -1);
            AppendEvent("revive", c.PlaceKey, c.PlaceLabel, c.Name + (useLayerStart ? " self-revived at the layer start" : " self-revived at the death position"));
            return true;
        }
        if (op == "EXPRESPAWNPOS" && p.Length >= 3)
        {
            float x, y;
            if (float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
            {
                c.X = x; c.Y = y;
                SavePlayerMeta(c, true);
            }
            return true;
        }
        return false;
    }

    private static void ObserveAuthoritativePayload(string placeKey, string payload)
    {
        if (string.IsNullOrEmpty(payload)) return;
        string[] p = payload.Split('|');
        if (p.Length == 0) return;
        if (p[0] == "PDEAD" && p.Length >= 3)
        {
            int id;
            if (!int.TryParse(p[1], out id)) return;
            ClientInfo target;
            if (!ById.TryGetValue(id, out target) || target.PlaceKey != placeKey) return;
            bool dead = p[2] == "1";
            if (target.Dead == dead) return;
            target.Dead = dead;
            bool immediateSoloRespawn = false;
            target.RespawnAtUnix = dead && target.Mode == ModeExploration ? UnixNow() + ExplorationRespawnSeconds : 0;
            if (target.Mode == ModeExploration)
            {
                if (dead)
                {
                    target.DeathX = target.X;
                    target.DeathY = target.Y;
                    target.HasDeathPosition = true;
                    immediateSoloRespawn = !HasOtherExplorationPlayer(target);
                    target.RespawnAtLayerStart = immediateSoloRespawn;

                    target.RespawnAtUnix = immediateSoloRespawn ? 0 : UnixNow() + ExplorationRespawnSeconds;
                }
                else
                {
                    target.HasDeathPosition = false;
                    target.RespawnAtLayerStart = false;
                }
                SavePlayerMeta(target, true);
                if (dead && !immediateSoloRespawn) SendExtension(target.Id, "EXPRESPAWNAT|" + target.RespawnAtUnix);
            }
            AppendEvent(dead ? "death" : "revive", target.PlaceKey, target.PlaceLabel,
                target.Name + (dead ? " died" : " was revived"));
            if (dead && immediateSoloRespawn) CompleteImmediateSoloExplorationRespawn(target);
        }
    }

    private static void LogGameExtension(ClientInfo sender, string payload)
    {
        if (string.IsNullOrEmpty(payload)) return;
        string[] p = payload.Split('|');
        if (p.Length == 0) return;
        if (p[0] == "CHAT" && p.Length >= 2)
        {
            AppendBridge("chat", sender.PlaceKey, sender.PlaceLabel, sender.Name, D(p[1]));
        }
        else if (p[0] == "SPEECH" && p.Length >= 2)
        {
            AppendBridge("speech", sender.PlaceKey, sender.PlaceLabel, sender.Name, D(p[1]));
        }
        else if (p[0] == "DEATHREPORT" && p.Length >= 3)
        {
            string cause = B64D(p[1]);
            string report = B64D(p[2]);
            if (string.IsNullOrWhiteSpace(cause)) cause = "Unknown";
            if (string.IsNullOrWhiteSpace(report)) report = "No forensic data was received.";
            AppendBridge("death_report", sender.PlaceKey, sender.PlaceLabel, sender.Name,
                cause + "\u001f" + report);
            try
            {
                string ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                File.AppendAllText(EventsFile, ts + "\tdeath_report\t[" + sender.PlaceLabel + "]\t" +
                    sender.Name + " | Cause: " + cause.Replace("\r", " ").Replace("\n", " ") + "\n");
            }
            catch { }
        }
        else if (p[0] == "PDEAD" && p.Length >= 2)
        {
            bool dead = p[1] == "1";
            if (sender.Dead != dead)
            {
                sender.Dead = dead;
                bool immediateSoloRespawn = false;
                sender.RespawnAtUnix = dead && sender.Mode == ModeExploration ? UnixNow() + ExplorationRespawnSeconds : 0;
                if (sender.Mode == ModeExploration)
                {
                    if (dead)
                    {
                        sender.DeathX = sender.X;
                        sender.DeathY = sender.Y;
                        sender.HasDeathPosition = true;
                        immediateSoloRespawn = !HasOtherExplorationPlayer(sender);
                        sender.RespawnAtLayerStart = immediateSoloRespawn;
                        sender.RespawnAtUnix = immediateSoloRespawn ? 0 : UnixNow() + ExplorationRespawnSeconds;
                    }
                    else
                    {
                        sender.HasDeathPosition = false;
                        sender.RespawnAtLayerStart = false;
                    }
                    SavePlayerMeta(sender, true);
                    if (dead && !immediateSoloRespawn) SendExtension(sender.Id, "EXPRESPAWNAT|" + sender.RespawnAtUnix);
                }
                AppendEvent(dead ? "death" : "revive", sender.PlaceKey, sender.PlaceLabel,
                    sender.Name + (dead ? " died" : " was revived"));
                if (dead && immediateSoloRespawn) CompleteImmediateSoloExplorationRespawn(sender);
            }
        }
        else if (p[0] == "RREV" && p.Length >= 2)
        {
            int targetId;
            if (int.TryParse(p[1], out targetId))
            {
                ClientInfo target;
                if (ById.TryGetValue(targetId, out target) && target.PlaceKey == sender.PlaceKey)
                    AppendBridge("revive_request", sender.PlaceKey, sender.PlaceLabel, sender.Name,
                        "revive -> " + target.Name);
            }
        }
    }

    private static void EnsureAuthority(string placeKey)
    {
        if (string.IsNullOrEmpty(placeKey)) return;
        int currentId;
        ClientInfo current;
        if (AuthorityByPlace.TryGetValue(placeKey, out currentId) && ById.TryGetValue(currentId, out current) && current.Welcomed && !current.Suspended && current.Peer != null && current.PlaceKey == placeKey) return;
        int best = int.MaxValue;
        foreach (ClientInfo c in ById.Values)
            if (c.Welcomed && !c.Suspended && c.Peer != null && c.PlaceKey == placeKey && c.Id < best) best = c.Id;
        if (best == int.MaxValue)
        {
            AuthorityByPlace.Remove(placeKey);
            return;
        }
        AuthorityByPlace[placeKey] = best;
        Console.WriteLine("[AUTHORITY] " + PlaceLabel(placeKey) + " #" + best + " " + ById[best].Name);
        BroadcastExtensionToPlace(placeKey, "DAUTH|" + best, null, -1);
    }

    private static int GetAuthority(string placeKey)
    {
        int id;
        return !string.IsNullOrEmpty(placeKey) && AuthorityByPlace.TryGetValue(placeKey, out id) ? id : -1;
    }

    private static void SendCurrentRosterToAuthority(string placeKey)
    {
        int authority = GetAuthority(placeKey);
        if (authority <= 0) return;
        foreach (ClientInfo c in ById.Values)
            if (c.Welcomed && !c.Suspended && c.Peer != null && c.PlaceKey == placeKey && c.Id != authority)
                SendExtension(authority, "DSROSTER|" + c.Id + "|" + E(c.Name));
    }

    private static void RemoveSuspendedFromAuthoritySimulation(ClientInfo suspended)
    {
        if (suspended == null) return;
        int authority = GetAuthority(suspended.PlaceKey);
        ClientInfo a;
        if (authority <= 0 || authority == suspended.Id || !ById.TryGetValue(authority, out a) ||
            a == null || a.Suspended || a.Peer == null) return;
        try { a.Peer.Send(BuildPlayerLeft(suspended.Id), DeliveryMethod.ReliableOrdered); } catch { }
    }

    private static void RestoreResumedToAuthoritySimulation(ClientInfo resumed)
    {
        if (resumed == null || resumed.Suspended) return;
        int authority = GetAuthority(resumed.PlaceKey);
        if (authority > 0 && authority != resumed.Id)
            SendExtension(authority, "DSJOIN|" + resumed.Id + "|" + E(resumed.Name));
        if (resumed.LastPose != null)
            BroadcastToPlace(resumed.PlaceKey, BuildStateRelay(resumed.Id, resumed.Name, resumed.LastPose),
                DeliveryMethod.ReliableOrdered, resumed.Peer, resumed.Id);
    }

    private static int WelcomedCount(string placeKey)
    {

        int count = 0;
        foreach (ClientInfo c in ById.Values)
            if (c.Welcomed && !c.Suspended && c.Peer != null && c.PlaceKey == placeKey) count++;
        return count;
    }

    private static int SuspendedCount(string placeKey)
    {
        int count = 0;
        foreach (ClientInfo c in ById.Values)
            if (c.Welcomed && c.Suspended && c.PlaceKey == placeKey) count++;
        return count;
    }

    private static void SendPersistentWorld(ClientInfo c)
    {
        if (c == null || c.Peer == null) return;
        WorldStore w = GetWorld(c.PlaceKey, c.Mode == ModeExploration);
        if (w == null) return;
        foreach (string payload in w.Blocks.Values) c.Peer.Send(BuildExtension(payload), DeliveryMethod.ReliableOrdered);
        foreach (string payload in w.Objects.Values) c.Peer.Send(BuildExtension(payload), DeliveryMethod.ReliableOrdered);
        foreach (string payload in w.Entities.Values) c.Peer.Send(BuildExtension(payload), DeliveryMethod.ReliableOrdered);
    }

    private static void RememberWorldPayload(string placeKey, string payload)
    {
        if (string.IsNullOrEmpty(placeKey) || string.IsNullOrEmpty(payload)) return;
        bool persistent = placeKey == "plaza" || placeKey == "exploration";

        WorldStore w = GetWorld(placeKey, placeKey == "exploration");
        string[] p = payload.Split('|');
        if (p.Length == 0) return;
        bool changed = false;
        string op = p[0];
        if (op == "LAYERGO" && p.Length >= 3 && placeKey == "exploration")
        {
            int depth;
            if (int.TryParse(p[2], out depth))
            {
                w.BiomeDepth = Math.Max(0, depth);

                w.Blocks.Clear(); w.Objects.Clear(); w.Entities.Clear();
                changed = true;
            }
        }
        else if ((op == "WB" || op == "BSET") && p.Length >= 4)
        {
            string key = p[1] + "," + p[2];
            w.Blocks[key] = payload; changed = true;
        }
        else if ((op == "BSP" && p.Length >= 6) || (op == "BDP" && p.Length >= 2))
        {
            string key = op == "BSP" ? D(p[5]) : D(p[1]);
            if (!string.IsNullOrEmpty(key)) { w.Objects[key] = payload; changed = true; }
        }
        else if (op == "BDEL" && p.Length >= 2)
        {
            string key = D(p[1]);
            changed = w.Objects.Remove(key) || changed;
        }
        else if (op == "BCLR")
        {
            w.Objects.Clear(); changed = true;
        }
        else if (op == "WES" && p.Length >= 2)
        {
            w.Entities[p[1]] = payload; changed = true;
        }
        else if (op == "WED" && p.Length >= 2)
        {
            changed = w.Entities.Remove(p[1]) || changed;
        }
        if (changed)
        {
            w.Revision++;
            if (persistent) MarkWorldDirty(w);
        }
    }

    private static WorldStore GetWorld(string placeKey, bool persistentSeed)
    {
        WorldStore w;
        if (WorldByPlace.TryGetValue(placeKey, out w)) return w;
        w = new WorldStore { PlaceKey = placeKey, Seed = placeKey == "plaza" ? plazaSeed : new Random().Next(100000, int.MaxValue), BiomeDepth = 0 };
        WorldByPlace[placeKey] = w;
        if (persistentSeed) SaveWorld(w);
        return w;
    }

    private static void LoadWorld(string placeKey, bool persistentSeed)
    {
        WorldStore w = new WorldStore { PlaceKey = placeKey, Seed = placeKey == "plaza" ? plazaSeed : new Random().Next(100000, int.MaxValue), BiomeDepth = 0 };
        WorldByPlace[placeKey] = w;
        string dir = WorldDir(placeKey);
        Directory.CreateDirectory(dir);
        string meta = Path.Combine(dir, "world.meta");
        if (File.Exists(meta))
        {
            try
            {
                string[] m = File.ReadAllLines(meta);
                foreach (string line in m)
                {
                    if (line.StartsWith("seed=", StringComparison.Ordinal)) int.TryParse(line.Substring(5), out w.Seed);
                    else if (line.StartsWith("revision=", StringComparison.Ordinal)) int.TryParse(line.Substring(9), out w.Revision);
                    else if (line.StartsWith("biome=", StringComparison.Ordinal)) int.TryParse(line.Substring(6), out w.BiomeDepth);
                }
            }
            catch { }
        }
        string state = Path.Combine(dir, "world_state.txt");
        if (File.Exists(state))
        {
            try
            {
                foreach (string line in File.ReadAllLines(state))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string payload = Encoding.UTF8.GetString(Convert.FromBase64String(line));
                    string[] p = payload.Split('|');
                    if (p.Length == 0) continue;
                    if ((p[0] == "WB" || p[0] == "BSET") && p.Length >= 4) w.Blocks[p[1] + "," + p[2]] = payload;
                    else if ((p[0] == "BSP" && p.Length >= 6)) w.Objects[D(p[5])] = payload;
                    else if (p[0] == "BDP" && p.Length >= 2) w.Objects[D(p[1])] = payload;
                    else if (p[0] == "WES" && p.Length >= 2) w.Entities[p[1]] = payload;
                }
            }
            catch (Exception ex) { Console.WriteLine("[STATE] " + placeKey + " load failed: " + ex.Message); }
        }
        if (persistentSeed && !File.Exists(meta)) SaveWorld(w);
    }

    private static void SaveWorld(WorldStore w)
    {
        if (w == null) return;
        string dir = WorldDir(w.PlaceKey);
        Directory.CreateDirectory(dir);
        string metaTmp = Path.Combine(dir, "world.meta.tmp");
        string meta = Path.Combine(dir, "world.meta");
        File.WriteAllText(metaTmp, "seed=" + w.Seed + "\nrevision=" + w.Revision + "\nbiome=" + w.BiomeDepth + "\nprotocol=" + (w.PlaceKey == "exploration" ? ExplorationProtocol : LegacyProtocol) + "\n");
        ReplaceAtomic(metaTmp, meta);

        string stateTmp = Path.Combine(dir, "world_state.txt.tmp");
        string state = Path.Combine(dir, "world_state.txt");
        List<string> lines = new List<string>();
        foreach (string v in w.Blocks.Values) lines.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(v)));
        foreach (string v in w.Objects.Values) lines.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(v)));
        foreach (string v in w.Entities.Values) lines.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(v)));
        File.WriteAllLines(stateTmp, lines.ToArray());
        ReplaceAtomic(stateTmp, state);
        w.Dirty = false;
        w.SaveAfterUtc = DateTime.MinValue;
    }

    private static void MarkWorldDirty(WorldStore w)
    {
        if (w == null) return;
        w.Dirty = true;
        if (w.SaveAfterUtc == DateTime.MinValue) w.SaveAfterUtc = DateTime.UtcNow.AddSeconds(5);
    }

    private static void FlushDirtyWorlds(bool force)
    {
        DateTime now = DateTime.UtcNow;
        foreach (WorldStore w in WorldByPlace.Values)
        {
            if (w == null || !w.Dirty) continue;
            if (!force && w.SaveAfterUtc > now) continue;
            try { SaveWorld(w); } catch (Exception ex) { Console.WriteLine("[STATE] save failed " + w.PlaceKey + " · " + ex.Message); }
        }
    }

    private static string WorldDir(string placeKey)
    {
        if (placeKey == "exploration") return Path.Combine(ExplorationRoot, "world");
        if (placeKey == "plaza") return Path.Combine(DataRoot, "plaza");
        return Path.Combine(DataRoot, "rooms", SafeFile(placeKey));
    }

    private static void SendPlayerSave(ClientInfo c)
    {
        LoadPlayerMeta(c);
        string savePath = PlayerSavePath(c.PlayerUuid);
        if (!File.Exists(savePath))
        {
            SendExtension(c.Id, "EXPNEW|" + c.X.ToString("R", CultureInfo.InvariantCulture) + "|" + c.Y.ToString("R", CultureInfo.InvariantCulture));
            if (c.Dead && c.RespawnAtUnix > UnixNow()) SendExtension(c.Id, "EXPRESPAWNAT|" + c.RespawnAtUnix);
            return;
        }
        try
        {
            byte[] data = File.ReadAllBytes(savePath);
            const int chunk = 2700;
            int total = (data.Length + chunk - 1) / chunk;
            SendExtension(c.Id, "EXPLOADBEGIN|" + c.X.ToString("R", CultureInfo.InvariantCulture) + "|" + c.Y.ToString("R", CultureInfo.InvariantCulture) + "|" + total + "|" + (c.Dead ? "1" : "0") + "|" + c.RespawnAtUnix);
            for (int i = 0; i < total; i++)
            {
                int off = i * chunk;
                int len = Math.Min(chunk, data.Length - off);
                byte[] part = new byte[len];
                Buffer.BlockCopy(data, off, part, 0, len);
                SendExtension(c.Id, "EXPLOADCHUNK|" + i + "|" + Convert.ToBase64String(part));
            }
            SendExtension(c.Id, "EXPLOADEND");
            if (c.Dead && c.RespawnAtUnix > UnixNow()) SendExtension(c.Id, "EXPRESPAWNAT|" + c.RespawnAtUnix);
        }
        catch (Exception ex) { Console.WriteLine("[PLAYER] load failed " + c.PlayerUuid + " · " + ex.Message); }
    }

    private static void CommitPlayerSave(ClientInfo c)
    {
        if (c == null || c.Mode != ModeExploration || string.IsNullOrEmpty(c.PlayerUuid)) return;
        if (c.SaveRejected || c.SaveExpectedChunks < 0 || c.SaveChunks.Count != c.SaveExpectedChunks || c.SaveTotalBytes > MaxPlayerSaveBytes) return;
        try
        {
            using (MemoryStream ms = new MemoryStream())
            {
                for (int i = 0; i < c.SaveExpectedChunks; i++)
                {
                    byte[] part;
                    if (!c.SaveChunks.TryGetValue(i, out part)) return;
                    ms.Write(part, 0, part.Length);
                }
                string path = PlayerSavePath(c.PlayerUuid);
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, ms.ToArray());
                ReplaceAtomic(tmp, path);
            }
            SavePlayerMeta(c, true);
        }
        catch (Exception ex) { Console.WriteLine("[PLAYER] save failed " + c.PlayerUuid + " · " + ex.Message); }
        finally { c.SaveChunks.Clear(); c.SaveExpectedChunks = 0; c.SaveTotalBytes = 0; c.SaveRejected = false; }
    }

    private static void LoadPlayerMeta(ClientInfo c)
    {
        if (c == null || string.IsNullOrEmpty(c.PlayerUuid)) return;
        string path = PlayerMetaPath(c.PlayerUuid);
        if (!File.Exists(path)) return;
        try
        {
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('='); if (eq <= 0) continue;
                string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                if (k == "x") float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out c.X);
                else if (k == "y") float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out c.Y);
                else if (k == "dead") c.Dead = v == "1";
                else if (k == "respawnAt") long.TryParse(v, out c.RespawnAtUnix);
                else if (k == "hasDeathPosition") c.HasDeathPosition = v == "1";
                else if (k == "deathX") float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out c.DeathX);
                else if (k == "deathY") float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out c.DeathY);
                else if (k == "respawnAtLayerStart") c.RespawnAtLayerStart = v == "1";
            }

            if (c.Dead && !c.HasDeathPosition)
            {
                c.DeathX = c.X; c.DeathY = c.Y; c.HasDeathPosition = true;
            }
        }
        catch { }
    }

    private static void SavePlayerMeta(ClientInfo c, bool atomic)
    {
        if (c == null || string.IsNullOrEmpty(c.PlayerUuid)) return;
        Directory.CreateDirectory(PlayerRoot);
        string path = PlayerMetaPath(c.PlayerUuid);
        string tmp = path + ".tmp";
        string text =
            "name=" + E(c.Name) + "\n" +
            "x=" + c.X.ToString("R", CultureInfo.InvariantCulture) + "\n" +
            "y=" + c.Y.ToString("R", CultureInfo.InvariantCulture) + "\n" +
            "dead=" + (c.Dead ? "1" : "0") + "\n" +
            "respawnAt=" + c.RespawnAtUnix + "\n" +
            "hasDeathPosition=" + (c.HasDeathPosition ? "1" : "0") + "\n" +
            "deathX=" + c.DeathX.ToString("R", CultureInfo.InvariantCulture) + "\n" +
            "deathY=" + c.DeathY.ToString("R", CultureInfo.InvariantCulture) + "\n" +
            "respawnAtLayerStart=" + (c.RespawnAtLayerStart ? "1" : "0") + "\n" +
            "updated=" + UnixNow() + "\n";
        try
        {
            File.WriteAllText(tmp, text);
            ReplaceAtomic(tmp, path);
        }
        catch { }
    }

    private static string PlayerSavePath(string uuid) { return Path.Combine(PlayerRoot, SafeFile(uuid) + ".sav"); }
    private static string PlayerMetaPath(string uuid) { return Path.Combine(PlayerRoot, SafeFile(uuid) + ".meta"); }

    private static void ResetExplorationWorld()
    {
        WorldStore w = GetWorld("exploration", true);
        w.Seed = new Random().Next(100000, int.MaxValue);
        w.BiomeDepth = 0;
        w.Revision++;
        w.Blocks.Clear(); w.Objects.Clear(); w.Entities.Clear();
        SaveWorld(w);

        try
        {
            foreach (string meta in Directory.GetFiles(PlayerRoot, "*.meta"))
            {
                string[] lines = File.ReadAllLines(meta);
                List<string> output = new List<string>();
                foreach (string line in lines)
                {
                    if (line.StartsWith("x=", StringComparison.Ordinal)) output.Add("x=0");
                    else if (line.StartsWith("y=", StringComparison.Ordinal)) output.Add("y=0");
                    else output.Add(line);
                }
                File.WriteAllLines(meta, output.ToArray());
            }
        }
        catch { }
        foreach (ClientInfo c in ById.Values)
        {
            if (!c.Welcomed || c.Mode != ModeExploration) continue;
            c.X = 0f; c.Y = 0f;
            c.HasDeathPosition = false; c.DeathX = 0f; c.DeathY = 0f; c.RespawnAtLayerStart = false;
        }
        BroadcastExtensionToPlace("exploration", "EXPRESET|" + w.Seed + "|" + w.BiomeDepth, null, -1);
        AppendBridge("system", "exploration", "Exploration", "Server", "Exploration world was reset.");
        WriteStatus();
    }

    private static void LoadBridgeInboxOffset()
    {
        try
        {
            long value;
            if (File.Exists(ServerInboxOffsetFile) && long.TryParse(File.ReadAllText(ServerInboxOffsetFile).Trim(), out value) && value >= 0)
            {
                long length = File.Exists(InboxFile) ? new FileInfo(InboxFile).Length : 0;
                inboxOffset = Math.Min(value, length);
                return;
            }

            inboxOffset = File.Exists(InboxFile) ? new FileInfo(InboxFile).Length : 0;
            SaveBridgeInboxOffset();
        }
        catch { inboxOffset = 0; }
    }

    private static void SaveBridgeInboxOffset()
    {
        try
        {
            string tmp = ServerInboxOffsetFile + ".tmp";
            File.WriteAllText(tmp, Math.Max(0, inboxOffset).ToString(CultureInfo.InvariantCulture));
            ReplaceAtomic(tmp, ServerInboxOffsetFile);
        }
        catch { }
    }

    private static void PollBridgeInbox()
    {
        try
        {
            FileInfo fi = new FileInfo(InboxFile);
            if (!fi.Exists) return;
            if (inboxOffset > fi.Length)
            {
                inboxOffset = fi.Length;
                SaveBridgeInboxOffset();
                return;
            }
            using (FileStream fs = new FileStream(InboxFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(inboxOffset, SeekOrigin.Begin);
                using (MemoryStream lineBuffer = new MemoryStream())
                {
                    int value;
                    bool oversized = false;
                    while ((value = fs.ReadByte()) >= 0)
                    {
                        if (value != 10)
                        {
                            if (lineBuffer.Length < 131072) lineBuffer.WriteByte((byte)value);
                            else oversized = true;
                            continue;
                        }
                        if (!oversized)
                        {
                            string line = Encoding.UTF8.GetString(lineBuffer.ToArray()).TrimEnd('\r');
                            HandleBridgeLine(line);
                        }
                        inboxOffset = fs.Position;
                        SaveBridgeInboxOffset();
                        lineBuffer.SetLength(0);
                        oversized = false;
                    }
                }
            }
        }
        catch { }
    }

    private static void HandleBridgeLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        string[] p = line.Split('\t');
        if (p.Length == 0) return;
        if (p[0] == "CHAT" && p.Length >= 4)
        {
            string target = p[1];
            string author = B64D(p[2]);
            string text = B64D(p[3]);

            string payload = "CHAT|0|" + E("[Discord] " + author) + "|" + E(text);
            if (target == "*") BroadcastExtensionAll(payload);
            else BroadcastExtensionToPlace(target, payload, null, -1);
        }
        else if (p[0] == "CMD" && p.Length >= 3)
        {
            string target = p[1];
            string cmd = B64D(p[2]);
            SendDiscordCommand(target, cmd);
        }
        else if (p[0] == "EXPRESET") ResetExplorationWorld();
        else if (p[0] == "EXPADMIN" && p.Length >= 3)
        {
            int targetId;
            if (int.TryParse(p[1], out targetId)) SetExplorationAdmin(targetId, string.Equals(p[2], "grant", StringComparison.OrdinalIgnoreCase));
        }
        else if (p[0] == "MAP" && p.Length >= 6) HandleBridgeMapRequest(p);
        else if (p[0] == "MAPCAST" && p.Length >= 4) HandleBridgeMapCast(p);
    }

    private static void HandleBridgeMapRequest(string[] p)
    {
        string requestId = p.Length >= 2 ? p[1] : string.Empty;
        string place = p.Length >= 3 ? p[2] : string.Empty;
        int playerId, width, height;
        if (!IsSafeCaptureId(requestId) || !int.TryParse(p.Length >= 4 ? p[3] : "", out playerId) ||
            !int.TryParse(p.Length >= 5 ? p[4] : "", out width) || !int.TryParse(p.Length >= 6 ? p[5] : "", out height))
        {
            if (IsSafeCaptureId(requestId)) WriteMapError(requestId, "Invalid map capture request.");
            return;
        }
        width = Math.Max(640, Math.Min(1920, width));
        height = Math.Max(360, Math.Min(1080, height));
        ClientInfo target;
        if (!ById.TryGetValue(playerId, out target) || target == null || !target.Welcomed || target.Suspended || target.Peer == null ||
            !string.Equals(target.PlaceKey, place, StringComparison.OrdinalIgnoreCase))
        {
            WriteMapError(requestId, "Selected player is no longer active in that place.");
            return;
        }
        if (!target.MapCaptureReady)
        {
            WriteMapError(requestId, "Selected player is using a game build without /map capture support.");
            return;
        }
        MapCaptureRequests[requestId] = new MapCaptureRequest
        {
            Id = requestId,
            PlaceKey = place,
            PlayerId = playerId,
            ExpiresUtc = DateTime.UtcNow.AddSeconds(35)
        };
        SendExtension(playerId, "MAPCAP|" + requestId + "|" + width + "|" + height + "|72");
        Console.WriteLine("[MAP] request " + requestId + " -> " + PlaceLabel(place) + " #" + playerId);
    }

    private static void HandleBridgeMapCast(string[] p)
    {
        string action = p.Length >= 2 ? p[1] : string.Empty;
        string castId = p.Length >= 3 ? p[2] : string.Empty;
        string place = p.Length >= 4 ? p[3] : string.Empty;
        if (string.Equals(action, "STOPALL", StringComparison.OrdinalIgnoreCase))
        {
            foreach (KeyValuePair<string, MapCastSession> pair in MapCastSessions)
                BroadcastExtensionToPlace(pair.Value.PlaceKey, "MAPCAST|STOP|" + pair.Key, null, -1);
            MapCastSessions.Clear();
            return;
        }
        if (!IsSafeCaptureId(castId)) return;
        if (string.Equals(action, "STOP", StringComparison.OrdinalIgnoreCase))
        {
            MapCastSession existing;
            if (MapCastSessions.TryGetValue(castId, out existing))
            {
                BroadcastExtensionToPlace(existing.PlaceKey, "MAPCAST|STOP|" + castId, null, -1);
                MapCastSessions.Remove(castId);
            }
            return;
        }
        if (!string.Equals(action, "START", StringComparison.OrdinalIgnoreCase) || p.Length < 8) return;
        int width, height, fps, quality;
        if (!int.TryParse(p[4], out width) || !int.TryParse(p[5], out height) || !int.TryParse(p[6], out fps) || !int.TryParse(p[7], out quality)) return;
        width = Math.Max(640, Math.Min(1280, width));
        height = Math.Max(360, Math.Min(720, height));
        fps = Math.Max(2, Math.Min(10, fps));
        quality = Math.Max(35, Math.Min(80, quality));
        int authority = SelectMapCaptureProvider(place);
        if (authority <= 0)
        {
            WriteMapCastError(castId, "No active player in that place supports map capture yet.");
            return;
        }

        List<string> remove = new List<string>();
        foreach (KeyValuePair<string, MapCastSession> pair in MapCastSessions)
            if (string.Equals(pair.Value.PlaceKey, place, StringComparison.OrdinalIgnoreCase) && pair.Key != castId) remove.Add(pair.Key);
        foreach (string oldId in remove)
        {
            BroadcastExtensionToPlace(place, "MAPCAST|STOP|" + oldId, null, -1);
            MapCastSessions.Remove(oldId);
        }
        MapCastSession cast = new MapCastSession
        {
            Id = castId, PlaceKey = place, Width = width, Height = height, Fps = fps, Quality = quality, AuthorityId = authority
        };
        MapCastSessions[castId] = cast;
        Directory.CreateDirectory(Path.Combine(MapCastRoot, castId));
        SendExtension(authority, BuildMapCastStart(cast));
        Console.WriteLine("[MAPCAST] start " + castId + " · " + PlaceLabel(place) + " · authority #" + authority);
    }

    private static string BuildMapCastStart(MapCastSession cast)
    {
        return "MAPCAST|START|" + cast.Id + "|" + cast.Width + "|" + cast.Height + "|" + cast.Fps + "|" + cast.Quality;
    }

    private static bool HandleMapImagePayload(ClientInfo sender, string payload)
    {
        if (sender == null || string.IsNullOrEmpty(payload)) return false;
        if (!payload.StartsWith("MAPIMG_", StringComparison.Ordinal)) return false;
        string[] p = payload.Split('|');
        if (p.Length < 4) return true;
        string op = p[0];
        string kind = p[1];
        string id = p[2];
        int seq;
        if (!IsSafeCaptureId(id) || !int.TryParse(p[3], out seq) || seq < 0) return true;
        if (!IsMapImageSenderAllowed(sender, kind, id)) return true;
        string key = sender.Id + "|" + kind + "|" + id + "|" + seq;
        if (op == "MAPIMG_BEGIN" && p.Length >= 7)
        {
            int total, chunks;
            if (!string.Equals(p[4], "jpg", StringComparison.OrdinalIgnoreCase) || !int.TryParse(p[5], out total) || !int.TryParse(p[6], out chunks)) return true;
            int limit = string.Equals(kind, "cast", StringComparison.Ordinal) ? 1024 * 1024 : 4 * 1024 * 1024;
            if (total <= 0 || total > limit || chunks <= 0 || chunks > 4096) return true;
            MapImageAssemblies[key] = new MapImageAssembly
            {
                Kind = kind, Id = id, Sequence = seq, SenderId = sender.Id, TotalBytes = total, ChunkCount = chunks
            };
            return true;
        }
        if (op == "MAPIMG_CHUNK" && p.Length >= 6)
        {
            MapImageAssembly assembly;
            int index;
            if (!MapImageAssemblies.TryGetValue(key, out assembly) || !int.TryParse(p[4], out index) || index < 0 || index >= assembly.ChunkCount) return true;
            try
            {
                byte[] data = Convert.FromBase64String(p[5]);
                if (data.Length > 4096) return true;
                assembly.Chunks[index] = data;
            }
            catch { }
            return true;
        }
        if (op == "MAPIMG_END")
        {
            MapImageAssembly assembly;
            if (!MapImageAssemblies.TryGetValue(key, out assembly)) return true;
            MapImageAssemblies.Remove(key);
            if (assembly.Chunks.Count != assembly.ChunkCount) return true;
            try
            {
                using (MemoryStream ms = new MemoryStream(assembly.TotalBytes))
                {
                    for (int i = 0; i < assembly.ChunkCount; i++)
                    {
                        byte[] part;
                        if (!assembly.Chunks.TryGetValue(i, out part)) return true;
                        ms.Write(part, 0, part.Length);
                    }
                    byte[] image = ms.ToArray();
                    if (image.Length != assembly.TotalBytes || image.Length < 4 || image[0] != 0xFF || image[1] != 0xD8 || image[2] != 0xFF) return true;
                    if (string.Equals(kind, "map", StringComparison.Ordinal))
                    {
                        MapCaptureRequest req;
                        if (!MapCaptureRequests.TryGetValue(id, out req) || req.PlayerId != sender.Id) return true;
                        WriteAtomicBytes(Path.Combine(MapResultsRoot, id + ".jpg"), image);
                        MapCaptureRequests.Remove(id);
                    }
                    else if (string.Equals(kind, "cast", StringComparison.Ordinal))
                    {
                        MapCastSession cast;
                        if (!MapCastSessions.TryGetValue(id, out cast) || cast.AuthorityId != sender.Id) return true;
                        string dir = Path.Combine(MapCastRoot, id);
                        Directory.CreateDirectory(dir);
                        WriteAtomicBytes(Path.Combine(dir, "latest.jpg"), image);
                        WriteAtomicText(Path.Combine(dir, "latest.seq"), seq.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
            catch { }
            return true;
        }
        return true;
    }

    private static bool IsMapImageSenderAllowed(ClientInfo sender, string kind, string id)
    {
        if (sender == null || sender.Suspended || sender.Peer == null) return false;
        if (string.Equals(kind, "map", StringComparison.Ordinal))
        {
            MapCaptureRequest request;
            return MapCaptureRequests.TryGetValue(id, out request) && request.PlayerId == sender.Id &&
                string.Equals(request.PlaceKey, sender.PlaceKey, StringComparison.OrdinalIgnoreCase);
        }
        if (string.Equals(kind, "cast", StringComparison.Ordinal))
        {
            MapCastSession cast;
            return MapCastSessions.TryGetValue(id, out cast) && cast.AuthorityId == sender.Id &&
                string.Equals(cast.PlaceKey, sender.PlaceKey, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static void MaintainMapCaptureBridge()
    {
        DateTime now = DateTime.UtcNow;
        List<string> expiredRequests = new List<string>();
        foreach (KeyValuePair<string, MapCaptureRequest> pair in MapCaptureRequests)
            if (now >= pair.Value.ExpiresUtc) expiredRequests.Add(pair.Key);
        foreach (string id in expiredRequests)
        {
            WriteMapError(id, "Map capture timed out on the selected game client.");
            MapCaptureRequests.Remove(id);
        }

        List<string> staleAssemblies = new List<string>();
        foreach (KeyValuePair<string, MapImageAssembly> pair in MapImageAssemblies)
            if ((now - pair.Value.CreatedUtc).TotalSeconds > 20) staleAssemblies.Add(pair.Key);
        foreach (string key in staleAssemblies) MapImageAssemblies.Remove(key);

        List<string> stopCasts = new List<string>();
        foreach (KeyValuePair<string, MapCastSession> pair in MapCastSessions)
        {
            MapCastSession cast = pair.Value;
            int authority = SelectMapCaptureProvider(cast.PlaceKey);
            if (authority <= 0)
            {
                WriteMapCastError(cast.Id, "No active map-capture capable player remains in the place.");
                stopCasts.Add(cast.Id);
                continue;
            }
            if (authority != cast.AuthorityId)
            {
                BroadcastExtensionToPlace(cast.PlaceKey, "MAPCAST|STOP|" + cast.Id, null, -1);
                cast.AuthorityId = authority;
                SendExtension(authority, BuildMapCastStart(cast));
                Console.WriteLine("[MAPCAST] authority moved -> #" + authority + " · " + PlaceLabel(cast.PlaceKey));
            }
        }
        foreach (string id in stopCasts) MapCastSessions.Remove(id);
    }

    private static int SelectMapCaptureProvider(string place)
    {
        EnsureAuthority(place);
        int authority = GetAuthority(place);
        ClientInfo candidate;
        if (authority > 0 && ById.TryGetValue(authority, out candidate) && candidate != null && candidate.Welcomed &&
            !candidate.Suspended && candidate.Peer != null && candidate.MapCaptureReady &&
            string.Equals(candidate.PlaceKey, place, StringComparison.OrdinalIgnoreCase)) return authority;
        int best = int.MaxValue;
        foreach (ClientInfo c in ById.Values)
        {
            if (c == null || !c.Welcomed || c.Suspended || c.Peer == null || !c.MapCaptureReady ||
                !string.Equals(c.PlaceKey, place, StringComparison.OrdinalIgnoreCase)) continue;
            if (c.Id < best) best = c.Id;
        }
        return best == int.MaxValue ? -1 : best;
    }

    private static bool IsSafeCaptureId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 8 || value.Length > 64) return false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;
        }
        return true;
    }

    private static void WriteMapError(string requestId, string message)
    {
        if (!IsSafeCaptureId(requestId)) return;
        string json = "{\"status\":\"error\",\"message\":\"" + JsonEscape(message) + "\"}";
        WriteAtomicText(Path.Combine(MapResultsRoot, requestId + ".json"), json);
    }

    private static void WriteMapCastError(string castId, string message)
    {
        if (!IsSafeCaptureId(castId)) return;
        string dir = Path.Combine(MapCastRoot, castId);
        try { Directory.CreateDirectory(dir); } catch { return; }
        WriteAtomicText(Path.Combine(dir, "error.txt"), message ?? "Map cast error");
    }

    private static string JsonEscape(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    }

    private static void WriteAtomicBytes(string path, byte[] data)
    {
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            ReplaceAtomic(tmp, path);
        }
        catch { }
    }

    private static void WriteAtomicText(string path, string text)
    {
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text ?? string.Empty);
            ReplaceAtomic(tmp, path);
        }
        catch { }
    }

    private static void CompleteImmediateSoloExplorationRespawn(ClientInfo c)
    {
        if (c == null || !c.Welcomed || c.Mode != ModeExploration) return;

        c.Dead = false;
        c.RespawnAtUnix = 0;
        c.RespawnAtLayerStart = false;
        c.HasDeathPosition = false;
        SavePlayerMeta(c, true);

        SendExtension(c.Id, "ACTION|" + c.Id + "|REVIVE||1");
        SendExtension(c.Id, "EXPRESPAWNSTART");
        BroadcastExtensionToPlace(c.PlaceKey, "PDEAD|" + c.Id + "|0", null, -1);
        AppendEvent("revive", c.PlaceKey, c.PlaceLabel, c.Name + " immediately respawned at the layer start (alone)");
    }

    private static bool HasOtherExplorationPlayer(ClientInfo target)
    {
        foreach (ClientInfo other in ById.Values)
        {
            if (other == target || !other.Welcomed || other.Mode != ModeExploration) continue;
            return true;
        }
        return false;
    }

    private static void LoadExplorationAdmins()
    {
        ExplorationAdmins.Clear();
        try
        {
            if (!File.Exists(ExplorationAdminsFile)) return;
            foreach (string raw in File.ReadAllLines(ExplorationAdminsFile))
            {
                string uuid = NormalizeUuid(raw);
                if (!string.IsNullOrEmpty(uuid)) ExplorationAdmins.Add(uuid);
            }
        }
        catch { }
    }

    private static void SaveExplorationAdmins()
    {
        try
        {
            string tmp = ExplorationAdminsFile + ".tmp";
            List<string> rows = new List<string>(ExplorationAdmins);
            rows.Sort(StringComparer.OrdinalIgnoreCase);
            File.WriteAllLines(tmp, rows.ToArray());
            ReplaceAtomic(tmp, ExplorationAdminsFile);
        }
        catch { }
    }

    private static void SendExplorationAdminSnapshot(ClientInfo receiver)
    {
        if (receiver == null || !receiver.Welcomed || receiver.Mode != ModeExploration) return;
        foreach (ClientInfo c in ById.Values)
            if (c.Welcomed && c.Mode == ModeExploration && c.ExplorationAdmin)
                SendExtension(receiver.Id, "ADM|" + c.Id + "|1");
    }

    private static void SetExplorationAdmin(int targetId, bool grant)
    {
        ClientInfo target;
        if (!ById.TryGetValue(targetId, out target) || !target.Welcomed || target.Mode != ModeExploration || string.IsNullOrEmpty(target.PlayerUuid))
        {
            AppendBridge("admin_change", "exploration", "Exploration", "Server", "Admin change failed: target is not connected to Exploration.");
            return;
        }
        if (grant) ExplorationAdmins.Add(target.PlayerUuid); else ExplorationAdmins.Remove(target.PlayerUuid);
        target.ExplorationAdmin = grant;
        SaveExplorationAdmins();
        BroadcastExtensionToPlace("exploration", "ADM|" + target.Id + "|" + (grant ? "1" : "0"), null, -1);
        AppendEvent("admin_change", "exploration", "Exploration", target.Name + (grant ? " was granted persistent admin" : " had persistent admin revoked"));
        WriteStatus();
    }

    private static bool TryHandlePersistentExplorationAdminCommand(string clean)
    {
        if (string.IsNullOrWhiteSpace(clean)) return false;
        string[] bits = clean.Trim().Split(new char[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
        if (bits.Length < 2) return false;
        bool grant;
        if (string.Equals(bits[0], "admin", StringComparison.OrdinalIgnoreCase)) grant = true;
        else if (string.Equals(bits[0], "unadmin", StringComparison.OrdinalIgnoreCase)) grant = false;
        else return false;

        string who = bits[1].Trim();
        int id;
        if (who.StartsWith("#", StringComparison.Ordinal)) who = who.Substring(1);
        ClientInfo target = null;
        if (int.TryParse(who, out id))
        {
            ClientInfo byId;
            if (ById.TryGetValue(id, out byId) && byId.Welcomed && byId.Mode == ModeExploration) target = byId;
        }
        if (target == null)
        {
            foreach (ClientInfo c in ById.Values)
            {
                if (!c.Welcomed || c.Mode != ModeExploration) continue;
                if (string.Equals(c.Name, who, StringComparison.OrdinalIgnoreCase)) { target = c; break; }
            }
        }
        if (target == null)
        {
            AppendBridge("admin_change", "exploration", "Exploration", "Server", "Admin change failed: Exploration player not found: " + who);
            return true;
        }
        SetExplorationAdmin(target.Id, grant);
        return true;
    }

    private static void SendDiscordCommand(string target, string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        if (target == "*")
        {
            List<string> places = ActivePlaceKeys();
            foreach (string place in places) SendDiscordCommand(place, command);
            return;
        }
        int authority = GetAuthority(target);
        if (authority <= 0) return;
        string clean = command.Trim();
        if (target == "exploration")
        {

            if (TryHandlePersistentExplorationAdminCommand(clean)) return;
            SendExtension(authority, "DCMD|" + E(clean));
        }
        else
        {

            SendExtension(authority, "DSREQ|" + authority + "|CMD|" + E(clean));
        }
        AppendBridge("admin_command", target, PlaceLabel(target), "Discord Admin", clean);
    }

    private static void AppendEvent(string kind, string placeKey, string placeLabel, string message)
    {
        string ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        try { File.AppendAllText(EventsFile, ts + "\t" + kind + "\t[" + placeLabel + "]\t" + message.Replace("\r", " ").Replace("\n", " ") + "\n"); } catch { }
        AppendBridge(kind, placeKey, placeLabel, "Server", message);
    }

    private static void AppendBridge(string kind, string placeKey, string placeLabel, string author, string text)
    {
        try
        {
            string line = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\t" +
                kind + "\t" + (placeKey ?? string.Empty) + "\t" + B64(placeLabel) + "\t" + B64(author) + "\t" + B64(text) + "\n";
            File.AppendAllText(OutboxFile, line);
        }
        catch { }
    }

    private static void WriteStatus()
    {
        try
        {
            StringBuilder b = new StringBuilder();
            b.Append("server\t").Append(UnixNow()).Append('\t').Append(manager != null && manager.IsRunning ? "1" : "0").Append('\n');
            List<string> places = ActivePlaceKeys();

            if (!places.Contains("plaza")) places.Insert(0, "plaza");
            if (!places.Contains("exploration")) places.Add("exploration");
            foreach (string place in places)
            {
                byte mode = PlaceMode(place);
                b.Append("place\t").Append(place).Append('\t').Append(B64(PlaceLabel(place))).Append('\t')
                    .Append(WelcomedCount(place)).Append('\t').Append(mode).Append('\t')
                    .Append(SuspendedCount(place)).Append('\n');
            }
            foreach (ClientInfo c in ById.Values)
            {
                if (!c.Welcomed) continue;
                int ping = -1;
                try { ping = c.Peer != null ? c.Peer.Ping : -1; } catch { }
                b.Append("player\t").Append(c.PlaceKey).Append('\t').Append(c.Id).Append('\t')
                    .Append(B64(c.Name)).Append('\t').Append(ping).Append('\t')
                    .Append(c.Mode == ModeExploration && c.ExplorationAdmin ? "1" : "0").Append('\t')
                    .Append(c.Mode == ModeExploration ? c.PlayerUuid : string.Empty).Append('\t')
                    .Append(c.Suspended ? "1" : "0").Append('\n');
            }
            string tmp = StatusFile + ".tmp";
            File.WriteAllText(tmp, b.ToString());
            ReplaceAtomic(tmp, StatusFile);
        }
        catch { }
    }

    private static List<string> ActivePlaceKeys()
    {
        List<string> result = new List<string>();
        foreach (ClientInfo c in ById.Values)
            if (c.Welcomed && !result.Contains(c.PlaceKey)) result.Add(c.PlaceKey);
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static byte PlaceMode(string place)
    {
        if (place == "plaza") return ModePlaza;
        if (place == "exploration") return ModeExploration;
        return ModeInternet;
    }

    private static string PlaceLabel(string place)
    {
        if (place == "plaza") return "Plaza";
        if (place == "exploration") return "Exploration";
        return place != null && place.StartsWith("room:", StringComparison.OrdinalIgnoreCase) ? place.Substring(5) : (place ?? "Room");
    }

    private static ClientInfo FirstClient(string place)
    {
        foreach (ClientInfo c in ById.Values) if (c.Welcomed && c.PlaceKey == place) return c;
        return null;
    }

    private static void SendExistingPoses(ClientInfo target)
    {
        if (target == null || target.Peer == null) return;
        foreach (ClientInfo c in ById.Values)
            if (c.Welcomed && c.PlaceKey == target.PlaceKey && c.LastPose != null)
                target.Peer.Send(BuildStateRelay(c.Id, c.Name, c.LastPose), DeliveryMethod.ReliableOrdered);
    }

    private static void BroadcastToPlace(string place, byte[] data, DeliveryMethod method, NetPeer exceptPeer, int exceptId)
    {
        foreach (ClientInfo c in ById.Values)
            if (c.Welcomed && !c.Suspended && c.PlaceKey == place && c.Peer != null && c.Peer != exceptPeer && c.Id != exceptId)
                c.Peer.Send(data, method);
    }

    private static void BroadcastExtensionToPlace(string place, string payload, NetPeer exceptPeer, int exceptId)
    {
        BroadcastToPlace(place, BuildExtension(payload), DeliveryMethod.ReliableOrdered, exceptPeer, exceptId);
    }

    private static void BroadcastExtensionAll(string payload)
    {
        byte[] data = BuildExtension(payload);
        foreach (ClientInfo c in ById.Values) if (c.Welcomed && !c.Suspended && c.Peer != null) c.Peer.Send(data, DeliveryMethod.ReliableOrdered);
    }

    private static void SendExtension(int id, string payload)
    {
        ClientInfo c;
        if (id <= 0 || !ById.TryGetValue(id, out c) || !c.Welcomed || c.Suspended || c.Peer == null) return;
        c.Peer.Send(BuildExtension(payload), DeliveryMethod.ReliableOrdered);
    }

    private static void SendExtensionIfPlace(int id, string place, string payload)
    {
        ClientInfo c;
        if (id <= 0 || !ById.TryGetValue(id, out c) || !c.Welcomed || c.Suspended || c.PlaceKey != place || c.Peer == null) return;
        c.Peer.Send(BuildExtension(payload), DeliveryMethod.ReliableOrdered);
    }

    private static byte[] BuildRoomListResponse()
    {
        List<string> rooms = new List<string>();
        foreach (ClientInfo c in ById.Values)
        {
            if (!c.Welcomed || c.Mode != ModeInternet || string.IsNullOrEmpty(c.PlaceKey)) continue;
            if (!rooms.Contains(c.PlaceKey)) rooms.Add(c.PlaceKey);
        }
        rooms.Sort(StringComparer.OrdinalIgnoreCase);
        return Build(delegate(BinaryWriter w)
        {
            w.Write((byte)MessageType.RoomListResponse);
            w.Write(LegacyProtocol);
            w.Write(rooms.Count);
            foreach (string key in rooms)
            {
                WriteSafeString(w, PlaceLabel(key), 40);
                w.Write(WelcomedCount(key));
                WriteSafeString(w, SceneName, 80);
            }

            w.Write(WelcomedCount("plaza"));
            w.Write(WelcomedCount("exploration"));
        });
    }

    private static byte[] BuildWelcome(ClientInfo c)
    {
        int seed = c.Mode == ModeExploration ? GetWorld("exploration", true).Seed : (c.Mode == ModePlaza ? plazaSeed : GetWorld(c.PlaceKey, false).Seed);
        return Build(delegate(BinaryWriter w)
        {
            w.Write((byte)MessageType.Welcome);
            w.Write(LegacyProtocol);
            w.Write(c.Id);
            w.Write(seed);
            w.Write(c.Mode);
            WriteSafeString(w, SceneName, 80);
        });
    }

    private static byte[] BuildStateRelay(int id, string name, byte[] pose)
    {
        return Build(delegate(BinaryWriter w)
        {
            w.Write((byte)MessageType.PlayerStateRelay);
            w.Write(id); WriteSafeString(w, name, 24); if (pose != null) w.Write(pose);
        });
    }

    private static byte[] BuildPlayerLeft(int id)
    {
        return Build(delegate(BinaryWriter w) { w.Write((byte)MessageType.PlayerLeft); w.Write(id); });
    }

    private static byte[] BuildExtension(string payload)
    {
        return Build(delegate(BinaryWriter w) { w.Write((byte)MessageType.Extension); WriteSafeString(w, payload, 8192); });
    }

    private static byte[] BuildError(string message)
    {
        return Build(delegate(BinaryWriter w) { w.Write((byte)MessageType.Error); WriteSafeString(w, message, 200); });
    }

    private static byte[] Build(Action<BinaryWriter> action)
    {
        using (MemoryStream ms = new MemoryStream(512))
        using (BinaryWriter w = new BinaryWriter(ms))
        {
            action(w); w.Flush(); return ms.ToArray();
        }
    }

    private static void WriteSafeString(BinaryWriter writer, string value, int max)
    {
        string safe = value ?? string.Empty; if (safe.Length > max) safe = safe.Substring(0, max); writer.Write(safe);
    }

    private static string ReadSafeString(BinaryReader reader, int max)
    {
        int length = 0;
        int shift = 0;
        for (int i = 0; i < 5; i++)
        {
            byte part = reader.ReadByte();
            if (i == 4 && (part & 0xF0) != 0) throw new InvalidDataException("Invalid string length.");
            length |= (part & 0x7F) << shift;
            if ((part & 0x80) == 0)
            {
                if (length < 0 || length > max * 4 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                    throw new InvalidDataException("String exceeds packet limit.");
                byte[] data = reader.ReadBytes(length);
                if (data.Length != length) throw new EndOfStreamException();
                string value = Encoding.UTF8.GetString(data);
                return value.Length <= max ? value : value.Substring(0, max);
            }
            shift += 7;
        }
        throw new InvalidDataException("Invalid string length prefix.");
    }

    private static string NormalizeName(string value)
    {
        string safe = string.IsNullOrWhiteSpace(value) ? "Player" : value.Trim();
        try { safe = safe.Normalize(NormalizationForm.FormC); } catch { }
        safe = safe.Replace("\u200B", string.Empty).Replace("\u200C", string.Empty).Replace("\u200D", string.Empty).Replace("\uFEFF", string.Empty);
        if (safe.Length > 24) safe = safe.Substring(0, 24); return safe;
    }

    private static string NormalizeUuid(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        StringBuilder b = new StringBuilder();
        foreach (char c in value) if (char.IsLetterOrDigit(c) || c == '-' || c == '_') b.Append(c);
        string s = b.ToString(); if (s.Length > 80) s = s.Substring(0, 80); return s;
    }

    private static string NormalizeRoom(string value)
    {
        string s = string.IsNullOrWhiteSpace(value) ? "HG Room" : value.Trim().Replace("\r", " ").Replace("\n", " ").Replace("|", " ");
        if (s.Length > 40) s = s.Substring(0, 40); return s;
    }

    private static string SafeFile(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        StringBuilder b = new StringBuilder();
        foreach (char c in value) if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') b.Append(c); else b.Append('_');
        string s = b.ToString(); if (s.Length > 96) s = s.Substring(0, 96); return s;
    }

    private static string E(string value) { try { return Uri.EscapeDataString(value ?? string.Empty); } catch { return string.Empty; } }
    private static string D(string value) { try { return Uri.UnescapeDataString(value ?? string.Empty); } catch { return value ?? string.Empty; } }
    private static string B64(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty)); }
    private static string B64D(string value) { try { return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty)); } catch { return string.Empty; } }
    private static long UnixNow()
    {
        DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return (long)(DateTime.UtcNow - epoch).TotalSeconds;
    }

    [DllImport("libc", EntryPoint = "rename", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int PosixRename(string oldPath, string newPath);

    private static void ReplaceAtomic(string tmp, string target)
    {
        if (!File.Exists(tmp)) throw new FileNotFoundException("Replacement source is missing.", tmp);
        if (!File.Exists(target))
        {
            File.Move(tmp, target);
            return;
        }
        string backup = target + ".bak";
        try
        {
            File.Replace(tmp, target, backup);
            return;
        }
        catch (PlatformNotSupportedException) { }
        catch (NotSupportedException) { }
        catch (IOException) { }
        if (!File.Exists(tmp)) throw new IOException("Replacement source disappeared before fallback.");
        File.Copy(target, backup, true);
        int result = PosixRename(tmp, target);
        if (result != 0) throw new IOException("Atomic rename failed (errno " + Marshal.GetLastWin32Error() + ").");
    }

    private static void Touch(string path)
    {
        if (!File.Exists(path)) using (File.Create(path)) { }
    }
}
