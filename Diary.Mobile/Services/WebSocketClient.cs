using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Diary.Mobile.Services;

// WebSocket-клиент для MAUI: подключается к серверу и слушает "changed".
// Когда приходит сигнал — вызывает callback.
public class WebSocketClient
{
    private readonly string _url;
    private readonly Action _onChange;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;

    public WebSocketClient(string url, Action onChange)
    {
        _url = url;
        _onChange = onChange;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _socket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                .Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _socket = new ClientWebSocket();
                await _socket.ConnectAsync(new Uri(_url), ct);
                Console.WriteLine("### WebSocket подключён (mobile)");

                var buffer = new byte[1024];
                var sb = new StringBuilder();

                while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    sb.Clear();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _socket.ReceiveAsync(
                            new ArraySegment<byte>(buffer), ct);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await _socket.CloseAsync(
                                WebSocketCloseStatus.NormalClosure, "bye", ct);
                            return;
                        }

                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    var message = sb.ToString();
                    Console.WriteLine($"### WebSocket получено: {message}");

                    if (message == "changed")
                    {
                        // Сигнал об изменении — вызываем callback.
                        _onChange();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"### WebSocket ошибка: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            Console.WriteLine("### WebSocket переподключение...");
        }
    }
}