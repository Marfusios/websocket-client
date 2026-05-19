using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace Websocket.Client.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
[RankColumn]
public class ClientReceiveBenchmarks
{
    private byte[][] _messages = null!;

    [Params(100, 1000)]
    public int MessageCount { get; set; }

    [Params(64, 1024)]
    public int MessageSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var message = Encoding.UTF8.GetBytes(new string('x', MessageSize));
        _messages = Enumerable.Range(0, MessageCount)
            .Select(_ => message.ToArray())
            .ToArray();
    }

    [Benchmark(Description = "Current client receive text")]
    public async Task<int> Current_ClientReceiveTextMessages()
    {
        var socket = new ScriptedTextWebSocket(_messages);
        using var client = new WebsocketClient(
            new Uri("wss://localhost/benchmark"),
            null,
            (_, _) => Task.FromResult<WebSocket>(socket));
        client.ReconnectTimeout = null;
        client.ErrorReconnectTimeout = null;
        client.IsReconnectionEnabled = false;

        var receivedCount = 0;
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = client.MessageReceived.Subscribe(message =>
        {
            if (message.Text?.Length != MessageSize)
            {
                completion.TrySetException(new InvalidOperationException("Received unexpected message."));
                return;
            }

            if (Interlocked.Increment(ref receivedCount) == MessageCount)
                completion.TrySetResult(receivedCount);
        });

        await client.Start().ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
#if NET472
        return await WaitAsync(completion.Task, timeout.Token).ConfigureAwait(false);
#else
        return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
#endif
    }

#if NET472
    private static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completedTask = await Task.WhenAny(task, cancellationTask).ConfigureAwait(false);

        if (completedTask == task)
            return await task.ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException("The benchmark operation timed out.");
    }
#endif

    private sealed class ScriptedTextWebSocket : WebSocket
    {
        private readonly byte[][] _messages;
        private int _messageIndex;
        private int _messageOffset;
        private bool _closeReturned;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;
        private WebSocketState _state = WebSocketState.Open;

        public ScriptedTextWebSocket(byte[][] messages)
        {
            _messages = messages;
        }

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;

        public override string? CloseStatusDescription => _closeStatusDescription;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
#if NET472
            return await ReceiveSegmentAsync(buffer, cancellationToken).ConfigureAwait(false);
#else
            var result = await ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            return new WebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage);
#endif
        }

#if !NET472
        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_messageIndex >= _messages.Length)
            {
                if (_closeReturned)
                    return WaitForCancellation(cancellationToken);

                _closeReturned = true;
                _state = WebSocketState.CloseReceived;
                _closeStatus = WebSocketCloseStatus.NormalClosure;
                _closeStatusDescription = "Benchmark complete";
                return ValueTask.FromResult(new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }

            var message = _messages[_messageIndex];
            var count = Math.Min(buffer.Length, message.Length - _messageOffset);
            message.AsMemory(_messageOffset, count).CopyTo(buffer);
            _messageOffset += count;

            var endOfMessage = _messageOffset == message.Length;
            if (endOfMessage)
            {
                _messageIndex++;
                _messageOffset = 0;
            }

            return ValueTask.FromResult(new ValueWebSocketReceiveResult(count, WebSocketMessageType.Text, endOfMessage));
        }
#endif

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

#if NET472
        private async Task<WebSocketReceiveResult> ReceiveSegmentAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_messageIndex >= _messages.Length)
            {
                if (_closeReturned)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                }

                _closeReturned = true;
                _state = WebSocketState.CloseReceived;
                _closeStatus = WebSocketCloseStatus.NormalClosure;
                _closeStatusDescription = "Benchmark complete";
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            var message = _messages[_messageIndex];
            var count = Math.Min(buffer.Count, message.Length - _messageOffset);
            Buffer.BlockCopy(message, _messageOffset, buffer.Array!, buffer.Offset, count);
            _messageOffset += count;

            var endOfMessage = _messageOffset == message.Length;
            if (endOfMessage)
            {
                _messageIndex++;
                _messageOffset = 0;
            }

            return new WebSocketReceiveResult(count, WebSocketMessageType.Text, endOfMessage);
        }
#else
        private static async ValueTask<ValueWebSocketReceiveResult> WaitForCancellation(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }
#endif
    }
}
