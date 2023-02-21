using System;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Tests.TestServer;
using Xunit;
using Xunit.Abstractions;

namespace Websocket.Client.Tests
{
    public class BasicTests
    {
        private readonly TestContext<SimpleStartup> _context;
        private readonly ITestOutputHelper _output;

        public BasicTests(ITestOutputHelper output)
        {
            _output = output;
            _context = new TestContext<SimpleStartup>(_output);
        }

        [Fact]
        public async Task PingPong()
        {
            using var client = _context.CreateClient();
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

                    if (receivedCount >= 6)
                        receivedEvent.Set();
                });

            await client.Start();

            client.Send("ping");
            client.Send("ping");
            client.Send("ping");
            client.Send("ping");
            client.Send("ping");

            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

            Assert.NotNull(received);
            Assert.Equal(5 + 1, receivedCount);
        }

        [Fact]
        public async Task Echo_ShouldReceiveInCorrectOrder()
        {
            using var client = _context.CreateClient();
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

                    if (receivedCount >= 7)
                        receivedEvent.Set();
                });

            await client.Start();

            for (int i = 0; i < 6; i++)
            {
                client.Send($"echo:{i}");
            }

            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

            Assert.NotNull(received);
            Assert.Equal(7, receivedCount);
            Assert.Equal("echo:5", received);
        }
    }


}
