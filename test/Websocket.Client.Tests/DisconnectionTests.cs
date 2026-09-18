using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Tests.TestServer;
using Xunit;

namespace Websocket.Client.Tests
{
    public class DisconnectionTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task InvalidCloseStatus_ShouldNotifyLostExactlyOnce(bool reconnectionEnabled, bool cancelReconnection)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new RawWebSocketServer();
            using var client = CreateClient(server.Url, reconnectionEnabled);
            var disconnections = new ConcurrentQueue<DisconnectionInfo>();
            var reconnections = new ConcurrentQueue<ReconnectionInfo>();
            using var disconnected = client.DisconnectionHappened.Subscribe(info =>
            {
                info.CancelReconnection = cancelReconnection;
                disconnections.Enqueue(info);
            });
            using var reconnected = client.ReconnectionHappened.Subscribe(reconnections.Enqueue);

            var connectionTask = server.AcceptConnectionAsync(timeout.Token);
            await client.StartOrFail().WaitAsync(timeout.Token);
            using var connection = await connectionTask;

            // Issue #160: a raw close frame with status 1100, rejected by ClientWebSocket.
            await connection.GetStream().WriteAsync(new byte[] { 0x88, 0x02, 0x04, 0x4c }, timeout.Token);

            var shouldReconnect = reconnectionEnabled && !cancelReconnection;
            using var nextConnection = shouldReconnect ? await server.AcceptConnectionAsync(timeout.Token) : null;
            await WaitUntil(() => shouldReconnect ? reconnections.Count == 2 : !client.IsStarted, timeout.Token);

            var info = Assert.Single(disconnections);
            Assert.Equal(DisconnectionType.Lost, info.Type);
            Assert.IsType<WebSocketException>(info.Exception);
            Assert.Null(info.CloseStatus);
            Assert.Null(info.CloseStatusDescription);
            Assert.Equal(shouldReconnect, client.IsStarted);
            Assert.Equal(shouldReconnect, client.IsRunning);
            Assert.Equal(shouldReconnect ? new[] { ReconnectionType.Initial, ReconnectionType.Lost } :
                new[] { ReconnectionType.Initial }, reconnections.Select(x => x.Type));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task ValidCloseStatus_ShouldNotifyByServerExactlyOnce(bool reconnectionEnabled, bool cancelClosing)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new RawWebSocketServer();
            using var client = CreateClient(server.Url, reconnectionEnabled);
            var disconnections = new ConcurrentQueue<DisconnectionInfo>();
            var reconnections = new ConcurrentQueue<ReconnectionInfo>();
            using var disconnected = client.DisconnectionHappened.Subscribe(info =>
            {
                info.CancelClosing = cancelClosing;
                disconnections.Enqueue(info);
            });
            using var reconnected = client.ReconnectionHappened.Subscribe(reconnections.Enqueue);

            var connectionTask = server.AcceptConnectionAsync(timeout.Token);
            await client.StartOrFail().WaitAsync(timeout.Token);
            using var connection = await connectionTask;

            // Status 1000 follows the normal server-close path, including CancelClosing.
            await connection.GetStream().WriteAsync(new byte[] { 0x88, 0x02, 0x03, 0xe8 }, timeout.Token);

            using var nextConnection = reconnectionEnabled ? await server.AcceptConnectionAsync(timeout.Token) : null;
            await WaitUntil(() => reconnectionEnabled ? reconnections.Count == 2 : !client.IsStarted, timeout.Token);

            var info = Assert.Single(disconnections);
            Assert.Equal(DisconnectionType.ByServer, info.Type);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, info.CloseStatus);
            Assert.Equal(string.Empty, info.CloseStatusDescription);
            Assert.Null(info.Exception);
            Assert.Equal(reconnectionEnabled, client.IsStarted);
            Assert.Equal(reconnectionEnabled, client.IsRunning);
            var expectedReconnection = cancelClosing ? ReconnectionType.Lost : ReconnectionType.ByServer;
            Assert.Equal(reconnectionEnabled ? new[] { ReconnectionType.Initial, expectedReconnection } :
                new[] { ReconnectionType.Initial }, reconnections.Select(x => x.Type));
        }

        private static WebsocketClient CreateClient(Uri url, bool reconnectionEnabled)
        {
            return new WebsocketClient(url)
            {
                IsReconnectionEnabled = reconnectionEnabled,
                ReconnectTimeout = null,
                ErrorReconnectTimeout = null,
                LostReconnectTimeout = null
            };
        }

        private static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken)
        {
            while (!condition())
                await Task.Delay(10, cancellationToken);
        }
    }
}
