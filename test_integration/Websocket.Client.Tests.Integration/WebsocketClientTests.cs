using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Websocket.Client.Tests.Integration
{
    public class WebsocketClientTests
    {
        [Fact]
        public async Task OnStarting_ShouldGetInfoResponse()
        {
            var url = new Uri("wss://www.bitmex.com/realtime");
            using (var client = new WebsocketClient(url))
            {
                string received = null;
                var receivedEvent = new ManualResetEvent(false);

                client.MessageReceived.Subscribe(msg =>
                {
                    received = msg;
                    receivedEvent.Set();
                });

                await client.Start();

                receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

                Assert.NotNull(received);
            }
        }
    }
}
