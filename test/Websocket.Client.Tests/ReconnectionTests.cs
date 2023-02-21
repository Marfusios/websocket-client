using System;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Exceptions;
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
            _context = new TestContext<SimpleStartup>(_output);
        }

        [Fact]
        public async Task Reconnecting_ShouldWorkAsExpected()
        {
            using var client = _context.CreateClient();
            var receivedCount = 0;
            var receivedEvent = new ManualResetEvent(false);

            client.IsReconnectionEnabled = true;
            client.ReconnectTimeout = TimeSpan.FromSeconds(3);

            client.MessageReceived
                .Where(x => x.MessageType == WebSocketMessageType.Text)
                .Subscribe(msg =>
                {
                    _output.WriteLine($"Received: '{msg}'");
                    receivedCount++;
                });

            await client.Start();
            await Task.Delay(12000);

            _output.WriteLine($"Reconnected {receivedCount} times");
            Assert.InRange(receivedCount, 2, 7);
        }

        [Fact]
        public async Task DisabledReconnecting_ShouldWorkAsExpected()
        {
            using var client = _context.CreateClient();
            var receivedCount = 0;
            var receivedEvent = new ManualResetEvent(false);

            client.IsReconnectionEnabled = false;
            client.ReconnectTimeout = TimeSpan.FromSeconds(1);

            client.MessageReceived
                .Where(x => x.MessageType == WebSocketMessageType.Text)
                .Subscribe(msg =>
                {
                    _output.WriteLine($"Received: '{msg}'");
                    receivedCount++;
                    if (receivedCount >= 2)
                        receivedEvent.Set();
                });

            await client.Start();
            await Task.Delay(3000);
            await client.Stop(WebSocketCloseStatus.InternalServerError, "something strange happened");

            await Task.Delay(3000);

            await client.Start();
            await Task.Delay(1000);

            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

            Assert.Equal(2, receivedCount);
        }

        [Fact]
        public async Task DisabledReconnecting_OnlyNoMessage_ShouldWorkAsExpected()
        {
            using var client = _context.CreateClient();
            var receivedCount = 0;
            var receivedEvent = new ManualResetEvent(false);

            client.IsReconnectionEnabled = true; // enable reconnecting
            client.ReconnectTimeout = null; // disable reconnecting for no message comes from server

            client.MessageReceived
                .Where(x => x.MessageType == WebSocketMessageType.Text)
                .Subscribe(msg =>
                {
                    _output.WriteLine($"Received: '{msg}'");
                    receivedCount++;
                    if (receivedCount >= 2)
                        receivedEvent.Set();
                });

            await client.Start();
            await Task.Delay(3000);
            await client.Reconnect();

            await Task.Delay(3000);

            receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

            Assert.Equal(2, receivedCount);
        }

        [Fact]
        public async Task DisabledReconnecting_ShouldWorkAtRuntime()
        {
            using var client = _context.CreateClient();
            var receivedCount = 0;

            client.IsReconnectionEnabled = true;
            client.ReconnectTimeout = TimeSpan.FromSeconds(1);

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

        [Fact]
        public async Task ManualReconnect_ShouldWorkAsExpected()
        {
            using var client = _context.CreateClient();
            var receivedCount = 0;
            var reconnectedCount = 0;
            var lastReconnectionType = ReconnectionType.Initial;

            client.IsReconnectionEnabled = true;
            client.ReconnectTimeout = null;

            client.MessageReceived
                .Where(x => x.MessageType == WebSocketMessageType.Text)
                .Subscribe(msg =>
                {
                    _output.WriteLine($"Received: '{msg}'");
                    receivedCount++;
                });

            client.ReconnectionHappened.Subscribe(x =>
            {
                _output.WriteLine($"Reconnected: '{x.Type}'");
                reconnectedCount++;
                lastReconnectionType = x.Type;
            });

            await client.Start();

            await Task.Delay(1000);
            await client.Reconnect();

            await Task.Delay(1000);
            await client.Reconnect();

            await Task.Delay(1000);
            await client.Reconnect();

            await Task.Delay(2000);

            _output.WriteLine($"Received message {receivedCount} times and reconnected {receivedCount} times, " +
                              $"last: {lastReconnectionType}");
            Assert.Equal(4, receivedCount);
            Assert.Equal(4, reconnectedCount);
            Assert.Equal(ReconnectionType.ByUser, lastReconnectionType);
        }

        [Fact]
        public async Task ManualReconnect_Advanced_ShouldWorkAsExpected()
        {
            using var client = _context.CreateClient();
            var receivedCount = 0;
            var reconnectedCount = 0;
            var lastReconnectionType = ReconnectionType.Initial;

            client.IsReconnectionEnabled = true;
            client.ReconnectTimeout = null;

            client.MessageReceived
                .Where(x => x.MessageType == WebSocketMessageType.Text)
                .Subscribe(msg =>
                {
                    _output.WriteLine($"Received: '{msg}'");
                    receivedCount++;
                });

            client.ReconnectionHappened.Subscribe(x =>
            {
                _output.WriteLine($"Reconnected: '{x.Type}'");
                reconnectedCount++;
                lastReconnectionType = x.Type;
            });

            await client.Start();

            await Task.Delay(1000);
            await client.Reconnect();

            await Task.Delay(1000);
            await client.Reconnect();
            _ = client.Reconnect();

            await Task.Delay(1000);
            await client.Reconnect();
            await client.ReconnectOrFail();
            await client.ReconnectOrFail();

            await Task.Delay(1000);
            await client.ReconnectOrFail();
            await client.Reconnect();
            await client.ReconnectOrFail();

            await Task.Delay(8000);

            _output.WriteLine($"Received message {receivedCount} times and reconnected {receivedCount} times, " +
                              $"last: {lastReconnectionType}");
            Assert.Equal(10, receivedCount);
            Assert.Equal(10, reconnectedCount);
            Assert.Equal(ReconnectionType.ByUser, lastReconnectionType);
        }

        [Fact]
        public async Task ManualReconnectOrFail_ShouldThrowException()
        {
            using var client = _context.CreateClient();
            var receivedCount = 0;
            var reconnectedCount = 0;
            var lastReconnectionType = ReconnectionType.NoMessageReceived;
            var disconnectionCount = 0;
            DisconnectionInfo disconnectionInfo = null;
            Exception causedException = null;

            client.IsReconnectionEnabled = true;
            client.ReconnectTimeout = null;

            client.MessageReceived
                .Where(x => x.MessageType == WebSocketMessageType.Text)
                .Subscribe(msg =>
                {
                    _output.WriteLine($"Received: '{msg}'");
                    receivedCount++;
                });

            client.ReconnectionHappened.Subscribe(x =>
            {
                _output.WriteLine($"Reconnected: '{x.Type}'");
                reconnectedCount++;
                lastReconnectionType = x.Type;
            });

            client.DisconnectionHappened.Subscribe(x =>
            {
                disconnectionCount++;
                disconnectionInfo = x;
            });

            await client.Start();

            await Task.Delay(1000);

            client.Url = new Uri("wss://google.com");

            try
            {
                await client.ReconnectOrFail();
            }
            catch (WebsocketException e)
            {
                // expected exception
                _output.WriteLine($"Received exception: '{e.Message}'");
                causedException = e;
            }

            await Task.Delay(1000);

            Assert.Equal(2, disconnectionCount);
            Assert.Equal(DisconnectionType.Error, disconnectionInfo.Type);
            Assert.NotNull(disconnectionInfo.Exception);
            Assert.Equal(causedException?.InnerException, disconnectionInfo.Exception);

            Assert.Equal(1, receivedCount);
            Assert.Equal(1, reconnectedCount);
            Assert.Equal(ReconnectionType.Initial, lastReconnectionType);
        }


        [Fact]
        public async Task CancelingReconnection_ViaDisconnectionStream_ShouldWork()
        {
            using var client = _context.CreateClient();
            var receivedCount = 0;
            var reconnectedCount = 0;
            var lastReconnectionType = ReconnectionType.NoMessageReceived;
            var disconnectionCount = 0;
            DisconnectionInfo disconnectionInfo = null;

            client.IsReconnectionEnabled = true;
            client.ReconnectTimeout = null;

            client.MessageReceived
                .Where(x => x.MessageType == WebSocketMessageType.Text)
                .Subscribe(msg =>
                {
                    _output.WriteLine($"Received: '{msg}'");
                    receivedCount++;
                });

            client.ReconnectionHappened.Subscribe(x =>
            {
                _output.WriteLine($"Reconnected: '{x.Type}'");
                reconnectedCount++;
                lastReconnectionType = x.Type;
            });

            client.DisconnectionHappened.Subscribe(x =>
            {
                disconnectionCount++;
                disconnectionInfo = x;

                if (disconnectionCount >= 2)
                    disconnectionInfo.CancelReconnection = true;
            });

            await client.Start();

            await Task.Delay(1000);
            await client.Reconnect();

            await Task.Delay(1000);
            await client.Reconnect();

            await Task.Delay(1000);
            await client.Reconnect();

            await Task.Delay(1000);

            Assert.Equal(2, disconnectionCount);
            Assert.Equal(DisconnectionType.ByUser, disconnectionInfo.Type);
            Assert.Null(disconnectionInfo.Exception);

            Assert.Equal(2, receivedCount);
            Assert.Equal(2, reconnectedCount);
            Assert.Equal(ReconnectionType.ByUser, lastReconnectionType);

            Assert.False(client.IsRunning);
            Assert.False(client.IsStarted);
        }
    }
}
