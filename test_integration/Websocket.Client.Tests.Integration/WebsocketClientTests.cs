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
                    received = msg.Text;
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
                    .Where(x => x.Text.ToLower().Contains("pong"))
                    .Subscribe(msg =>
                    {
                        receivedCount++;
                        received = msg.Text;

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

        [Fact]
        public async Task SendBinaryMessage_ShouldWork()
        {
            var url = new Uri("wss://www.bitmex.com/realtime");
            using (var client = new WebsocketClient(url))
            {
                string received = null;
                var receivedEvent = new ManualResetEvent(false);

                client.MessageReceived.Subscribe(msg =>
                {
                    var msgText = msg.Text ?? string.Empty;
                    if (msgText.Contains("Unrecognized request"))
                    {
                        received = msgText;
                        receivedEvent.Set();
                    }
                });

                await client.Start();
                await client.Send(new byte[] {10, 14, 15, 16});

                receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

                Assert.NotNull(received);
            }
        }
    }
}
