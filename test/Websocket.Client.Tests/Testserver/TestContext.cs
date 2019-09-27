using System;

namespace Websocket.Client.Tests.TestServer
{
    public class TestContext<TStartup> where TStartup : class
    {
        private readonly TestServerApplicationFactory<TStartup> _factory;

        public TestContext()
        {
            _factory = new TestServerApplicationFactory<TStartup>();
        }

        public IWebsocketClient CreateClient()
        {
            var httpClient = _factory.CreateClient(); // This is needed since _factory.Server would otherwise be null
            var wsUri = new UriBuilder(_factory.Server.BaseAddress)
            {
                Scheme = "ws",
                Path = "ws"
            }.Uri;
            return new WebsocketClient(wsUri,
                async (uri, token) =>
                {
                    var client = _factory.Server.CreateWebSocketClient();
                    return await client.ConnectAsync(uri, token).ConfigureAwait(false);
                });
        }
    }
}
