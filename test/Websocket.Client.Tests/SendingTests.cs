using System;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Tests.TestServer;
using Xunit;
using Xunit.Abstractions;

namespace Websocket.Client.Tests
{
    public class SendingTests
    {
        private readonly TestContext<SimpleStartup> _context;
        private readonly ITestOutputHelper _output;

        public SendingTests(ITestOutputHelper output)
        {
            _output = output;
            _context = new TestContext<SimpleStartup>(_output);
        }

        [Fact]
        public async Task SendMessageBeforeStart_ShouldWorkAfterStart()
        {
            using var client = _context.CreateClient();
            string received = null;
            var receivedCount = 0;
            var receivedEvent = new ManualResetEvent(false);

            client.Send("ping");
            client.Send("ping");
            client.Send("ping");
            client.Send("ping");

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

            client.Send("ping");
            client.Send("ping");
            client.Send("ping");

            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

            Assert.NotNull(received);
        }

        [Fact]
        public async Task SendBinaryMessage_ShouldWork()
        {
            using var client = _context.CreateClient();
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
            client.Send(new byte[] { 10, 14, 15, 16 });

            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

            Assert.NotNull(received);
            Assert.Equal(4, received.Length);
            Assert.Equal(14, received[1]);
        }

        [Fact]
        public async Task SendMessageAfterDispose_ShouldDoNothing()
        {
            using var client = _context.CreateClient();
            string received = null;
            var receivedCount = 0;
            var receivedEvent = new ManualResetEvent(false);

            client
                .MessageReceived
                .Where(x => x.Text.ToLower().Contains("pong"))
                .Subscribe(msg =>
                {
                    receivedCount++;
                    received = msg.Text;

                    if (receivedCount >= 3)
                        receivedEvent.Set();
                });

            await client.Start();

            client.Send("ping");
            client.Send("ping");
            client.Send("ping");

            await Task.Delay(1000);
            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

            client.Dispose();

            await Task.Delay(2000);

            client.Send("ping");
            await client.SendInstant("ping");

            await Task.Delay(1000);

            Assert.NotNull(received);
            Assert.Equal(3, receivedCount);
        }
    }
}
