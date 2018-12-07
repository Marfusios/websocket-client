using System;
using System.Reactive.Linq;
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

        [Fact]
        public async Task SendMessageBeforeStart_ShouldWorkAfterStart()
        {
            var url = new Uri("wss://www.bitmex.com/realtime");
            using (var client = new WebsocketClient(url))
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
                    .Where(x => x.ToLower().Contains("pong"))
                    .Subscribe(msg =>
                    {
                        receivedCount++;
                        received = msg;

                        if(receivedCount >= 7)
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
    }
}
