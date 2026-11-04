using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace G5Control;

/// <summary>
/// Minimal SSAP client for the LG G5 (wss://:3001). Settings are written through the luna
/// settings service, wrapped in a briefly shown alert, the same route ColorControl uses.
/// </summary>
public sealed class LgTv : IAsyncDisposable
{
    const int Port = 3001;

    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "G5Control");
    // IP of the TV found last; when it no longer answers, the local network is searched again.
    static readonly string IpFile = Path.Combine(DataDir, "tv.txt");

    readonly ClientWebSocket _ws = new();
    readonly string _ip;
    int _nextId;

    LgTv(string ip)
    {
        _ip = ip;
        _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
    }

    // Pairing keys belong to one TV, so a replaced TV asks for a new pairing instead of rejecting the old key.
    string KeyFile => Path.Combine(DataDir, $"clientkey_{_ip}.txt");

    public static async Task<LgTv> ConnectAsync(CancellationToken ct = default)
    {
        var ip = await LocateAsync(ct) ?? throw new OperationCanceledException("Kein LG-Fernseher im Netzwerk gefunden.");
        var tv = new LgTv(ip);
        try
        {
            using (var connectTimeout = Timeout(ct, 5))
            {
                await tv._ws.ConnectAsync(new Uri($"wss://{ip}:{Port}"), connectTimeout.Token);
            }
            await tv.RegisterAsync(ct);
            return tv;
        }
        catch
        {
            await tv.DisposeAsync();
            throw;
        }
    }

    /// <summary>The saved TV address, entered by hand or found by the last search.</summary>
    public static string? SavedIp
    {
        get
        {
            try
            {
                var ip = File.Exists(IpFile) ? File.ReadAllText(IpFile).Trim() : "";
                return ip.Length > 0 ? ip : null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    public static void SaveIp(string ip)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(IpFile, ip);
    }

    /// <summary>Searches the local network for the TV and saves its address when found.</summary>
    public static async Task<string?> SearchAsync(CancellationToken ct = default)
    {
        var ip = await FindTvAsync(ct);
        if (ip != null)
        {
            SaveIp(ip);
        }
        return ip;
    }

    // Right after the TV or the PC wakes up the port can stay closed for a few seconds, so try a few times.
    static async Task<string?> LocateAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(2000, ct);
            }
            if (SavedIp is { } saved && await IsPortOpenAsync(saved, ct))
            {
                return saved;
            }
            if (await SearchAsync(ct) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    static async Task<bool> IsPortOpenAsync(string ip, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = Timeout(ct, 2);
        try
        {
            await client.ConnectAsync(ip, Port, timeout.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Probes every address of the local /24 networks for the TV's SSAP port.</summary>
    static async Task<string?> FindTvAsync(CancellationToken ct)
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.GetAddressBytes())
            .Where(own => own[0] != 169)  // link-local, no DHCP
            .SelectMany(own => Enumerable.Range(1, 254).Where(host => host != own[3]).Select(host => $"{own[0]}.{own[1]}.{own[2]}.{host}"))
            .Distinct()
            .ToArray();
        var probes = candidates.Select(async ip => await IsPortOpenAsync(ip, ct) ? ip : null).ToList();
        while (probes.Count > 0)
        {
            var done = await Task.WhenAny(probes);
            probes.Remove(done);
            if (await done is { } ip)
            {
                return ip;
            }
        }
        return null;
    }

    async Task RegisterAsync(CancellationToken ct)
    {
        var msg = JsonNode.Parse(ReadResource("register.json"))!;
        var key = LoadClientKey();
        if (key != null)
        {
            msg["payload"]!["client-key"] = key;
        }
        await SendAsync(msg, ct);

        // Without a stored key the TV shows a pairing prompt; give the user time to accept it.
        using var timeout = Timeout(ct, key == null ? 60 : 10);
        while (true)
        {
            JsonNode response;
            try
            {
                response = await ReceiveAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (key == null && !ct.IsCancellationRequested)
            {
                throw new InvalidOperationException("Kopplung nicht bestätigt – bitte die Anfrage auf dem Fernseher annehmen.");
            }
            switch ((string?)response["type"])
            {
                case "registered":
                    var newKey = (string?)response["payload"]?["client-key"];
                    if (newKey != null && newKey != key)
                    {
                        Directory.CreateDirectory(DataDir);
                        File.WriteAllText(KeyFile, newKey);
                    }
                    return;
                case "error":
                    throw new InvalidOperationException($"Kopplung abgelehnt: {response["error"]}");
            }
        }
    }

    string? LoadClientKey()
    {
        var key = File.Exists(KeyFile) ? File.ReadAllText(KeyFile).Trim() : "";
        return key.Length > 0 ? key : null;
    }

    /// <summary>Sets one setting. Keys like "gameMode_hdmi1" become { "gameMode": { "hdmi1": value } }.</summary>
    public async Task SetSettingAsync(string key, string value, string category, CancellationToken ct = default)
    {
        JsonNode jsonValue = JsonValue.Create(value);
        var separator = key.IndexOf('_');
        if (separator > 0)
        {
            jsonValue = new JsonObject { [key[(separator + 1)..]] = value };
            key = key[..separator];
        }

        const string lunaUri = "luna://com.webos.settingsservice/setSystemSettings";
        JsonObject Call(string uriField) => new()
        {
            [uriField] = lunaUri,
            ["params"] = new JsonObject { ["category"] = category, ["settings"] = new JsonObject { [key] = jsonValue.DeepClone() } },
        };

        var button = Call("onClick");
        button["label"] = "";
        var payload = new JsonObject
        {
            ["message"] = "Applying...",
            ["buttons"] = new JsonArray(button),
            ["onclose"] = Call("uri"),
            ["onfail"] = Call("uri"),
        };

        var alert = await RequestAsync("ssap://system.notifications/createAlert", payload, ct);
        var alertId = (string?)alert["alertId"];
        if (alertId != null)
        {
            await RequestAsync("ssap://system.notifications/closeAlert", new JsonObject { ["alertId"] = alertId }, ct);
        }
    }

    /// <summary>Reads the picture values the TV allows to be read (dark room mode and VRR are write-only).</summary>
    public async Task<JsonObject> GetPictureSettingsAsync(CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["category"] = "picture",
            ["keys"] = new JsonArray("pictureMode", "backlight", "energySaving"),
        };
        var response = await RequestAsync("ssap://settings/getSystemSettings", payload, ct);
        return response["settings"]?.AsObject() ?? new JsonObject();
    }

    async Task<JsonNode> RequestAsync(string uri, JsonNode payload, CancellationToken ct)
    {
        var id = $"req_{++_nextId}";
        await SendAsync(new JsonObject { ["type"] = "request", ["id"] = id, ["uri"] = uri, ["payload"] = payload }, ct);

        using var timeout = Timeout(ct, 10);
        while (true)
        {
            var response = await ReceiveAsync(timeout.Token);
            if ((string?)response["id"] != id)
            {
                continue;
            }
            if ((string?)response["type"] == "error")
            {
                throw new InvalidOperationException($"{uri}: {response["error"]}");
            }
            return response["payload"] ?? new JsonObject();
        }
    }

    async Task SendAsync(JsonNode message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    async Task<JsonNode> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[16384];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new IOException("Der Fernseher hat die Verbindung geschlossen.");
            }
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray()))!;
    }

    static CancellationTokenSource Timeout(CancellationToken ct, int seconds)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    static string ReadResource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"G5Control.{name}")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
            catch
            {
                // The TV may drop the socket first; nothing to clean up then.
            }
        }
        _ws.Dispose();
    }
}
