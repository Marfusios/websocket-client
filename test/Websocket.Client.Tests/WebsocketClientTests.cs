using System;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Tests.TestServer;
using Xunit;

namespace Websocket.Client.Tests
{
    public class WebsocketClientTests
    {
        private readonly TestContext<SimpleStartup> _context;

        public WebsocketClientTests()
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
                Assert.Equal(5, receivedCount);
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

                        if (receivedCount >= 6)
                            receivedEvent.Set();
                    });

                await client.Start();

                for (int i = 0; i < 6; i++)
                {
                    await client.Send($"echo:{i}");
                }

                receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

                Assert.NotNull(received);
                Assert.Equal(6, receivedCount);
                Assert.Equal("echo:5", received);
            }
        }
    }


}
