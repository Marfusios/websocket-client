﻿using System;
using System.Net.WebSockets;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Websocket.Client
{
    public partial class WebsocketClient
    {
        private readonly Channel<string> _messagesTextToSendQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions()
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
        /// It inserts the message to the queue and actual sending is done on an other thread
        /// </summary>
        /// <param name="message">Text message to be sent</param>
        public void Send(string message)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            _messagesTextToSendQueue.Writer.TryWrite(message);
        }

        /// <summary>
        /// Send binary message to the websocket channel. 
        /// It inserts the message to the queue and actual sending is done on an other thread
        /// </summary>
        /// <param name="message">Binary message to be sent</param>
        public void Send(byte[] message)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            _messagesBinaryToSendQueue.Writer.TryWrite(new ArraySegment<byte>(message));
        }

        /// <summary>
        /// Send binary message to the websocket channel. 
        /// It inserts the message to the queue and actual sending is done on an other thread
        /// </summary>
        /// <param name="message">Binary message to be sent</param>
        public void Send(ArraySegment<byte> message)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            _messagesBinaryToSendQueue.Writer.TryWrite(message);
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

            return SendInternalSynchronized(message);
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
            return SendInternalSynchronized(new ArraySegment<byte>(message));
        }

        /// <summary>
        /// Stream/publish fake message (via 'MessageReceived' observable).
        /// Use for testing purposes to simulate a server message. 
        /// </summary>
        /// <param name="message">Message to be stream</param>
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
                            await SendInternalSynchronized(message).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, L($"Failed to send text message: '{message}'. Error: {e.Message}"));
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
                if (_cancellationTotal.IsCancellationRequested || _disposing)
                {
                    // disposing/canceling, do nothing and exit
                    return;
                }

                _logger.LogTrace(L($"Sending text thread failed, error: {e.Message}. Creating a new sending thread."));
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
                            await SendInternalSynchronized(message).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, L($"Failed to send binary message: '{message}'. Error: {e.Message}"));
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
                if (_cancellationTotal.IsCancellationRequested || _disposing)
                {
                    // disposing/canceling, do nothing and exit
                    return;
                }

                _logger.LogTrace(L($"Sending binary thread failed, error: {e.Message}. Creating a new sending thread."));
                StartBackgroundThreadForSendingBinary();
            }

        }

        private void StartBackgroundThreadForSendingText()
        {
            _ = Task.Factory.StartNew(_ => SendTextFromQueue(), TaskCreationOptions.LongRunning, _cancellationTotal.Token);
        }

        private void StartBackgroundThreadForSendingBinary()
        {
            _ = Task.Factory.StartNew(_ => SendBinaryFromQueue(), TaskCreationOptions.LongRunning, _cancellationTotal.Token);
        }

        private async Task SendInternalSynchronized(string message)
        {
            using (await _locker.LockAsync())
            {
                await SendInternal(message);
            }
        }

        private async Task SendInternal(string message)
        {
            if (!IsClientConnected())
            {
                _logger.LogDebug(L($"Client is not connected to server, cannot send:  {message}"));
                return;
            }

            _logger.LogTrace(L($"Sending:  {message}"));
            var buffer = GetEncoding().GetBytes(message);
            var messageSegment = new ArraySegment<byte>(buffer);
            await _client
                .SendAsync(messageSegment, WebSocketMessageType.Text, true, _cancellation.Token)
                .ConfigureAwait(false);
        }

        private async Task SendInternalSynchronized(ArraySegment<byte> message)
        {
            using (await _locker.LockAsync())
            {
                await SendInternal(message);
            }
        }

        private async Task SendInternal(ArraySegment<byte> message)
        {
            if (!IsClientConnected())
            {
                _logger.LogDebug(L($"Client is not connected to server, cannot send binary, length: {message.Count}"));
                return;
            }

            _logger.LogTrace(L($"Sending binary, length: {message.Count}"));

            await _client
                .SendAsync(message, WebSocketMessageType.Binary, true, _cancellation.Token)
                .ConfigureAwait(false);
        }
    }
}
