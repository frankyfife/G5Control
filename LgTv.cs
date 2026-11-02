using System;
using System.IO;
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
    public const string Ip = "192.168.178.41";

    static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static readonly string DataDir = Path.Combine(AppData, "G5Control");
    static readonly string KeyFile = Path.Combine(DataDir, "clientkey.txt");
    // Same manifest as ColorControl, so its existing pairing can be reused without a new prompt on the TV.
    static readonly string ColorControlKeyFile = Path.Combine(AppData, "Maassoft", "ColorControl", $"{Ip}_ClientKey.txt");

    readonly ClientWebSocket _ws = new();
    int _nextId;

    LgTv()
    {
        _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
    }

    public static async Task<LgTv> ConnectAsync(CancellationToken ct = default)
    {
        var tv = new LgTv();
        try
        {
            using (var connectTimeout = Timeout(ct, 5))
            {
                await tv._ws.ConnectAsync(new Uri($"wss://{Ip}:3001"), connectTimeout.Token);
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
            var response = await ReceiveAsync(timeout.Token);
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

    static string? LoadClientKey()
    {
        foreach (var file in new[] { KeyFile, ColorControlKeyFile })
        {
            if (File.Exists(file))
            {
                var key = File.ReadAllText(file).Trim();
                if (key.Length > 0)
                {
                    return key;
                }
            }
        }
        return null;
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
