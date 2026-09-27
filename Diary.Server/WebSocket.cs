using System.Net.WebSockets;
using System.Text;

namespace Diary.Server;

// Хаб всех подключённых WebSocket-клиентов.
// Позволяет серверу рассылать сигналы всем клиентам одновременно.
public static class WebSocketHub
{
    private static readonly List<WebSocket> _sockets = new();
    private static readonly object _lock = new();

    public static void Add(WebSocket socket)
    {
        lock (_lock)
        {
            _sockets.Add(socket);
        }
    }

    public static void Remove(WebSocket socket)
    {
        lock (_lock)
        {
            _sockets.Remove(socket);
        }
    }

    // Рассылает всем подключённым клиентам короткое сообщение.
    // Клиенты, получив его, сами дёрнут GET /sync или /notes.
    public static async Task BroadcastChangeAsync()
    {
        // Короткое сообщение. Что именно — неважно, клиент просто понимает «есть изменения».
        var payload = Encoding.UTF8.GetBytes("changed");

        List<WebSocket> targets;
        lock (_lock)
        {
            targets = _sockets.ToList();
        }

        var tasks = new List<Task>();
        foreach (var socket in targets)
        {
            if (socket.State == WebSocketState.Open)
            {
                tasks.Add(SafeSendAsync(socket, payload));
            }
        }

        await Task.WhenAll(tasks);
    }

    private static async Task SafeSendAsync(WebSocket socket, byte[] payload)
    {
        try
        {
            await socket.SendAsync(
                new ArraySegment<byte>(payload),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);
        }
        catch
        {
            // Если отправка не удалась — клиент отвалился, удалим его.
            Remove(socket);
        }
    }
}