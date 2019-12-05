using System;
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
            using (var client = _context.CreateClient())
            {
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
        }
    }
}
