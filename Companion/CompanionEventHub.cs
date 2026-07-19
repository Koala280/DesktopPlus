using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPlus.Companion
{
    /// <summary>
    /// Tracks connected WebSocket clients and pushes live events: panel open/close
    /// (broadcast) and per-connection directory-change notifications (the client asks to
    /// "watch" the folder it is currently viewing).
    /// </summary>
    internal sealed class CompanionEventHub
    {
        private readonly ConcurrentDictionary<Guid, Connection> _connections = new();

        public int Count => _connections.Count;

        public async Task HandleConnectionAsync(WebSocket socket, CancellationToken cancellation)
        {
            var connection = new Connection(socket);
            _connections[connection.Id] = connection;
            try
            {
                var buffer = new byte[4096];
                while (socket.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        HandleClientMessage(connection, Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                }
            }
            finally
            {
                _connections.TryRemove(connection.Id, out _);
                connection.Dispose();
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch
                {
                }
            }
        }

        public void BroadcastPanelsChanged() => Broadcast("{\"type\":\"panelsChanged\"}");

        private void HandleClientMessage(Connection connection, string message)
        {
            try
            {
                using var document = JsonDocument.Parse(message);
                if (document.RootElement.TryGetProperty("watch", out var watch) &&
                    watch.ValueKind == JsonValueKind.String)
                {
                    connection.Watch(
                        watch.GetString(),
                        path => connection.SendFireAndForget(
                            $"{{\"type\":\"fsChanged\",\"path\":{JsonSerializer.Serialize(path)}}}"));
                }
            }
            catch
            {
                // Ignore malformed client messages.
            }
        }

        private void Broadcast(string json)
        {
            foreach (var connection in _connections.Values)
            {
                connection.SendFireAndForget(json);
            }
        }

        private sealed class Connection : IDisposable
        {
            public Guid Id { get; } = Guid.NewGuid();

            private readonly WebSocket _socket;
            private readonly SemaphoreSlim _sendLock = new(1, 1);
            private FileSystemWatcher? _watcher;
            private DateTime _lastEventUtc = DateTime.MinValue;

            public Connection(WebSocket socket)
            {
                _socket = socket;
            }

            public void SendFireAndForget(string json) => _ = SendAsync(json);

            private async Task SendAsync(string json)
            {
                if (_socket.State != WebSocketState.Open)
                {
                    return;
                }

                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await _sendLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_socket.State == WebSocketState.Open)
                    {
                        await _socket.SendAsync(
                            new ArraySegment<byte>(bytes),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch
                {
                }
                finally
                {
                    _sendLock.Release();
                }
            }

            public void Watch(string? path, Action<string> onChanged)
            {
                try
                {
                    _watcher?.Dispose();
                }
                catch
                {
                }
                _watcher = null;

                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                {
                    return;
                }

                try
                {
                    var watcher = new FileSystemWatcher(path)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                       NotifyFilters.Size | NotifyFilters.LastWrite,
                        IncludeSubdirectories = false
                    };

                    void Notify()
                    {
                        var now = DateTime.UtcNow;
                        if ((now - _lastEventUtc).TotalMilliseconds < 300)
                        {
                            return;
                        }
                        _lastEventUtc = now;
                        onChanged(path!);
                    }

                    watcher.Created += (_, __) => Notify();
                    watcher.Deleted += (_, __) => Notify();
                    watcher.Changed += (_, __) => Notify();
                    watcher.Renamed += (_, __) => Notify();
                    watcher.EnableRaisingEvents = true;
                    _watcher = watcher;
                }
                catch
                {
                }
            }

            public void Dispose()
            {
                try
                {
                    _watcher?.Dispose();
                }
                catch
                {
                }
                _sendLock.Dispose();
            }
        }
    }
}
