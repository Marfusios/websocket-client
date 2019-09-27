using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Tests.TestServer;
using Xunit;

namespace Websocket.Client.Tests
{
    public class BasicTests
    {
        private readonly TestContext<SimpleStartup> _context;

        public BasicTests()
        {
            _context = new TestContext<SimpleStartup>();
        }

        [Fact]
        public async Task PingPong()
        {
            using (var client = _context.CreateClient())
            {
                string received = null;
                var receivedCount = 0;
                var receivedEvent = new ManualResetEvent(false);
                
                client
                    .MessageReceived
                    .Subscribe(msg =>
                    {
                        receivedCount++;
                        received = msg.Text;

                        if(receivedCount >= 5)
                            receivedEvent.Set();
                    });

                await client.Start();

                await client.Send("ping");
                await client.Send("ping");
                await client.Send("ping");
                await client.Send("ping");
                await client.Send("ping");

                receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

                Assert.NotNull(received);
                Assert.Equal(5+1, receivedCount);
            }
        }

        [Fact]
        public async Task Echo_ShouldReceiveInCorrectOrder()
        {
            using (var client = _context.CreateClient())
            {
                string received = null;
                var receivedCount = 0;
                var receivedEvent = new ManualResetEvent(false);

                client
                    .MessageReceived
                    .Subscribe(msg =>
                    {
                        receivedCount++;
                        received = msg.Text;

                        if (receivedCount >= 7)
                            receivedEvent.Set();
                    });

                await client.Start();

                for (int i = 0; i < 6; i++)
                {
                    await client.Send($"echo:{i}");
                }

                receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

                Assert.NotNull(received);
                Assert.Equal(7, receivedCount);
                Assert.Equal("echo:5", received);
            }
        }

        [Fact]
        public async Task Starting_MultipleTimes_ShouldWorkWithNoExceptions()
        {
            var clients = new List<IWebsocketClient>();
            for (int i = 0; i < 5; i++)
            {
                var client = _context.CreateClient();
                await client.Start();
                await Task.Delay(i * 20);
                clients.Add(client);
            }

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
                client.ReconnectTimeoutMs = 7 * 1000; // 7sec

                string received = null;
                var receivedCount = 0;
                var receivedEvent = new ManualResetEvent(false);

                client.MessageReceived
                    .Where(x => x.MessageType == WebSocketMessageType.Text)
                    .Subscribe(msg =>
                {
                    receivedCount++;
                    received = msg.Text;
                });

                await client.Start();

#pragma warning disable 4014
                Task.Run(async () =>
#pragma warning restore 4014
                {
                    await Task.Delay(200);
                    var success = await client.Stop(WebSocketCloseStatus.InternalServerError, "server error 500");
                    Assert.True(success);
                    receivedEvent.Set();
                });

                receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

                // check that reconnection is disabled
                await Task.Delay(8000);
                Assert.Equal(1, receivedCount);
            }
        }
    }


}
