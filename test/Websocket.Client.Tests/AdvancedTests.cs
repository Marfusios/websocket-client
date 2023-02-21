using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Exceptions;
using Websocket.Client.Tests.TestServer;
using Xunit;
using Xunit.Abstractions;

namespace Websocket.Client.Tests
{
    public class AdvancedTests
    {
        private readonly TestContext<SimpleStartup> _context;
        private readonly ITestOutputHelper _output;

        public AdvancedTests(ITestOutputHelper output)
        {
            _output = output;
            _context = new TestContext<SimpleStartup>(_output);
        }

        [Fact]
        public async Task StreamFakeMessage_CustomMessage_ShouldBeFakelyStreamed()
        {
            using var client = _context.CreateClient();
            var myMessage = "my fake message";
            string received = null;
            var receivedCount = 0;
            var receivedEvent = new ManualResetEvent(false);

            client
                .MessageReceived
                .Subscribe(msg =>
                {
                    _output.WriteLine($"Received: '{msg}'");
                    receivedCount++;
                    received = msg.Text;

                    if (receivedCount >= 3)
                        receivedEvent.Set();
                });

            await client.Start();

            client.StreamFakeMessage(ResponseMessage.TextMessage(null));
            client.StreamFakeMessage(ResponseMessage.TextMessage(myMessage));

            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

            Assert.Equal(myMessage, received);
            Assert.Throws<WebsocketBadInputException>(() => client.StreamFakeMessage(null));
        }

        [Fact]
        public async Task Echo_OneChunkMessage_ShouldReceiveCorrectly()
        {
            using var client = _context.CreateClient();
            string received = null;
            var receivedCount = 0;
            var receivedEvent = new ManualResetEvent(false);

            client
                .MessageReceived
                .Subscribe(msg =>
                {
                    receivedCount++;
                    received = msg.Text;

                    if (receivedCount >= 3)
                        receivedEvent.Set();
                });

            await client.Start();

            for (int i = 1; i < 3; i++)
            {
                var sign = i == 1 ? 'A' : 'B';
                var msg = new string(sign, 1024 * 4 - 14);
                client.Send($"echo: special {msg}");
            }

            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

            Assert.NotNull(received);
            Assert.Equal(3, receivedCount);
            Assert.Equal(1024 * 4, received.Length);
            Assert.StartsWith("echo: special BBBB", received);
        }

        [Fact]
        public async Task Echo_LargeMessage_ShouldReceiveCorrectly()
        {
            using var client = _context.CreateClient();
            string received = null;
            var receivedCount = 0;
            var receivedEvent = new ManualResetEvent(false);

            client
                .MessageReceived
                .Subscribe(msg =>
                {
                    receivedCount++;
                    received = msg.Text;

                    if (receivedCount >= 3)
                        receivedEvent.Set();
                });

            await client.Start();

            for (int i = 1; i < 3; i++)
            {
                var sign = i == 1 ? 'A' : 'B';
                var msg = new string(sign, 1024 * 9);
                client.Send($"echo:{msg}");
            }

            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

            Assert.NotNull(received);
            Assert.Equal(3, receivedCount);
            Assert.Equal(1024 * 9 + 5, received.Length);
            Assert.StartsWith("echo:BBBB", received);
        }

        [Fact]
        public async Task IsTextMessageConversionEnabled_False_ShouldReceiveStringMessageAsBinary()
        {
            using var client = _context.CreateClient();
            client.IsTextMessageConversionEnabled = false;

            ResponseMessage received = null;
            var receivedCount = 0;
            var receivedEvent = new ManualResetEvent(false);

            client
                .MessageReceived
                .Subscribe(msg =>
                {
                    receivedCount++;
                    received = msg;

                    if (receivedCount > 1)
                        receivedEvent.Set();
                });

            await client.Start();

            var sign = 'C';
            var msg = new string(sign, 1024 * 9);
            client.Send($"echo:{msg}");

            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

            Assert.NotNull(received);
            Assert.Equal(WebSocketMessageType.Binary, received.MessageType);
            Assert.NotNull(received.Binary);
            Assert.Null(received.Text);
            Assert.Equal(2, receivedCount);
            Assert.Equal(1024 * 9 + 5, received.Binary.Length);
        }
    }
}
