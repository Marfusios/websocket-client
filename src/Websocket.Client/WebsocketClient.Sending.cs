using Microsoft.Extensions.Logging;
using System;
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
        private readonly Channel<ArraySegment<byte>> _messagesBinaryToSendQueue = Channel.CreateUnbounded<ArraySegment<byte>>(new UnboundedChannelOptions()
        {
            SingleReader = true,
            SingleWriter = false
        });


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

            return _messagesBinaryToSendQueue.Writer.TryWrite(new ArraySegment<byte>(message));
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

            return _messagesBinaryToSendQueue.Writer.TryWrite(message);
        }

        /// <summary>
        /// Send text message to the websocket channel. 
        /// It doesn't use a sending queue, 
        /// beware of issue while sending two messages in the exact same time 
        /// on the full .NET Framework platform
        /// </summary>
        /// <param name="message">Message to be sent</param>
        /// <param name="cancellation">Cancellation token</param>
        public Task SendInstant(string message, CancellationToken cancellation)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            return SendInternalSynchronized(new RequestTextMessage(message), cancellation);
        }

        /// <summary>
        /// Send binary message to the websocket channel. 
        /// It doesn't use a sending queue, 
        /// beware of issue while sending two messages in the exact same time 
        /// on the full .NET Framework platform
        /// </summary>
        /// <param name="message">Message to be sent</param>
        /// <param name="cancellation">Cancellation token</param>
        public Task SendInstant(byte[] message, CancellationToken cancellation)
        {
            return SendInternalSynchronized(new ArraySegment<byte>(message), cancellation);
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
            try
            {
                while (await _messagesTextToSendQueue.Reader.WaitToReadAsync())
                {
                    while (_messagesTextToSendQueue.Reader.TryRead(out var message))
                    {
                        try
                        {
                            await SendInternalSynchronized(message, _cancellationTotal?.Token ?? CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, L("Failed to send text message: '{message}'. Error: {error}"), Name, message, e.Message);
                        }
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // task was canceled, ignore
            }
            catch (OperationCanceledException)
            {
                // operation was canceled, ignore
            }
            catch (Exception e)
            {
                if (_cancellationTotal?.IsCancellationRequested == true || _disposing)
                {
                    // disposing/canceling, do nothing and exit
                    return;
                }

                _logger.LogTrace(L("Sending text thread failed, error: {error}. Creating a new sending thread."), Name, e.Message);
                StartBackgroundThreadForSendingText();
            }

        }

        private async Task SendBinaryFromQueue()
        {
            try
            {
                while (await _messagesBinaryToSendQueue.Reader.WaitToReadAsync())
                {
                    while (_messagesBinaryToSendQueue.Reader.TryRead(out var message))
                    {
                        try
                        {
                            await SendInternalSynchronized(message, _cancellationTotal?.Token ?? CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, L("Failed to send binary message: '{message}'. Error: {error}"), Name, message, e.Message);
                        }
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // task was canceled, ignore
            }
            catch (OperationCanceledException)
            {
                // operation was canceled, ignore
            }
            catch (Exception e)
            {
                if (_cancellationTotal?.IsCancellationRequested == true || _disposing)
                {
                    // disposing/canceling, do nothing and exit
                    return;
                }

                _logger.LogTrace(L("Sending binary thread failed, error: {error}. Creating a new sending thread."), Name, e.Message);
                StartBackgroundThreadForSendingBinary();
            }

        }

        private void StartBackgroundThreadForSendingText()
        {
            _ = Task.Factory.StartNew(_ => SendTextFromQueue(), TaskCreationOptions.LongRunning, _cancellationTotal?.Token ?? CancellationToken.None);
        }

        private void StartBackgroundThreadForSendingBinary()
        {
            _ = Task.Factory.StartNew(_ => SendBinaryFromQueue(), TaskCreationOptions.LongRunning, _cancellationTotal?.Token ?? CancellationToken.None);
        }

        private async Task SendInternalSynchronized(RequestMessage message, CancellationToken cancellation)
        {
            using (await _locker.LockAsync(cancellation))
            {
                await SendInternal(message, cancellation);
            }
        }

        private async Task SendInternal(RequestMessage message, CancellationToken cancellation)
        {
            if (!IsClientConnected())
            {
                _logger.LogDebug(L("Client is not connected to server, cannot send: {message}"), Name, message);
                return;
            }

            _logger.LogTrace(L("Sending: {message}"), Name, message);

            ReadOnlyMemory<byte> payload;

            switch (message)
            {
                case RequestTextMessage textMessage:
                    payload = MemoryMarshal.AsMemory<byte>(GetEncoding().GetBytes(textMessage.Text));
                    break;
                case RequestBinaryMessage binaryMessage:
                    payload = MemoryMarshal.AsMemory<byte>(binaryMessage.Data);
                    break;
                case RequestBinarySegmentMessage segmentMessage:
                    payload = segmentMessage.Data.AsMemory();
                    break;
                default:
                    throw new ArgumentException($"Unknown message type: {message.GetType()}");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _cancellation?.Token ?? CancellationToken.None);
            await _client!
                .SendAsync(payload, WebSocketMessageType.Text, true, cts.Token)
                .ConfigureAwait(false);
        }

        private async Task SendInternalSynchronized(ArraySegment<byte> message, CancellationToken cancellation)
        {
            using (await _locker.LockAsync(cancellation))
            {
                await SendInternal(message, cancellation);
            }
        }

        private async Task SendInternal(ArraySegment<byte> payload, CancellationToken cancellation)
        {
            if (!IsClientConnected())
            {
                _logger.LogDebug(L("Client is not connected to server, cannot send binary, length: {length}"), Name, payload.Count);
                return;
            }

            _logger.LogTrace(L("Sending binary, length: {length}"), Name, payload.Count);

            await _client!
                .SendAsync(payload, WebSocketMessageType.Binary, true, cancellation)
                .ConfigureAwait(false);
        }
    }
}
