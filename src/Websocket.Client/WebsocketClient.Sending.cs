using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Net.WebSockets;
#if NETSTANDARD2_0
using System.Runtime.InteropServices;
#endif
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Websocket.Client
{
    public partial class WebsocketClient
    {
        private readonly Channel<RequestMessage> _messagesTextToSendQueue = Channel.CreateUnbounded<RequestMessage>(new UnboundedChannelOptions()
        {
            SingleReader = true,
            SingleWriter = false
        });
        private readonly Channel<ReadOnlySequence<byte>> _messagesBinaryToSendQueue = Channel.CreateUnbounded<ReadOnlySequence<byte>>(new UnboundedChannelOptions()
        {
            SingleReader = true,
            SingleWriter = false
        });

        private static readonly byte[] _emptyArray = Array.Empty<byte>();

        /// <inheritdoc />
        public bool TextSenderRunning { get; private set; }

        /// <inheritdoc />
        public bool BinarySenderRunning { get; private set; }

        /// <summary>
        /// Send text message to the websocket channel. 
        /// It inserts the message to the queue and actual sending is done on another thread
        /// </summary>
        /// <param name="message">Text message to be sent</param>
        /// <returns>true if the message was written to the queue</returns>
        public bool Send(string message)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            return _messagesTextToSendQueue.Writer.TryWrite(RequestMessage.TextMessage(message));
        }

        /// <summary>
        /// Send binary message to the websocket channel. 
        /// It inserts the message to the queue and actual sending is done on another thread
        /// </summary>
        /// <param name="message">Binary message to be sent</param>
        /// <returns>true if the message was written to the queue</returns>
        public bool Send(byte[] message)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            return _messagesBinaryToSendQueue.Writer.TryWrite(new ReadOnlySequence<byte>(message));
        }

        /// <summary>
        /// Send binary message to the websocket channel. 
        /// It inserts the message to the queue and actual sending is done on another thread
        /// </summary>
        /// <param name="message">Binary message to be sent</param>
        /// <returns>true if the message was written to the queue</returns>
        public bool Send(ArraySegment<byte> message)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            return _messagesBinaryToSendQueue.Writer.TryWrite(new ReadOnlySequence<byte>(message));
        }

        /// <summary>
        /// Send binary message to the websocket channel. 
        /// It inserts the message to the queue and actual sending is done on another thread
        /// </summary>
        /// <param name="message">Binary message to be sent</param>
        /// <returns>true if the message was written to the queue</returns>
        public bool Send(ReadOnlySequence<byte> message)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            return _messagesBinaryToSendQueue.Writer.TryWrite(message);
        }

        /// <summary>
        /// Send text message to the websocket channel. 
        /// It doesn't use a sending queue, 
        /// beware of issue while sending two messages in the exact same time 
        /// on the full .NET Framework platform
        /// </summary>
        /// <param name="message">Message to be sent</param>
        public Task SendInstant(string message)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            return SendInternalSynchronized(RequestMessage.TextMessage(message));
        }

        /// <summary>
        /// Send binary message to the websocket channel. 
        /// It doesn't use a sending queue, 
        /// beware of issue while sending two messages in the exact same time 
        /// on the full .NET Framework platform
        /// </summary>
        /// <param name="message">Message to be sent</param>
        public Task SendInstant(byte[] message)
        {
            return SendInternalSynchronized(message);
        }

        /// <summary>
        /// Send already converted text message to the websocket channel. 
        /// Use this method to avoid double serialization of the text message.
        /// It inserts the message to the queue and actual sending is done on another thread
        /// </summary>
        /// <param name="message">Message to be sent</param>
        /// <returns>true if the message was written to the queue</returns>
        public bool SendAsText(byte[] message)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            return _messagesTextToSendQueue.Writer.TryWrite(RequestMessage.BinaryMessage(message));
        }

        /// <summary>
        /// Send already converted text message to the websocket channel. 
        /// Use this method to avoid double serialization of the text message.
        /// It inserts the message to the queue and actual sending is done on another thread
        /// </summary>
        /// <param name="message">Message to be sent</param>
        /// <returns>true if the message was written to the queue</returns>
        public bool SendAsText(ArraySegment<byte> message)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            return _messagesTextToSendQueue.Writer.TryWrite(RequestMessage.BinarySegmentMessage(message));
        }

        /// <summary>
        /// Send already converted text message to the websocket channel. 
        /// Use this method to avoid double serialization of the text message.
        /// It inserts the message to the queue and actual sending is done on another thread
        /// </summary>
        /// <param name="message">Message to be sent</param>
        /// <returns>true if the message was written to the queue</returns>
        public bool SendAsText(ReadOnlySequence<byte> message)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            return _messagesTextToSendQueue.Writer.TryWrite(RequestMessage.BinarySequenceMessage(message));
        }

        /// <summary>
        /// Stream/publish fake message (via 'MessageReceived' observable).
        /// Use for testing purposes to simulate a server message. 
        /// </summary>
        /// <param name="message">Message to be streamed</param>
        public void StreamFakeMessage(ResponseMessage message)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            _messageReceivedSubject.OnNext(message);
        }


        private async Task SendTextFromQueue()
        {
            TextSenderRunning = true;
            try
            {
                while (await _messagesTextToSendQueue.Reader.WaitToReadAsync())
                {
                    while (_messagesTextToSendQueue.Reader.TryRead(out var message))
                    {
                        try
                        {
                            await SendInternalSynchronized(message).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, LogPrefix + "Failed to send text message: '{message}'. Error: {error}", Name, message, e.Message);
                        }
                    }
                }
            }
            catch (TaskCanceledException e)
            {
                // task was canceled, ignore
                _logger.LogDebug(e, LogPrefix + "Sending text thread failed, error: {error}. Shutting down.", Name, e.Message);
            }
            catch (OperationCanceledException e)
            {
                // operation was canceled, ignore
                _logger.LogDebug(e, LogPrefix + "Sending text thread failed, error: {error}. Shutting down.", Name, e.Message);
            }
            catch (Exception e)
            {
                if (_cancellationTotal?.IsCancellationRequested == true || _disposing)
                {
                    // disposing/canceling, do nothing and exit
                    _logger.LogDebug(e, LogPrefix + "Sending text thread failed, error: {error}. Shutting down.", Name, e.Message);
                    TextSenderRunning = false;
                    return;
                }

                _logger.LogDebug(e, LogPrefix + "Sending text thread failed, error: {error}. Creating a new sending thread.", Name, e.Message);
                StartBackgroundThreadForSendingText();
            }
            TextSenderRunning = false;
        }

        private async Task SendBinaryFromQueue()
        {
            BinarySenderRunning = true;
            try
            {
                while (await _messagesBinaryToSendQueue.Reader.WaitToReadAsync())
                {
                    while (_messagesBinaryToSendQueue.Reader.TryRead(out var message))
                    {
                        try
                        {
                            await SendInternalSynchronized(message).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, LogPrefix + "Failed to send binary message: '{message}'. Error: {error}", Name, message, e.Message);
                        }
                    }
                }
            }
            catch (TaskCanceledException e)
            {
                // task was canceled, ignore
                _logger.LogDebug(e, LogPrefix + "Sending binary thread failed, error: {error}. Shutting down.", Name, e.Message);
            }
            catch (OperationCanceledException e)
            {
                // operation was canceled, ignore
                _logger.LogDebug(e, LogPrefix + "Sending binary thread failed, error: {error}. Shutting down.", Name, e.Message);
            }
            catch (Exception e)
            {
                if (_cancellationTotal?.IsCancellationRequested == true || _disposing)
                {
                    // disposing/canceling, do nothing and exit
                    _logger.LogDebug(e, LogPrefix + "Sending binary thread failed, error: {error}. Shutting down.", Name, e.Message);
                    BinarySenderRunning = false;
                    return;
                }

                _logger.LogDebug(e, LogPrefix + "Sending binary thread failed, error: {error}. Creating a new sending thread.", Name, e.Message);
                StartBackgroundThreadForSendingBinary();
            }
            BinarySenderRunning = false;
        }

        private void StartBackgroundThreadForSendingText()
        {
            _ = Task.Run(SendTextFromQueue, _cancellationTotal?.Token ?? CancellationToken.None);
        }

        private void StartBackgroundThreadForSendingBinary()
        {
            _ = Task.Run(SendBinaryFromQueue, _cancellationTotal?.Token ?? CancellationToken.None);
        }

        private async Task SendInternalSynchronized(RequestMessage message)
        {
            using (await _locker.LockAsync().ConfigureAwait(false))
            {
                await SendInternal(message).ConfigureAwait(false);
            }
        }

        private async Task SendInternal(RequestMessage message)
        {
            switch (message.Type)
            {
                case RequestMessageType.Text:
                    await SendTextMessage(message.Text!).ConfigureAwait(false);
                    break;
                case RequestMessageType.Binary:
                    await SendBinaryMessage(message.Binary!).ConfigureAwait(false);
                    break;
                case RequestMessageType.BinarySegment:
                    await SendBinarySegmentMessage(message.BinarySegment).ConfigureAwait(false);
                    break;
                case RequestMessageType.BinarySequence:
                    await SendBinarySequenceMessage(message.BinarySequence).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentException($"Unknown message type: {message.Type}");
            }
        }

        private async Task SendTextMessage(string text)
        {
            var encoding = GetEncoding();
            var byteCount = encoding.GetByteCount(text);
            var buffer = ArrayPool<byte>.Shared.Rent(byteCount);

            try
            {
                var written = encoding.GetBytes(text, 0, text.Length, buffer, 0);
                await SendInternal(buffer.AsMemory(0, written), WebSocketMessageType.Text).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task SendBinaryMessage(byte[] binary)
        {
            var payload = new ReadOnlyMemory<byte>(binary);
            await SendInternal(payload, WebSocketMessageType.Text).ConfigureAwait(false);
        }

        private async Task SendBinarySegmentMessage(ArraySegment<byte> segment)
        {
            await SendInternal(segment, WebSocketMessageType.Text).ConfigureAwait(false);
        }

        private async Task SendBinarySequenceMessage(ReadOnlySequence<byte> sequence)
        {
            await SendInternal(sequence, WebSocketMessageType.Text).ConfigureAwait(false);
        }

        private async Task SendInternalSynchronized(ReadOnlySequence<byte> message)
        {
            using (await _locker.LockAsync().ConfigureAwait(false))
            {
                await SendInternal(message, WebSocketMessageType.Binary).ConfigureAwait(false);
            }
        }
        private async Task SendInternalSynchronized(ReadOnlyMemory<byte> message)
        {
            using (await _locker.LockAsync().ConfigureAwait(false))
            {
                await SendInternal(message, WebSocketMessageType.Binary).ConfigureAwait(false);
            }
        }

        private async Task SendInternal(ReadOnlySequence<byte> payload, WebSocketMessageType messageType)
        {
            if (payload.IsSingleSegment)
            {
                await SendInternal(payload.First, messageType).ConfigureAwait(false);
                return;
            }

            if (!CheckClientConnection(payload.Length))
            {
                return;
            }

            var client = _client!;
            var cancellationToken = _cancellation?.Token ?? CancellationToken.None;
            var segments = payload.GetEnumerator();
            if (!segments.MoveNext())
            {
#if NETSTANDARD2_0
                await client
                    .SendAsync(new ArraySegment<byte>(_emptyArray), messageType, true, cancellationToken)
                    .ConfigureAwait(false);
#else
                await client
                    .SendAsync(_emptyArray, messageType, true, cancellationToken)
                    .ConfigureAwait(false);
#endif
                return;
            }

            var current = segments.Current;
            while (segments.MoveNext())
            {
#if NETSTANDARD2_0
                await SendAsync(client, current, messageType, false, cancellationToken).ConfigureAwait(false);
#else
                await client
                    .SendAsync(current, messageType, false, cancellationToken)
                    .ConfigureAwait(false);
#endif

                current = segments.Current;
            }

#if NETSTANDARD2_0
            await SendAsync(client, current, messageType, true, cancellationToken).ConfigureAwait(false);
#else
            await client
                .SendAsync(current, messageType, true, cancellationToken)
                .ConfigureAwait(false);
#endif
        }

        private async Task SendInternal(ReadOnlyMemory<byte> payload, WebSocketMessageType messageType)
        {
            if (!CheckClientConnection(payload.Length))
            {
                return;
            }

            var client = _client!;
            var cancellationToken = _cancellation?.Token ?? CancellationToken.None;

#if NETSTANDARD2_0
            await SendAsync(client, payload, messageType, true, cancellationToken).ConfigureAwait(false);
#else
            await client
                .SendAsync(payload, messageType, true, cancellationToken)
                .ConfigureAwait(false);
#endif
        }

#if NETSTANDARD2_0
        private static Task SendAsync(WebSocket client, ReadOnlyMemory<byte> payload,
            WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (MemoryMarshal.TryGetArray(payload, out var segment))
            {
                return client.SendAsync(segment, messageType, endOfMessage, cancellationToken);
            }

            var buffer = payload.ToArray();
            return client.SendAsync(new ArraySegment<byte>(buffer), messageType, endOfMessage, cancellationToken);
        }
#endif

        private bool CheckClientConnection(long length)
        {
            if (!IsClientConnected())
            {
                _logger.LogDebug(LogPrefix + "Client is not connected to server, cannot send binary, length: {length}", Name, length);
                return false;
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace(LogPrefix + "Sending binary, length: {length}", Name, length);
            }

            return true;
        }
    }
}
