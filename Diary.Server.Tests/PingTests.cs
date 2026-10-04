using System.Net;
using Diary.Server.Tests.Infrastructure;

namespace Diary.Server.Tests;

public class PingTests : ApiTestBase
{
    [Fact]
    public async Task Ping_ReturnsOkStatus()
    {
        var response = await Client.GetAsync("/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }
}
