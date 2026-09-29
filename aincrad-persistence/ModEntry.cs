using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace AincradPersistence;

public sealed class ModConfig
{
    public bool Enabled { get; set; } = true;
    public int SnapshotIntervalSeconds { get; set; } = 5;
    public int DiskFlushIntervalSeconds { get; set; } = 20;
    public int RestoreDelayMilliseconds { get; set; } = 1800;
    public bool RestoreFacingDirection { get; set; } = true;
    public bool RestoreAcrossDifferentDays { get; set; } = true;
    public bool FallbackToLastSafePosition { get; set; } = true;
    public bool SkipRestoreDuringEvents { get; set; } = true;
    public List<string> UnsafeLocationPrefixes { get; set; } = new()
    {
        "Temp",
        "Festival",
        "UndergroundMine",
        "VolcanoDungeon"
    };
    public List<string> UnsafeLocationNames { get; set; } = new();
    public bool VerboseLogging { get; set; } = false;
}

public sealed class PlayerPosition
{
    public string PlayerName { get; set; } = "";
    public long PlayerId { get; set; }
    public string LocationName { get; set; } = "";
    public int TileX { get; set; }
    public int TileY { get; set; }
    public int FacingDirection { get; set; } = 2;
    public uint DaysPlayed { get; set; }
    public bool WasDuringEvent { get; set; }
    public DateTime SavedAtUtc { get; set; }
}

public sealed class PlayerRecord
{
    public PlayerPosition? LastKnown { get; set; }
    public PlayerPosition? LastSafe { get; set; }
}

public sealed class PersistenceStore
{
    public Dictionary<string, PlayerRecord> Players { get; set; } = new();
}

public sealed class ReadyPacket
{
    public long PlayerId { get; set; }
}

public sealed class RestorePacket
{
    public long PlayerId { get; set; }
    public string LocationName { get; set; } = "";
    public int TileX { get; set; }
    public int TileY { get; set; }
    public int FacingDirection { get; set; } = 2;
}

public sealed class ModEntry : Mod
{
    private const string DataFile = "player-state.json";
    private const string MessageReady = "PersistenceReady";
    private const string MessageRestore = "RestorePosition";

    private ModConfig Config = new();
    private PersistenceStore Store = new();
    private DateTime LastSnapshotUtc = DateTime.MinValue;
    private DateTime LastFlushUtc = DateTime.MinValue;
    private DateTime ClientReadyAtUtc = DateTime.MaxValue;
    private bool ClientReadySent;
    private RestorePacket? PendingRestore;
    private DateTime PendingRestoreAtUtc = DateTime.MaxValue;
    private bool Dirty;

    public override void Entry(IModHelper helper)
    {
        Config = helper.ReadConfig<ModConfig>();

        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Multiplayer.PeerDisconnected += OnPeerDisconnected;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;

        helper.ConsoleCommands.Add(
            "aincrad_persistence_status",
            "Show Aincrad Persistence status.",
            (_, _) => LogStatus()
        );
        helper.ConsoleCommands.Add(
            "aincrad_persistence_save",
            "Capture and flush player positions immediately (host only).",
            (_, _) => ForceSave()
        );

        Monitor.Log("Aincrad Persistence loaded.", LogLevel.Info);
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        PendingRestore = null;
        PendingRestoreAtUtc = DateTime.MaxValue;
        ClientReadySent = false;

        if (!Config.Enabled)
            return;

        if (Context.IsMainPlayer)
        {
            Store = Helper.Data.ReadJsonFile<PersistenceStore>(DataFile) ?? new PersistenceStore();
            LastSnapshotUtc = DateTime.MinValue;
            LastFlushUtc = DateTime.UtcNow;
            Dirty = false;
            Monitor.Log($"Loaded persistence data for {Store.Players.Count} player(s).", LogLevel.Info);
        }
        else
        {
            ClientReadyAtUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(500, Config.RestoreDelayMilliseconds));
        }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Config.Enabled || !Context.IsWorldReady)
            return;

        DateTime now = DateTime.UtcNow;

        if (Context.IsMainPlayer)
        {
            if ((now - LastSnapshotUtc).TotalSeconds >= Math.Max(2, Config.SnapshotIntervalSeconds))
            {
                CaptureOnlinePlayers();
                LastSnapshotUtc = now;
            }

            if (Dirty && (now - LastFlushUtc).TotalSeconds >= Math.Max(5, Config.DiskFlushIntervalSeconds))
                FlushToDisk();
        }
        else
        {
            if (!ClientReadySent && now >= ClientReadyAtUtc)
            {
                ClientReadySent = true;
                Helper.Multiplayer.SendMessage(
                    new ReadyPacket { PlayerId = Game1.player.UniqueMultiplayerID },
                    MessageReady,
                    new[] { ModManifest.UniqueID }
                );

                if (Config.VerboseLogging)
                    Monitor.Log("Requested saved position from host.", LogLevel.Trace);
            }

            if (PendingRestore is not null && now >= PendingRestoreAtUtc)
                TryApplyPendingRestore();
        }
    }

    private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        if (!Config.Enabled || !Context.IsMainPlayer || !Context.IsWorldReady)
            return;

        Farmer? farmer = Game1.getFarmer(e.Peer.PlayerID);
        if (farmer is not null)
            CaptureFarmer(farmer);

        FlushToDisk();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        if (Config.Enabled && Context.IsMainPlayer && Dirty)
            FlushToDisk();

        PendingRestore = null;
        ClientReadySent = false;
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (!Config.Enabled || e.FromModID != ModManifest.UniqueID)
            return;

        if (Context.IsMainPlayer && e.Type == MessageReady)
        {
            ReadyPacket request = e.ReadAs<ReadyPacket>();
            if (request.PlayerId != e.FromPlayerID)
                return;

            SendRestoreToPlayer(e.FromPlayerID);
            return;
        }

        if (!Context.IsMainPlayer && e.Type == MessageRestore)
        {
            IMultiplayerPeer? senderPeer = Helper.Multiplayer.GetConnectedPlayer(e.FromPlayerID);
            if (senderPeer?.IsHost != true)
                return;

            RestorePacket packet = e.ReadAs<RestorePacket>();
            if (packet.PlayerId != Game1.player.UniqueMultiplayerID)
                return;

            PendingRestore = packet;
            PendingRestoreAtUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(250, Config.RestoreDelayMilliseconds));
        }
    }

    private void CaptureOnlinePlayers()
    {
        foreach (Farmer farmer in Game1.getOnlineFarmers())
            CaptureFarmer(farmer);
    }

    private void CaptureFarmer(Farmer farmer)
    {
        if (farmer.currentLocation is null || string.IsNullOrWhiteSpace(farmer.currentLocation.NameOrUniqueName))
            return;

        if (!TryGetTile(farmer, out int tileX, out int tileY))
            return;

        string key = farmer.UniqueMultiplayerID.ToString();
        if (!Store.Players.TryGetValue(key, out PlayerRecord? record))
        {
            record = new PlayerRecord();
            Store.Players[key] = record;
        }

        bool duringEvent = farmer.currentLocation.currentEvent is not null;
        var position = new PlayerPosition
        {
            PlayerName = farmer.Name ?? "",
            PlayerId = farmer.UniqueMultiplayerID,
            LocationName = farmer.currentLocation.NameOrUniqueName,
            TileX = tileX,
            TileY = tileY,
            FacingDirection = Math.Clamp(farmer.FacingDirection, 0, 3),
            DaysPlayed = Game1.stats.DaysPlayed,
            WasDuringEvent = duringEvent,
            SavedAtUtc = DateTime.UtcNow
        };

        record.LastKnown = position;

        if (!duringEvent && IsLocationNameSafe(position.LocationName))
            record.LastSafe = Clone(position);

        Dirty = true;
    }

    private void SendRestoreToPlayer(long playerId)
    {
        if (!Store.Players.TryGetValue(playerId.ToString(), out PlayerRecord? record))
        {
            if (Config.VerboseLogging)
                Monitor.Log($"No saved persistence position for player {playerId}.", LogLevel.Trace);
            return;
        }

        PlayerPosition? chosen = ChooseRestorePosition(record);
        if (chosen is null)
        {
            Monitor.Log($"No safe restore position for {record.LastKnown?.PlayerName ?? playerId.ToString()}; vanilla spawn will be used.", LogLevel.Info);
            return;
        }

        var packet = new RestorePacket
        {
            PlayerId = playerId,
            LocationName = chosen.LocationName,
            TileX = chosen.TileX,
            TileY = chosen.TileY,
            FacingDirection = chosen.FacingDirection
        };

        Helper.Multiplayer.SendMessage(
            packet,
            MessageRestore,
            new[] { ModManifest.UniqueID },
            new[] { playerId }
        );

        Monitor.Log($"Queued MMO restore for {chosen.PlayerName} at {chosen.LocationName} ({chosen.TileX}, {chosen.TileY}).", LogLevel.Info);
    }

    private PlayerPosition? ChooseRestorePosition(PlayerRecord record)
    {
        bool Valid(PlayerPosition? p)
        {
            if (p is null || p.WasDuringEvent || !IsLocationNameSafe(p.LocationName))
                return false;

            if (!Config.RestoreAcrossDifferentDays && p.DaysPlayed != Game1.stats.DaysPlayed)
                return false;

            return true;
        }

        if (Valid(record.LastKnown))
            return record.LastKnown;

        if (Config.FallbackToLastSafePosition && Valid(record.LastSafe))
            return record.LastSafe;

        return null;
    }

    private void TryApplyPendingRestore()
    {
        if (PendingRestore is null || !Context.IsWorldReady)
            return;

        if (Config.SkipRestoreDuringEvents && (Game1.eventUp || Game1.currentLocation?.currentEvent is not null))
        {
            PendingRestoreAtUtc = DateTime.UtcNow.AddMilliseconds(1000);
            return;
        }

        RestorePacket packet = PendingRestore;

        if (!IsLocationNameSafe(packet.LocationName))
        {
            PendingRestore = null;
            Monitor.Log($"Skipped unsafe persistence restore target '{packet.LocationName}'.", LogLevel.Warn);
            return;
        }

        try
        {
            GameLocation? target = Game1.getLocationFromName(packet.LocationName);
            if (target is null)
            {
                PendingRestore = null;
                Monitor.Log($"Saved location '{packet.LocationName}' isn't available; keeping vanilla spawn.", LogLevel.Warn);
                return;
            }

            Game1.player.completelyStopAnimatingOrDoingAction();
            Game1.warpFarmer(
                Game1.getLocationRequest(packet.LocationName),
                packet.TileX,
                packet.TileY,
                Config.RestoreFacingDirection ? Math.Clamp(packet.FacingDirection, 0, 3) : Game1.player.FacingDirection
            );

            PendingRestore = null;
            Monitor.Log($"Restored your Aincrad position: {packet.LocationName} ({packet.TileX}, {packet.TileY}).", LogLevel.Info);
        }
        catch (Exception ex)
        {
            PendingRestore = null;
            Monitor.Log($"Couldn't restore saved position; keeping vanilla spawn. {ex.Message}", LogLevel.Warn);
        }
    }

    private bool IsLocationNameSafe(string locationName)
    {
        if (string.IsNullOrWhiteSpace(locationName))
            return false;

        if (Config.UnsafeLocationNames.Any(name => string.Equals(name, locationName, StringComparison.OrdinalIgnoreCase)))
            return false;

        return !Config.UnsafeLocationPrefixes.Any(prefix =>
            !string.IsNullOrWhiteSpace(prefix)
            && locationName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static PlayerPosition Clone(PlayerPosition source) => new()
    {
        PlayerName = source.PlayerName,
        PlayerId = source.PlayerId,
        LocationName = source.LocationName,
        TileX = source.TileX,
        TileY = source.TileY,
        FacingDirection = source.FacingDirection,
        DaysPlayed = source.DaysPlayed,
        WasDuringEvent = source.WasDuringEvent,
        SavedAtUtc = source.SavedAtUtc
    };

    private static bool TryGetTile(Farmer farmer, out int x, out int y)
    {
        x = 0;
        y = 0;

        try
        {
            object? tile = farmer.GetType().GetProperty("TilePoint")?.GetValue(farmer);
            if (tile is null)
                return false;

            object? xValue = tile.GetType().GetProperty("X")?.GetValue(tile);
            object? yValue = tile.GetType().GetProperty("Y")?.GetValue(tile);
            if (xValue is null || yValue is null)
                return false;

            x = Convert.ToInt32(xValue);
            y = Convert.ToInt32(yValue);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void FlushToDisk()
    {
        if (!Context.IsMainPlayer)
            return;

        try
        {
            Helper.Data.WriteJsonFile(DataFile, Store);
            LastFlushUtc = DateTime.UtcNow;
            Dirty = false;

            if (Config.VerboseLogging)
                Monitor.Log("Persistence state flushed to disk.", LogLevel.Trace);
        }
        catch (Exception ex)
        {
            Monitor.Log($"Couldn't write persistence data: {ex.Message}", LogLevel.Error);
        }
    }

    private void ForceSave()
    {
        if (!Context.IsMainPlayer || !Context.IsWorldReady)
        {
            Monitor.Log("This command can only be used by the active host while a save is loaded.", LogLevel.Warn);
            return;
        }

        CaptureOnlinePlayers();
        FlushToDisk();
        Monitor.Log("Aincrad Persistence state saved.", LogLevel.Info);
    }

    private void LogStatus()
    {
        string role = Context.IsMainPlayer ? "HOST" : "CLIENT";
        Monitor.Log(
            $"Aincrad Persistence: {(Config.Enabled ? "enabled" : "disabled")} | role={role} | records={Store.Players.Count} | snapshot={Config.SnapshotIntervalSeconds}s | flush={Config.DiskFlushIntervalSeconds}s",
            LogLevel.Info
        );
    }
}
