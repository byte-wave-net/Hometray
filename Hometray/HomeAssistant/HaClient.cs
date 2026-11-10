using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Hometray.HomeAssistant;

internal enum HaStatus
{
    NotConfigured,
    Connecting,
    Connected,
    AuthFailed,
    Disconnected,
}

internal sealed class HaException(string message) : Exception(message);

/// <summary>An area (room) of Home Assistant.</summary>
internal sealed record HaArea(string Id, string Name);

/// <summary>
/// Home Assistant WebSocket API client (https://developers.home-assistant.io/docs/api/websocket):
/// authenticates with a long-lived access token, loads all states, follows
/// <c>state_changed</c> events and calls services. Reconnects automatically.
/// Events are raised on background threads - marshal to the UI thread in the handler.
/// </summary>
internal sealed class HaClient : IDisposable
{
    /// <summary>Upper bound of one WebSocket message (a full state list of a big installation is a few MB).</summary>
    private const int MaxMessageBytes = 64 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, HaEntity> _entities = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private HashSet<string> _watched = new();

    private CancellationTokenSource? _cts;
    private ClientWebSocket? _socket;
    private int _nextId;

    public HaStatus Status { get; private set; } = HaStatus.NotConfigured;
    public string? StatusMessage { get; private set; }
    public string? LocationName { get; private set; }

    /// <summary>Areas (rooms) defined in Home Assistant, sorted by name; empty if unavailable.</summary>
    public IReadOnlyList<HaArea> Areas { get; private set; } = Array.Empty<HaArea>();

    private IReadOnlyDictionary<string, string> _entityArea = new Dictionary<string, string>();

    /// <summary>Area id of an entity (its own, else its device's); null = no area.</summary>
    public string? AreaIdOf(string entityId) => _entityArea.TryGetValue(entityId, out var a) ? a : null;

    public string? AreaNameOf(string entityId) =>
        AreaIdOf(entityId) is { } id ? Areas.FirstOrDefault(a => a.Id == id)?.Name : null;
    public string? Version { get; private set; }

    /// <summary>Connection state changed.</summary>
    public event Action? StatusChanged;

    /// <summary>The full state list was (re)loaded after connecting.</summary>
    public event Action? EntitiesLoaded;

    /// <summary>A watched entity changed (null = removed).</summary>
    public event Action<string, HaEntity?>? EntityChanged;

    public IEnumerable<HaEntity> Entities => _entities.Values;
    public HaEntity? Get(string entityId) => _entities.TryGetValue(entityId, out var e) ? e : null;

    /// <summary>Only entities passed here raise <see cref="EntityChanged"/> (all are kept up to date).</summary>
    public void SetWatched(IEnumerable<string> entityIds) => _watched = new HashSet<string>(entityIds);

    // ── URL handling ─────────────────────────────────────────────────────────

    /// <summary>
    /// "homeassistant.local:8123", "http://ha:8123/" or "https://x.ui.nabu.casa/some/path" →
    /// base URI (scheme + host + port, no path). Null if it cannot be parsed.
    /// </summary>
    public static Uri? NormalizeBaseUri(string? input)
    {
        input = input?.Trim();
        if (string.IsNullOrEmpty(input)) return null;
        if (!input.Contains("://")) input = "http://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        return new Uri(uri.GetLeftPart(UriPartial.Authority));
    }

    /// <summary>
    /// True for plain <c>http://</c> to a host that does not look like the home network: the access token and
    /// every command would travel unencrypted over the internet / an untrusted network.
    /// Private = loopback, private / link-local / CGNAT-VPN IP ranges, single-label names ("homeassistant") and
    /// typical home suffixes (.local, .lan, .home, .home.arpa, .internal, .localdomain, .fritz.box).
    /// </summary>
    public static bool IsInsecureRemote(Uri baseUri)
    {
        if (baseUri.Scheme != "http") return false;
        string host = baseUri.DnsSafeHost;

        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip)) return false;
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC); // fc00::/7 unique local
            byte[] b = ip.GetAddressBytes();
            bool isPrivate = b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127); // CGNAT range, also used by VPNs such as Tailscale
            return !isPrivate;
        }

        if (!host.Contains('.')) return false;
        string[] homeSuffixes = { ".local", ".lan", ".home", ".home.arpa", ".internal", ".localdomain", ".fritz.box" };
        return !homeSuffixes.Any(s =>
            host.EndsWith(s, StringComparison.OrdinalIgnoreCase) || host.Equals(s[1..], StringComparison.OrdinalIgnoreCase));
    }

    private static Uri WebSocketUri(Uri baseUri) =>
        new UriBuilder(baseUri) { Scheme = baseUri.Scheme == "https" ? "wss" : "ws", Path = "/api/websocket" }.Uri;

    // ── connection management ────────────────────────────────────────────────

    /// <summary>(Re)starts the connection. Empty URL or token = not configured, stays disconnected.</summary>
    public void Configure(string? baseUrl, string? token)
    {
        Stop();
        var uri = NormalizeBaseUri(baseUrl);
        if (uri is null || string.IsNullOrWhiteSpace(token))
        {
            SetStatus(HaStatus.NotConfigured, null);
            return;
        }

        _cts = new CancellationTokenSource();
        _ = RunAsync(WebSocketUri(uri), token.Trim(), _cts.Token);
    }

    private void Stop()
    {
        var cts = _cts;
        _cts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
        try { _socket?.Abort(); } catch { }
        FailPending(new OperationCanceledException());
        _entities.Clear();
        Areas = Array.Empty<HaArea>();
        _entityArea = new Dictionary<string, string>();
        _servicesCache = default;
    }

    private void SetStatus(HaStatus status, string? message)
    {
        Status = status;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    /// <summary>Supervisor loop: connect, run until the socket closes, back off, repeat.</summary>
    private async Task RunAsync(Uri uri, string token, CancellationToken ct)
    {
        int delaySeconds = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetStatus(HaStatus.Connecting, null);
                await RunConnectionAsync(uri, token, () => delaySeconds = 1, ct);
                if (ct.IsCancellationRequested) return;
                SetStatus(HaStatus.Disconnected, Loc.T("ha.err.disconnected"));
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return; // Configure() replaced this connection - its status is not ours anymore
            }
            catch (HaAuthException ex)
            {
                SetStatus(HaStatus.AuthFailed, ex.Message); // retrying with the same token is pointless
                return;
            }
            catch (Exception ex)
            {
                SetStatus(HaStatus.Disconnected, Describe(ex));
            }

            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct); }
            catch (OperationCanceledException) { return; }
            delaySeconds = Math.Min(delaySeconds * 2, 30);
        }
    }

    private static string Describe(Exception ex) =>
        ex is WebSocketException { InnerException: { } inner } ? inner.Message : ex.Message;

    private sealed class HaAuthException(string message) : Exception(message);

    private async Task RunConnectionAsync(Uri uri, string token, Action onAuthenticated, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _socket = socket;

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await socket.ConnectAsync(uri, connectCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HaException(Loc.T("ha.err.connectTimeout"));
            }
        }

        // auth_required → auth → auth_ok / auth_invalid
        using (var hello = await ReceiveAsync(socket, ct))
        {
            if (hello is null) throw new HaException(Loc.T("ha.err.serverClosed"));
            if (TypeOf(hello.RootElement) != "auth_required") throw new HaException(Loc.T("ha.err.notHa"));
        }
        await SendJsonAsync(socket, new Dictionary<string, object> { ["type"] = "auth", ["access_token"] = token }, ct);
        using (var auth = await ReceiveAsync(socket, ct))
        {
            if (auth is null) throw new HaException(Loc.T("ha.err.serverClosed"));
            string type = TypeOf(auth.RootElement);
            if (type == "auth_invalid")
                throw new HaAuthException(Loc.T("ha.err.tokenRejected"));
            if (type != "auth_ok") throw new HaException(Loc.T("ha.err.authUnexpected"));
            Version = auth.RootElement.TryGetProperty("ha_version", out var v) ? v.GetString() : null;
        }
        onAuthenticated();

        // The receive loop must run while the start-up commands wait for their answers.
        var receiver = ReceiveLoopAsync(socket, ct);
        var startup = StartupAsync(socket, ct);
        try
        {
            // If the socket dies while the start-up commands wait, the receiver ends first.
            if (await Task.WhenAny(receiver, startup) == receiver)
            {
                await receiver;
                return;
            }
            await startup;
            await receiver;
        }
        finally
        {
            FailPending(new HaException(Loc.T("ha.err.disconnected")));
            try { await startup; } catch { /* already reported through the receiver / socket */ }
            if (ReferenceEquals(_socket, socket)) _socket = null;
        }
    }

    private async Task StartupAsync(ClientWebSocket socket, CancellationToken ct)
    {
        // subscribe first, then read the states: events that arrive meanwhile are never lost
        await CommandAsync(socket, new() { ["type"] = "subscribe_events", ["event_type"] = "state_changed" }, ct);
        try
        {
            var config = await CommandAsync(socket, new() { ["type"] = "get_config" }, ct);
            LocationName = config.TryGetProperty("location_name", out var n) ? n.GetString() : null;
        }
        catch (HaException) { /* cosmetic only */ }

        var states = await CommandAsync(socket, new() { ["type"] = "get_states" }, ct);
        _entities.Clear();
        foreach (var s in states.EnumerateArray())
            if (HaEntity.FromJson(s) is { } entity) _entities[entity.EntityId] = entity;

        await LoadAreasAsync(socket, ct);

        SetStatus(HaStatus.Connected, null);
        EntitiesLoaded?.Invoke();
    }

    /// <summary>
    /// Reads areas (rooms) and which entity belongs to which area: an entity's own area, otherwise
    /// the area of its device. The registry commands need an admin user - without it there are
    /// simply no areas (the room filter is hidden).
    /// </summary>
    private async Task LoadAreasAsync(ClientWebSocket socket, CancellationToken ct)
    {
        try
        {
            var areaList = await CommandAsync(socket, new() { ["type"] = "config/area_registry/list" }, ct);
            var deviceList = await CommandAsync(socket, new() { ["type"] = "config/device_registry/list" }, ct);
            var entityList = await CommandAsync(socket, new() { ["type"] = "config/entity_registry/list" }, ct);

            var areas = new List<HaArea>();
            foreach (var a in areaList.EnumerateArray())
            {
                if (a.TryGetProperty("area_id", out var id) && id.GetString() is { } areaId &&
                    a.TryGetProperty("name", out var name) && name.GetString() is { } areaName)
                    areas.Add(new HaArea(areaId, areaName));
            }

            var deviceArea = new Dictionary<string, string>();
            foreach (var d in deviceList.EnumerateArray())
            {
                if (d.TryGetProperty("id", out var id) && id.GetString() is { } deviceId &&
                    d.TryGetProperty("area_id", out var area) && area.GetString() is { } areaId)
                    deviceArea[deviceId] = areaId;
            }

            var entityArea = new Dictionary<string, string>();
            foreach (var e in entityList.EnumerateArray())
            {
                if (!e.TryGetProperty("entity_id", out var eid) || eid.GetString() is not { } entityId) continue;
                string? areaId = e.TryGetProperty("area_id", out var own) ? own.GetString() : null;
                if (areaId is null && e.TryGetProperty("device_id", out var dev) && dev.GetString() is { } deviceId)
                    deviceArea.TryGetValue(deviceId, out areaId);
                if (areaId is not null) entityArea[entityId] = areaId;
            }

            _entityArea = entityArea;
            Areas = areas.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (HaException)
        {
            _entityArea = new Dictionary<string, string>();
            Areas = Array.Empty<HaArea>();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var doc = await ReceiveAsync(socket, ct);
            if (doc is null) return; // closed
            Dispatch(doc.RootElement);
        }
    }

    private void Dispatch(JsonElement root)
    {
        switch (TypeOf(root))
        {
            case "result":
                if (root.TryGetProperty("id", out var rid) && _pending.TryRemove(rid.GetInt32(), out var tcs))
                {
                    if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
                        tcs.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default);
                    else
                    {
                        string msg = root.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m)
                            ? m.GetString() ?? Loc.T("ha.err.generic") : Loc.T("ha.err.generic");
                        tcs.TrySetException(new HaException(msg));
                    }
                }
                break;

            case "event":
                if (root.TryGetProperty("event", out var ev) &&
                    ev.TryGetProperty("event_type", out var et) && et.GetString() == "state_changed" &&
                    ev.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("entity_id", out var idEl) && idEl.GetString() is { } entityId)
                {
                    HaEntity? entity = null;
                    if (data.TryGetProperty("new_state", out var ns) && ns.ValueKind == JsonValueKind.Object)
                        entity = HaEntity.FromJson(ns);

                    if (entity is null) _entities.TryRemove(entityId, out _);
                    else _entities[entityId] = entity;

                    if (_watched.Contains(entityId)) EntityChanged?.Invoke(entityId, entity);
                }
                break;
        }
    }

    // ── commands ─────────────────────────────────────────────────────────────

    /// <summary>Calls a Home Assistant service, e.g. ("light", "turn_on", "light.kitchen", {brightness_pct: 40}).</summary>
    public async Task CallServiceAsync(string domain, string service, string? entityId = null,
        Dictionary<string, object>? data = null)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open || Status != HaStatus.Connected)
            throw new HaException(Loc.T("ha.err.notConnected"));

        var command = new Dictionary<string, object>
        {
            ["type"] = "call_service",
            ["domain"] = domain,
            ["service"] = service,
        };
        if (data is { Count: > 0 }) command["service_data"] = data;
        if (entityId is not null) command["target"] = new Dictionary<string, object> { ["entity_id"] = entityId };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await CommandAsync(socket, command, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new HaException(Loc.T("ha.err.noAnswer"));
        }
    }

    // ── script inputs ────────────────────────────────────────────────────────

    private JsonElement _servicesCache;
    private DateTime _servicesAt;

    /// <summary>
    /// The input fields of a script (empty if it has none). Home Assistant publishes them as the "fields"
    /// of the service <c>script.&lt;script id&gt;</c> in <c>get_services</c>; the script id is the entity id
    /// without the "script." prefix. The (large) service list is cached for a short time.
    /// </summary>
    public async Task<IReadOnlyList<ScriptField>> GetScriptFieldsAsync(string entityId)
    {
        string scriptId = entityId[(entityId.IndexOf('.') + 1)..];
        var services = await GetServicesAsync();
        if (services.ValueKind == JsonValueKind.Object &&
            services.TryGetProperty("script", out var script) && script.ValueKind == JsonValueKind.Object &&
            script.TryGetProperty(scriptId, out var service) && service.ValueKind == JsonValueKind.Object &&
            service.TryGetProperty("fields", out var fields))
            return ScriptField.ParseAll(fields);
        return Array.Empty<ScriptField>();
    }

    private async Task<JsonElement> GetServicesAsync()
    {
        if (_servicesCache.ValueKind == JsonValueKind.Object && DateTime.UtcNow - _servicesAt < TimeSpan.FromSeconds(30))
            return _servicesCache;

        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open || Status != HaStatus.Connected)
            throw new HaException(Loc.T("ha.err.notConnected"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            _servicesCache = await CommandAsync(socket, new() { ["type"] = "get_services" }, timeout.Token);
            _servicesAt = DateTime.UtcNow;
            return _servicesCache;
        }
        catch (OperationCanceledException)
        {
            throw new HaException(Loc.T("ha.err.noAnswer"));
        }
    }

    private async Task<JsonElement> CommandAsync(ClientWebSocket socket, Dictionary<string, object> command, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref _nextId);
        command["id"] = id;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendJsonAsync(socket, command, ct);
            return await tcs.Task.WaitAsync(ct);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void FailPending(Exception ex)
    {
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetException(ex);
    }

    // ── wire helpers ─────────────────────────────────────────────────────────

    private async Task SendJsonAsync(ClientWebSocket socket, object payload, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Reads one complete (possibly fragmented) text message; null when the server closed.</summary>
    private static async Task<JsonDocument?> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            // A (hostile or broken) server must not be able to make the app buffer unbounded data.
            if (ms.Length + result.Count > MaxMessageBytes)
                throw new HaException(Loc.T("ha.err.tooLarge"));
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        ms.Position = 0;
        return await JsonDocument.ParseAsync(ms, cancellationToken: ct);
    }

    private static string TypeOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

    public void Dispose() => Stop();

    // ── connection test (settings dialog) ────────────────────────────────────

    /// <summary>Tries to connect and authenticate once. Returns null on success or an error text.</summary>
    public static async Task<string?> TestAsync(string baseUrl, string token)
    {
        var uri = NormalizeBaseUri(baseUrl);
        if (uri is null) return Loc.T("ha.test.badAddress");
        if (string.IsNullOrWhiteSpace(token)) return Loc.T("ha.test.noToken");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(WebSocketUri(uri), cts.Token);

            using (var hello = await ReceiveAsync(socket, cts.Token))
                if (hello is null || TypeOf(hello.RootElement) != "auth_required") return Loc.T("ha.test.notHa");

            byte[] auth = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["type"] = "auth",
                ["access_token"] = token.Trim(),
            });
            await socket.SendAsync(auth, WebSocketMessageType.Text, true, cts.Token);

            using var reply = await ReceiveAsync(socket, cts.Token);
            string type = reply is null ? "" : TypeOf(reply.RootElement);
            try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
            return type switch
            {
                "auth_ok" => null,
                "auth_invalid" => Loc.T("ha.test.tokenRejected"),
                _ => Loc.T("ha.test.unexpected"),
            };
        }
        catch (OperationCanceledException)
        {
            return Loc.T("ha.test.timeout");
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }
    }
}
