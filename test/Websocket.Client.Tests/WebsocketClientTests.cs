using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Tests.Testserver;
using Xunit;

namespace Websocket.Client.Tests
{
    public class WebsocketClientTests
    {
        private readonly TestserverApplicationFactory<EchoTestServerStartup> _factory;

        public WebsocketClientTests()
        {
            _factory = new TestserverApplicationFactory<EchoTestServerStartup>();
        }

        [Fact]
        public async Task Receives_Echo()
        {
            using (IWebsocketClient client = CreateClient())
            {
                string received = null;
                var receivedCount = 0;
                var receivedEvent = new ManualResetEvent(false);
                
                client
                    .MessageReceived
                    .Where(x => x.Text.ToLower().Contains("ping"))
                    .Subscribe(msg =>
                    {
                        receivedCount++;
                        received = msg.Text;

                        receivedEvent.Set();
                    });

                await client.Start();

                await client.Send("ping");

                receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

                Assert.NotNull(received);
            }
        }

        private IWebsocketClient CreateClient()
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

        // This is from https://github.com/aspnet/AspNetCore.Docs/blob/master/aspnetcore/fundamentals/websockets/samples/2.x/WebSocketsSample/Startup.cs
        private class EchoTestServerStartup
        {
            public void ConfigureServices(IServiceCollection services)
            {

            }

            public void Configure(IApplicationBuilder app, IHostingEnvironment env)
            {
                app.UseWebSockets();
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path == "/ws")
                    {
                        if (context.WebSockets.IsWebSocketRequest)
                        {
                            WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
                            await Echo(context, webSocket);
                        }
                        else
                        {
                            context.Response.StatusCode = 400;
                        }
                    }
                    else
                    {
                        await next();
                    }
                });
            }

            private async Task Echo(HttpContext context, WebSocket webSocket)
            {
                var buffer = new byte[1024 * 4];
                WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                while (!result.CloseStatus.HasValue)
                {
                    await webSocket.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, CancellationToken.None);

                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                }
                await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
            }
        }
    }


}
