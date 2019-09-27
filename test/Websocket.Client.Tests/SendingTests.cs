using System;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Tests.TestServer;
using Xunit;

namespace Websocket.Client.Tests
{
    public class SendingTests
    {
        private readonly TestContext<SimpleStartup> _context;

        public SendingTests()
        {
            _context = new TestContext<SimpleStartup>();
        }

        [Fact]
        public async Task SendMessageBeforeStart_ShouldWorkAfterStart()
        {
            using (var client = _context.CreateClient())
            {
                string received = null;
                var receivedCount = 0;
                var receivedEvent = new ManualResetEvent(false);

                await client.Send("ping");
                await client.Send("ping");
                await client.Send("ping");
                await client.Send("ping");

                client
                    .MessageReceived
                    .Where(x => x.Text.ToLower().Contains("pong"))
                    .Subscribe(msg =>
                    {
                        receivedCount++;
                        received = msg.Text;

                        if (receivedCount >= 7)
                            receivedEvent.Set();
                    });

                await client.Start();

                await client.Send("ping");
                await client.Send("ping");
                await client.Send("ping");

                receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

                Assert.NotNull(received);
            }
        }

        [Fact]
        public async Task SendBinaryMessage_ShouldWork()
        {
            using (var client = _context.CreateClient())
            {
                byte[] received = null;
                var receivedEvent = new ManualResetEvent(false);

                client.MessageReceived
                    .Where(x => x.MessageType == WebSocketMessageType.Binary)
                    .Subscribe(msg =>
                {
                    received = msg.Binary;
                    receivedEvent.Set();
                });

                await client.Start();
                await client.Send(new byte[] { 10, 14, 15, 16 });

                receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

                Assert.NotNull(received);
            }
        }
    }
}
