using System.Net.Http;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace AincradSystemBridge;

public sealed class ModConfig
{
    public string BotBaseUrl { get; set; } = "https://aincrad-system-bot.onrender.com";
    public string BridgeSecret { get; set; } = "CHANGE_ME";
    public List<string> GameMasterNames { get; set; } = new() { "PRECISION" };
    public int HeartbeatSeconds { get; set; } = 30;
    public bool SendSaveNotifications { get; set; } = true;
    public bool SendPlayerJoinLeave { get; set; } = true;
}

public sealed class ModEntry : Mod
{
    private readonly HttpClient Http = new();
    private ModConfig Config = new();
    private DateTime LastHeartbeatUtc = DateTime.MinValue;

    public override void Entry(IModHelper helper)
    {
        Config = helper.ReadConfig<ModConfig>();

        helper.Events.GameLoop.SaveLoaded += async (_,__) =>
        {
            if (Context.IsMainPlayer)
                await Send("server_online", new { players = Names() });
        };

        helper.Events.GameLoop.Saved += async (_,__) =>
        {
            if (Context.IsMainPlayer && Config.SendSaveNotifications)
                await Send("save_complete", new { players = Names() });
        };

        helper.Events.GameLoop.UpdateTicked += OnTick;
        helper.Events.Multiplayer.PeerConnected += OnJoin;
        helper.Events.Multiplayer.PeerDisconnected += OnLeave;

        helper.Events.GameLoop.ReturnedToTitle += async (_,__) =>
        {
            if (Context.IsMainPlayer)
                await Send("server_stopping", new { reason = "returned_to_title" });
        };

        Monitor.Log("Aincrad System Bridge loaded.", LogLevel.Info);
    }

    private async void OnTick(object? s, UpdateTickedEventArgs e)
    {
        if (!Context.IsMainPlayer || !Context.IsWorldReady)
            return;

        if ((DateTime.UtcNow - LastHeartbeatUtc).TotalSeconds < Math.Max(10, Config.HeartbeatSeconds))
            return;

        LastHeartbeatUtc = DateTime.UtcNow;
        await Send("heartbeat", new { players = Names() });
    }

    private async void OnJoin(object? s, PeerConnectedEventArgs e)
    {
        if (!Context.IsMainPlayer || !Config.SendPlayerJoinLeave)
            return;

        Farmer? f = Game1.getFarmer(e.Peer.PlayerID);
        string name = f?.Name ?? e.Peer.PlayerID.ToString();
        bool gm = Config.GameMasterNames.Any(x =>
            string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

        await Send("player_join", new
        {
            player = name,
            player_id = e.Peer.PlayerID,
            is_gm = gm
        });
    }

    private async void OnLeave(object? s, PeerDisconnectedEventArgs e)
    {
        if (!Context.IsMainPlayer || !Config.SendPlayerJoinLeave)
            return;

        Farmer? f = Game1.getFarmer(e.Peer.PlayerID);
        string name = f?.Name ?? e.Peer.PlayerID.ToString();

        await Send("player_leave", new
        {
            player = name,
            player_id = e.Peer.PlayerID
        });
    }

    private List<string> Names() =>
        Game1.getOnlineFarmers()
            .Select(f => f.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task Send(string evt, object extra)
    {
        try
        {
            var data = new Dictionary<string, object?>
            {
                ["event"] = evt,
                ["server"] = "Aincrad Style",
                ["timestamp_utc"] = DateTime.UtcNow.ToString("O")
            };

            foreach (var p in extra.GetType().GetProperties())
                data[p.Name] = p.GetValue(extra);

            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                Config.BotBaseUrl.TrimEnd('/') + "/event"
            );

            req.Headers.Add("X-Aincrad-Secret", Config.BridgeSecret);
            req.Content = new StringContent(
                JsonSerializer.Serialize(data),
                Encoding.UTF8,
                "application/json"
            );

            using var res = await Http.SendAsync(req);

            if (!res.IsSuccessStatusCode)
                Monitor.Log($"Bot bridge returned HTTP {(int)res.StatusCode}.", LogLevel.Warn);
        }
        catch (Exception ex)
        {
            Monitor.Log($"Couldn't send Discord bridge event: {ex.Message}", LogLevel.Trace);
        }
    }
}
