using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Pinscreen2.Server;

/// <summary>
/// What a pinscreen last told us about itself. Persisted so the dashboard can
/// still list a screen that is currently powered off.
/// </summary>
public class DeviceRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Address { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    // Last reported cache state.
    public int CachedFiles { get; set; }
    public long CachedBytes { get; set; }
    public long FreeBytes { get; set; }

    // Last reported sync activity. SyncState is idle | syncing | error.
    public string SyncState { get; set; } = "idle";
    public string SyncMessage { get; set; } = "";
    public int SyncFilesDone { get; set; }
    public int SyncFilesTotal { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>
    /// Live connection state. Also lands in devices.json, which is harmless --
    /// <see cref="DeviceRegistry.Load"/> forces it false on startup.
    /// </summary>
    public bool Online { get; set; }
}

/// <summary>Status payload a device POSTs to <c>/api/devices/{id}/status</c>.</summary>
public class DeviceStatusDto
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public int CachedFiles { get; set; }
    public long CachedBytes { get; set; }
    public long FreeBytes { get; set; }
    public string? SyncState { get; set; }
    public string? SyncMessage { get; set; }
    public int SyncFilesDone { get; set; }
    public int SyncFilesTotal { get; set; }
}

/// <summary>One live SSE connection to a pinscreen.</summary>
internal sealed class DeviceConnection
{
    public required string DeviceId { get; init; }
    public Channel<string> Outbox { get; } =
        Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            // A wedged client must never block the dispatcher or grow unbounded.
            FullMode = BoundedChannelFullMode.DropOldest
        });
    public CancellationTokenSource Cts { get; } = new();
}

/// <summary>
/// Tracks known pinscreens and pushes commands to the ones currently connected.
///
/// Devices hold an SSE connection to <c>/events</c>; commands are queued per
/// connection rather than written directly so a slow or half-dead socket can
/// never stall a caller of <see cref="Send"/>.
/// </summary>
public class DeviceRegistry
{
    private readonly ConcurrentDictionary<string, DeviceRecord> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DeviceConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _statePath;
    private readonly object _saveLock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    // SSE data payloads must be single-line: per spec every line of a frame has
    // to carry its own "data:" prefix, so indented JSON silently truncates the
    // payload to "{" for any conformant reader.
    private static readonly JsonSerializerOptions FrameOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public DeviceRegistry(string statePath)
    {
        _statePath = statePath;
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var list = JsonSerializer.Deserialize<List<DeviceRecord>>(File.ReadAllText(_statePath), JsonOpts);
            if (list == null) return;
            foreach (var d in list)
            {
                if (string.IsNullOrWhiteSpace(d.Id)) continue;
                d.Online = false;
                // A device that was mid-sync when we shut down is not syncing now.
                if (d.SyncState == "syncing") d.SyncState = "idle";
                _devices[d.Id] = d;
            }
            Console.WriteLine($"Loaded {_devices.Count} known device(s) from {_statePath}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"Device state load failed: {ex.Message}"); }
    }

    private void Save()
    {
        try
        {
            lock (_saveLock)
            {
                var json = JsonSerializer.Serialize(_devices.Values.OrderBy(d => d.Name).ToList(), JsonOpts);
                var tmp = _statePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _statePath, overwrite: true);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"Device state save failed: {ex.Message}"); }
    }

    public List<DeviceRecord> Snapshot()
    {
        foreach (var kv in _devices) kv.Value.Online = _connections.ContainsKey(kv.Key);
        return _devices.Values
            .OrderByDescending(d => d.Online)
            .ThenBy(d => string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public DeviceRecord Touch(string id, string? name, string? version, string? address)
    {
        var now = DateTimeOffset.UtcNow;
        var rec = _devices.GetOrAdd(id, _ => new DeviceRecord { Id = id, FirstSeen = now });
        if (!string.IsNullOrWhiteSpace(name)) rec.Name = name!;
        if (!string.IsNullOrWhiteSpace(version)) rec.Version = version!;
        if (!string.IsNullOrWhiteSpace(address)) rec.Address = address!;
        if (string.IsNullOrWhiteSpace(rec.Name)) rec.Name = id;
        rec.LastSeen = now;
        Save();
        return rec;
    }

    public bool ApplyStatus(string id, DeviceStatusDto dto, string? address)
    {
        if (!_devices.TryGetValue(id, out var rec))
            rec = Touch(id, dto.Name, dto.Version, address);

        if (!string.IsNullOrWhiteSpace(dto.Name)) rec.Name = dto.Name!;
        if (!string.IsNullOrWhiteSpace(dto.Version)) rec.Version = dto.Version!;
        if (!string.IsNullOrWhiteSpace(address)) rec.Address = address!;
        rec.CachedFiles = dto.CachedFiles;
        rec.CachedBytes = dto.CachedBytes;
        rec.FreeBytes = dto.FreeBytes;
        rec.SyncFilesDone = dto.SyncFilesDone;
        rec.SyncFilesTotal = dto.SyncFilesTotal;
        if (!string.IsNullOrWhiteSpace(dto.SyncState))
        {
            // Only stamp LastSyncAt on the syncing -> idle edge, so the dashboard
            // shows when a sync actually finished rather than when we last heard.
            if (rec.SyncState == "syncing" && dto.SyncState == "idle")
                rec.LastSyncAt = DateTimeOffset.UtcNow;
            rec.SyncState = dto.SyncState!;
        }
        rec.SyncMessage = dto.SyncMessage ?? "";
        rec.LastSeen = DateTimeOffset.UtcNow;
        Save();
        return true;
    }

    public bool Forget(string id)
    {
        if (_connections.TryRemove(id, out var conn))
        {
            conn.Outbox.Writer.TryComplete();
            try { conn.Cts.Cancel(); } catch { }
        }
        var removed = _devices.TryRemove(id, out _);
        if (removed) Save();
        return removed;
    }

    /// <summary>
    /// Registers a live SSE connection. A second connection from the same device
    /// (reconnect after a dropped link) evicts the first.
    /// </summary>
    internal DeviceConnection OpenConnection(string deviceId)
    {
        var conn = new DeviceConnection { DeviceId = deviceId };
        if (_connections.TryGetValue(deviceId, out var old))
        {
            old.Outbox.Writer.TryComplete();
            try { old.Cts.Cancel(); } catch { }
        }
        _connections[deviceId] = conn;
        Console.WriteLine($"Device connected: {deviceId} ({_connections.Count} online)");
        return conn;
    }

    internal void CloseConnection(DeviceConnection conn)
    {
        // Only remove if it is still the current connection -- a reconnect may
        // have already replaced it, and we must not evict the newer one.
        if (_connections.TryGetValue(conn.DeviceId, out var cur) && ReferenceEquals(cur, conn))
            _connections.TryRemove(conn.DeviceId, out _);
        conn.Outbox.Writer.TryComplete();
        try { conn.Cts.Cancel(); } catch { }
        try { conn.Cts.Dispose(); } catch { }
        Console.WriteLine($"Device disconnected: {conn.DeviceId} ({_connections.Count} online)");
    }

    public bool IsOnline(string deviceId) => _connections.ContainsKey(deviceId);

    public int OnlineCount => _connections.Count;

    /// <summary>Queues an SSE frame for one device. Returns false if it is offline.</summary>
    public bool Send(string deviceId, string eventName, object payload)
    {
        if (!_connections.TryGetValue(deviceId, out var conn)) return false;
        var frame = $"event: {eventName}\ndata: {JsonSerializer.Serialize(payload, FrameOpts)}\n\n";
        return conn.Outbox.Writer.TryWrite(frame);
    }

    /// <summary>Queues an SSE frame for every connected device. Returns the count reached.</summary>
    public int Broadcast(string eventName, object payload)
    {
        var frame = $"event: {eventName}\ndata: {JsonSerializer.Serialize(payload, FrameOpts)}\n\n";
        int n = 0;
        foreach (var conn in _connections.Values)
            if (conn.Outbox.Writer.TryWrite(frame)) n++;
        return n;
    }
}
