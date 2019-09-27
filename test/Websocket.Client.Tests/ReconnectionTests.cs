using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Tests.TestServer;
using Xunit;
using Xunit.Abstractions;

namespace Websocket.Client.Tests
{
    public class ReconnectionTests
    {
        private readonly TestContext<SimpleStartup> _context;
        private readonly ITestOutputHelper _output;

        public ReconnectionTests(ITestOutputHelper output)
        {
            _output = output;
            _context = new TestContext<SimpleStartup>();
        }


        [Fact]
        public async Task DisabledReconnecting_ShouldWorkAsExpected()
        {
            using (var client = _context.CreateClient())
            {
                var receivedCount = 0;
                var receivedEvent = new ManualResetEvent(false);

                client.IsReconnectionEnabled = false;
                client.ReconnectTimeoutMs = 1 * 1000; // 1sec

                client.MessageReceived
                    .Where(x => x.MessageType ==WebSocketMessageType.Text)
                    .Subscribe(msg =>
                {
                    _output.WriteLine($"Received: '{msg}'");
                    receivedCount++;
                    if (receivedCount >= 2)
                        receivedEvent.Set();
                });

                await client.Start();
                await Task.Delay(3000);
                await client.Stop(WebSocketCloseStatus.Empty, string.Empty);

                await Task.Delay(3000);

                await client.Start();
                await Task.Delay(1000);

                receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

                Assert.Equal(2, receivedCount);
            }
        }

        [Fact]
        public async Task DisabledReconnecting_ShouldWorkAtRuntime()
        {
            using (var client = _context.CreateClient())
            {
                var receivedCount = 0;

                client.IsReconnectionEnabled = true;
                client.ReconnectTimeoutMs = 1 * 1000; // 1sec

                client.MessageReceived.Subscribe(msg =>
                {
                    _output.WriteLine($"Received: '{msg}'");
                    receivedCount++;
                    if (receivedCount >= 2)
                        client.IsReconnectionEnabled = false;
                });

                await client.Start();
                await Task.Delay(7000);

                Assert.Equal(2, receivedCount);
            }
        }
    }
}
