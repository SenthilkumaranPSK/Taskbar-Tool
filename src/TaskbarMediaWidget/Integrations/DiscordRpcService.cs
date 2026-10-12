using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TaskbarMediaWidget.Core;
using TaskbarMediaWidget.Media;

namespace TaskbarMediaWidget.Integrations;

/// <summary>
/// Lightweight, zero-dependency Discord Rich Presence client.
/// Communicates directly with Discord desktop client via Windows named pipe (\\.\pipe\discord-ipc-0).
/// Automatically connects, updates activity when media plays, and disconnects when stopped.
/// </summary>
internal sealed class DiscordRpcService : IDisposable
{
    private const string DefaultClientId = "1214000000000000000"; // Generic Music / Taskbar Tool Application ID
    private const int HandshakeOpcode = 0;
    private const int FrameOpcode = 1;

    private readonly object _pipeLock = new();
    private NamedPipeClientStream? _pipe;
    private bool _isConnected;
    private bool _isDisposed;
    private NowPlayingInfo? _lastInfo;
    private System.Threading.Timer? _reconnectTimer;

    public bool IsConnected => _isConnected;

    public void Start()
    {
        _reconnectTimer = new System.Threading.Timer(OnReconnectTimerTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(20));
    }

    private void OnReconnectTimerTick(object? state)
    {
        if (_isDisposed || !AppSettings.DiscordRpcEnabled) return;

        lock (_pipeLock)
        {
            if (!_isConnected)
            {
                TryConnect();
            }
        }
    }

    private void TryConnect()
    {
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var pipeName = $"discord-ipc-{i}";
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                try
                {
                    pipe.Connect(50); // fast 50ms connect timeout
                    _pipe = pipe;

                    // Send Handshake
                    var handshakePayload = JsonSerializer.Serialize(new { v = 1, client_id = DefaultClientId });
                    SendPacket(HandshakeOpcode, handshakePayload);

                    // Read Handshake response header
                    var header = new byte[8];
                    _pipe.ReadExactly(header, 0, 8);
                    var length = BitConverter.ToInt32(header, 4);
                    if (length > 0)
                    {
                        var resp = new byte[length];
                        _pipe.ReadExactly(resp, 0, length);
                    }

                    _isConnected = true;
                    AppLog.Info($"Discord RPC connected via {pipeName}.");

                    if (_lastInfo is not null)
                    {
                        UpdateActivity(_lastInfo);
                    }
                    return;
                }
                catch
                {
                    pipe.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Discord RPC connection attempt failed: {ex.Message}");
        }
    }

    public void UpdateActivity(NowPlayingInfo? info)
    {
        _lastInfo = info;

        if (!AppSettings.DiscordRpcEnabled)
        {
            ClearActivity();
            return;
        }

        lock (_pipeLock)
        {
            if (!_isConnected || _pipe is null || !_pipe.IsConnected)
            {
                return;
            }

            try
            {
                if (info is null || !info.IsPlaying || string.IsNullOrWhiteSpace(info.Title))
                {
                    ClearActivity();
                    return;
                }

                var title = info.Title.Length > 120 ? info.Title[..117] + "..." : info.Title;
                var artist = string.IsNullOrWhiteSpace(info.Artist) ? info.SourceApp : $"by {info.Artist}";
                if (artist.Length > 120) artist = artist[..117] + "...";

                long? startTimestamp = null;
                long? endTimestamp = null;

                if (info.Duration > TimeSpan.Zero)
                {
                    var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var elapsedSec = (long)info.CurrentPosition.TotalSeconds;
                    var remainingSec = (long)(info.Duration - info.CurrentPosition).TotalSeconds;
                    startTimestamp = nowUnix - elapsedSec;
                    endTimestamp = nowUnix + remainingSec;
                }

                var activityPayload = new
                {
                    cmd = "SET_ACTIVITY",
                    args = new
                    {
                        pid = Environment.ProcessId,
                        activity = new
                        {
                            details = title,
                            state = artist,
                            timestamps = startTimestamp.HasValue ? new { start = startTimestamp, end = endTimestamp } : null,
                            assets = new
                            {
                                large_image = "music_note",
                                large_text = string.IsNullOrWhiteSpace(info.AlbumTitle) ? info.SourceApp : info.AlbumTitle,
                                small_image = "play",
                                small_text = info.SourceApp
                            }
                        }
                    },
                    nonce = Guid.NewGuid().ToString("N")
                };

                var json = JsonSerializer.Serialize(activityPayload);
                SendPacket(FrameOpcode, json);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Discord RPC send activity failed: {ex.Message}");
                Disconnect();
            }
        }
    }

    public void ClearActivity()
    {
        lock (_pipeLock)
        {
            if (!_isConnected || _pipe is null || !_pipe.IsConnected) return;

            try
            {
                var payload = new
                {
                    cmd = "SET_ACTIVITY",
                    args = new
                    {
                        pid = Environment.ProcessId,
                        activity = (object?)null
                    },
                    nonce = Guid.NewGuid().ToString("N")
                };

                var json = JsonSerializer.Serialize(payload);
                SendPacket(FrameOpcode, json);
            }
            catch
            {
                Disconnect();
            }
        }
    }

    private void SendPacket(int opcode, string json)
    {
        if (_pipe is null || !_pipe.IsConnected) return;

        var bytes = Encoding.UTF8.GetBytes(json);
        var buffer = new byte[8 + bytes.Length];
        BitConverter.GetBytes(opcode).CopyTo(buffer, 0);
        BitConverter.GetBytes(bytes.Length).CopyTo(buffer, 4);
        bytes.CopyTo(buffer, 8);

        _pipe.Write(buffer, 0, buffer.Length);
        _pipe.Flush();
    }

    private void Disconnect()
    {
        _isConnected = false;
        try
        {
            _pipe?.Dispose();
        }
        catch { }
        _pipe = null;
    }

    public void Dispose()
    {
        _isDisposed = true;
        _reconnectTimer?.Dispose();
        Disconnect();
    }
}
