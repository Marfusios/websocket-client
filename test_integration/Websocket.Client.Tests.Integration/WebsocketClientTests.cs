using System;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Serilog.Events;
using Xunit;
using Xunit.Abstractions;

namespace Websocket.Client.Tests.Integration
{
    public class WebsocketClientTests
    {
        private static readonly Uri WebsocketUrl = new Uri("wss://www.bitmex.com/realtime");
        private readonly ITestOutputHelper _output;

        public WebsocketClientTests(ITestOutputHelper output)
        {
            _output = output;
            InitLogging(_output);
        }

        [Fact]
        public async Task OnStarting_ShouldGetInfoResponse()
        {
            using IWebsocketClient client = new WebsocketClient(WebsocketUrl);
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

        [Fact]
        public async Task SendMessageBeforeStart_ShouldWorkAfterStart()
        {
            using IWebsocketClient client = new WebsocketClient(WebsocketUrl);
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
        public async Task Starting_MultipleTimes_ShouldWorkWithNoExceptions()
        {
            for (int i = 0; i < 3; i++)
            {
                using IWebsocketClient client = new WebsocketClient(WebsocketUrl);
                await client.Start();
                await Task.Delay(i * 20);
            }
        }

        [Fact]
        public async Task DisabledReconnecting_ShouldWorkAsExpected()
        {
            using IWebsocketClient client = new WebsocketClient(WebsocketUrl);
            var receivedCount = 0;
            var receivedEvent = new ManualResetEvent(false);

            client.IsReconnectionEnabled = false;
            client.ReconnectTimeout = TimeSpan.FromSeconds(2);

            client.MessageReceived.Subscribe(msg =>
            {
                receivedCount++;
                if (receivedCount >= 2)
                    receivedEvent.Set();
            });

            await client.Start();
            await Task.Delay(5000);
            await client.Stop(WebSocketCloseStatus.Empty, string.Empty);

            await Task.Delay(5000);

            await client.Start();
            await Task.Delay(1000);

            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

            Assert.Equal(2, receivedCount);
        }

        [Fact]
        public async Task DisabledReconnecting_ShouldWorkAtRuntime()
        {
            using IWebsocketClient client = new WebsocketClient(WebsocketUrl);
            var receivedCount = 0;

            client.IsReconnectionEnabled = true;
            client.ReconnectTimeout = TimeSpan.FromSeconds(5);

            client.MessageReceived.Subscribe(msg =>
            {
                receivedCount++;
                if (receivedCount >= 2)
                    client.IsReconnectionEnabled = false;
            });

            await client.Start();
            await Task.Delay(17000);

            Assert.Equal(2, receivedCount);
        }

        [Fact]
        public async Task OnClose_ShouldWorkCorrectly()
        {
            using IWebsocketClient client = new WebsocketClient(WebsocketUrl);
            client.ReconnectTimeout = TimeSpan.FromSeconds(5);

            string received = null;
            var receivedCount = 0;
            var receivedEvent = new ManualResetEvent(false);
            var disconnectionCount = 0;
            DisconnectionInfo disconnectionInfo = null;

            client.MessageReceived.Subscribe(msg =>
            {
                receivedCount++;
                received = msg.Text;
            });

            client.DisconnectionHappened.Subscribe(x =>
            {
                disconnectionCount++;
                disconnectionInfo = x;
            });

            await client.Start();

            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                var success = await client.Stop(WebSocketCloseStatus.InternalServerError, "server error 500");
                Assert.True(success);
                receivedEvent.Set();
            });

            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

            Assert.NotNull(received);
            Assert.Equal(1, receivedCount);

            var nativeClient = client.NativeClient;
            Assert.NotNull(nativeClient);
            Assert.Equal(1, disconnectionCount);
            Assert.Equal(DisconnectionType.ByUser, disconnectionInfo.Type);
            Assert.Equal(WebSocketCloseStatus.InternalServerError, disconnectionInfo.CloseStatus);
            Assert.Equal("server error 500", disconnectionInfo.CloseStatusDescription);
            Assert.Equal(WebSocketState.Closed, nativeClient.State);
            Assert.Equal(WebSocketCloseStatus.InternalServerError, nativeClient.CloseStatus);
            Assert.Equal("server error 500", nativeClient.CloseStatusDescription);

            // check that reconnection is disabled
            await Task.Delay(7000);
            Assert.Equal(1, receivedCount);
        }


        private static void InitLogging(ITestOutputHelper output)
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.TestOutput(output, LogEventLevel.Verbose)
                .CreateLogger();
        }
    }
}
