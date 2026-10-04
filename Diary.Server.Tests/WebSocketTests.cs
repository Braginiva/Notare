using System.Net.WebSockets;
using System.Text;
using Diary.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;

namespace Diary.Server.Tests;

public class WebSocketTests : ApiTestBase
{
    [Fact]
    public async Task WsClient_ReceivesChangedMessage_WhenNoteIsCreated()
    {
        // Через in-memory транспорт TestServer, без реальных сокетов.
        var wsClient = Factory.Server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        Assert.Equal(WebSocketState.Open, socket.State);

        // Сервер добавляет сокет в хаб уже после завершения handshake,
        // поэтому шлём изменения с повторами, пока не дождёмся пуша.
        var buffer = new byte[64];
        var message = "";

        for (var attempt = 0; attempt < 5 && message == ""; attempt++)
        {
            await CreateNoteAsync($"Пуш-заметка {attempt}");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            }
            catch (OperationCanceledException)
            {
                // Таймаут — broadcast ещё не пришёл, пробуем ещё раз.
            }
        }

        Assert.Equal("changed", message);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", CancellationToken.None);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }
}
