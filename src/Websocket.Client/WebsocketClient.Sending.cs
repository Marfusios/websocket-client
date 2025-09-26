using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
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

        private static readonly byte[] _emptyArray = { };

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

            return _messagesTextToSendQueue.Writer.TryWrite(new RequestTextMessage(message));
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

            return SendInternalSynchronized(new RequestTextMessage(message));
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

            return _messagesTextToSendQueue.Writer.TryWrite(new RequestBinaryMessage(message));
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

            return _messagesTextToSendQueue.Writer.TryWrite(new RequestBinarySegmentMessage(message));
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

            return _messagesTextToSendQueue.Writer.TryWrite(new RequestBinarySequenceMessage(message));
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
                            _logger.LogError(e, L("Failed to send text message: '{message}'. Error: {error}"), Name, message, e.Message);
                        }
                    }
                }
            }
            catch (TaskCanceledException e)
            {
                // task was canceled, ignore
                _logger.LogDebug(e, L("Sending text thread failed, error: {error}. Shutting down."), Name, e.Message);
            }
            catch (OperationCanceledException e)
            {
                // operation was canceled, ignore
                _logger.LogDebug(e, L("Sending text thread failed, error: {error}. Shutting down."), Name, e.Message);
            }
            catch (Exception e)
            {
                if (_cancellationTotal?.IsCancellationRequested == true || _disposing)
                {
                    // disposing/canceling, do nothing and exit
                    _logger.LogDebug(e, L("Sending text thread failed, error: {error}. Shutting down."), Name, e.Message);
                    TextSenderRunning = false;
                    return;
                }

                _logger.LogDebug(e, L("Sending text thread failed, error: {error}. Creating a new sending thread."), Name, e.Message);
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
                            _logger.LogError(e, L("Failed to send binary message: '{message}'. Error: {error}"), Name, message, e.Message);
                        }
                    }
                }
            }
            catch (TaskCanceledException e)
            {
                // task was canceled, ignore
                _logger.LogDebug(e, L("Sending binary thread failed, error: {error}. Shutting down."), Name, e.Message);
            }
            catch (OperationCanceledException e)
            {
                // operation was canceled, ignore
                _logger.LogDebug(e, L("Sending binary thread failed, error: {error}. Shutting down."), Name, e.Message);
            }
            catch (Exception e)
            {
                if (_cancellationTotal?.IsCancellationRequested == true || _disposing)
                {
                    // disposing/canceling, do nothing and exit
                    _logger.LogDebug(e, L("Sending binary thread failed, error: {error}. Shutting down."), Name, e.Message);
                    BinarySenderRunning = false;
                    return;
                }

                _logger.LogDebug(e, L("Sending binary thread failed, error: {error}. Creating a new sending thread."), Name, e.Message);
                StartBackgroundThreadForSendingBinary();
            }
            BinarySenderRunning = false;
        }

        private void StartBackgroundThreadForSendingText()
        {
            _ = Task.Factory.StartNew(_ => SendTextFromQueue(), TaskCreationOptions.LongRunning, _cancellationTotal?.Token ?? CancellationToken.None);
        }

        private void StartBackgroundThreadForSendingBinary()
        {
            _ = Task.Factory.StartNew(_ => SendBinaryFromQueue(), TaskCreationOptions.LongRunning, _cancellationTotal?.Token ?? CancellationToken.None);
        }

        private async Task SendInternalSynchronized(RequestMessage message)
        {
            using (await _locker.LockAsync())
            {
                await SendInternal(message);
            }
        }

        private async Task SendInternal(RequestMessage message)
        {
            switch (message)
            {
                case RequestTextMessage textMessage:
                    await SendTextMessage(textMessage).ConfigureAwait(false);
                    break;
                case RequestBinaryMessage binaryMessage:
                    await SendBinaryMessage(binaryMessage).ConfigureAwait(false);
                    break;
                case RequestBinarySegmentMessage segmentMessage:
                    await SendBinarySegmentMessage(segmentMessage).ConfigureAwait(false);
                    break;
                case RequestBinarySequenceMessage sequenceMessage:
                    await SendBinarySequenceMessage(sequenceMessage).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentException($"Unknown message type: {message.GetType()}");
            }
        }

        private async Task SendTextMessage(RequestTextMessage textMessage)
        {
            var payload = MemoryMarshal.AsMemory<byte>(GetEncoding().GetBytes(textMessage.Text));
            await SendInternal(payload, WebSocketMessageType.Text).ConfigureAwait(false);
        }

        private async Task SendBinaryMessage(RequestBinaryMessage binaryMessage)
        {
            var payload = MemoryMarshal.AsMemory<byte>(binaryMessage.Data);
            await SendInternal(payload, WebSocketMessageType.Text).ConfigureAwait(false);
        }

        private async Task SendBinarySegmentMessage(RequestBinarySegmentMessage segmentMessage)
        {
            await SendInternal(segmentMessage.Data, WebSocketMessageType.Text).ConfigureAwait(false);
        }

        private async Task SendBinarySequenceMessage(RequestBinarySequenceMessage sequenceMessage)
        {
            await SendInternal(sequenceMessage.Data, WebSocketMessageType.Text).ConfigureAwait(false);
        }

        private async Task SendInternalSynchronized(ReadOnlySequence<byte> message)
        {
            using (await _locker.LockAsync())
            {
                await SendInternal(message, WebSocketMessageType.Binary);
            }
        }
        private async Task SendInternalSynchronized(ReadOnlyMemory<byte> message)
        {
            using (await _locker.LockAsync())
            {
                await SendInternal(message, WebSocketMessageType.Binary);
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

            foreach (var memory in payload)
            {
                await _client!
                    .SendAsync(memory, messageType, false, _cancellation?.Token ?? CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await _client!
                .SendAsync(_emptyArray, messageType, true, _cancellation?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);
        }

        private async Task SendInternal(ReadOnlyMemory<byte> payload, WebSocketMessageType messageType)
        {
            if (!CheckClientConnection(payload.Length))
            {
                return;
            }

            await _client!
                .SendAsync(payload, messageType, true, _cancellation?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);
        }

        private bool CheckClientConnection(long length)
        {
            if (!IsClientConnected())
            {
                _logger.LogDebug(L("Client is not connected to server, cannot send binary, length: {length}"), Name, length);
                return false;
            }

            _logger.LogTrace(L("Sending binary, length: {length}"), Name, length);
            return true;
        }
    }
}
