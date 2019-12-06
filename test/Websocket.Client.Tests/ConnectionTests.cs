using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Exceptions;
using Websocket.Client.Tests.TestServer;
using Xunit;
using Xunit.Abstractions;

namespace Websocket.Client.Tests
{
    public class ConnectionTests
    {
        private readonly TestContext<SimpleStartup> _context;
        private readonly ITestOutputHelper _output;

        public ConnectionTests(ITestOutputHelper output)
        {
            _output = output;
            _context = new TestContext<SimpleStartup>(_output);
        }


        [Fact]
        public async Task StartOrFail_ValidServer_ShouldWorkAsExpected()
        {
            using (var client = _context.CreateClient())
            {
                string received = null;
                var receivedCount = 0;
                var disconnectionCount = 0;
                var disconnectionType = DisconnectionType.Exit;

                client
                    .MessageReceived
                    .Subscribe(msg =>
                    {
                        _output.WriteLine($"Received: '{msg}'");
                        receivedCount++;
                        received = msg.Text;
                    });

                client.DisconnectionHappened.Subscribe(x =>
                {
                    disconnectionCount++;
                    disconnectionType = x.Type;
                });

                await client.StartOrFail();

                client.Send("ping");
                client.Send("ping");
                client.Send("ping");
                client.Send("ping");
                client.Send("ping");

                await Task.Delay(1000);

                Assert.Equal(0, disconnectionCount);
                Assert.Equal(DisconnectionType.Exit, disconnectionType);

                Assert.NotNull(received);
                Assert.Equal(5 + 1, receivedCount);
            }
        }

        [Fact]
        public async Task StartOrFail_InvalidServer_ShouldThrowException()
        {
            using (var client = _context.CreateClient(new Uri("wss://google.com")))
            {
                string received = null;
                var receivedCount = 0;
                var disconnectionCount = 0;
                DisconnectionInfo disconnectionInfo = null;
                Exception causedException = null;

                client
                    .MessageReceived
                    .Subscribe(msg =>
                    {
                        _output.WriteLine($"Received: '{msg}'");
                        receivedCount++;
                        received = msg.Text;
                    });

                client.DisconnectionHappened.Subscribe(x =>
                {
                    disconnectionCount++;
                    disconnectionInfo = x;
                });

                try
                {
                    await client.StartOrFail();
                }
                catch (WebsocketException e)
                {
                    // expected exception
                    _output.WriteLine($"Received exception: '{e.Message}'");
                    causedException = e;
                }

                await Task.Delay(1000);

                Assert.Equal(1, disconnectionCount);
                Assert.Equal(DisconnectionType.Error, disconnectionInfo.Type);
                Assert.NotNull(disconnectionInfo.Exception);
                Assert.Equal(causedException?.InnerException, disconnectionInfo.Exception);

                Assert.Equal(0, receivedCount);
                Assert.Null(received);
            }
        }



        [Fact]
        public async Task Starting_MultipleTimes_ShouldWorkWithNoExceptions()
        {
            var clients = new List<IWebsocketClient>();
            for (int i = 0; i < 5; i++)
            {
                var client = _context.CreateClient();
                client.Name = $"Client:{i}";
                await client.Start();
                await Task.Delay(i * 20);
                clients.Add(client);
            }

            foreach (var client in clients)
            {
#pragma warning disable 4014
                client.Send("ping");
#pragma warning restore 4014
            }

            await Task.Delay(1000);

            foreach (var client in clients)
            {
                client.Dispose();
            }
        }

        [Fact]
        public async Task Stopping_ShouldWorkCorrectly()
        {
            using (var client = _context.CreateClient())
            {
                client.ReconnectTimeout = TimeSpan.FromSeconds(7);

                string received = null;
                var receivedCount = 0;
                var receivedEvent = new ManualResetEvent(false);
                var disconnectionCount = 0;
                DisconnectionInfo disconnectionInfo = null;

                client.MessageReceived
                    .Where(x => x.MessageType == WebSocketMessageType.Text)
                    .Subscribe(msg =>
                    {
                        _output.WriteLine($"Received: '{msg}'");
                        receivedCount++;
                        received = msg.Text;
                    });

                client.DisconnectionHappened.Subscribe(x =>
                {
                    disconnectionCount++;
                    disconnectionInfo = x;
                });

                await client.Start();

                _ = Task.Run(async () =>
                {
                    await Task.Delay(200);
                    await client.Stop(WebSocketCloseStatus.InternalServerError, "server error 500");
                    receivedEvent.Set();
                });

                receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

                // check that reconnection is disabled
                await Task.Delay(8000);
                Assert.Equal(1, receivedCount);
                Assert.InRange(disconnectionCount, 1, 2);
                Assert.Equal(DisconnectionType.ByUser, disconnectionInfo.Type);
                Assert.Equal(WebSocketCloseStatus.InternalServerError, disconnectionInfo.CloseStatus);
                Assert.Equal("server error 500", disconnectionInfo.CloseStatusDescription);
            }
        }
    }
}
