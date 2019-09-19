using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Logging;

namespace Websocket.Client
{
    /// <summary>
    /// A simple websocket client with built-in reconnection and error handling
    /// </summary>
    public class WebsocketClient : IWebsocketClient
    {
        private static readonly ILog Logger = GetLogger();

        private Uri _url;
        private Timer _lastChanceTimer;
        private readonly Func<ClientWebSocket> _clientFactory;

        private DateTime _lastReceivedMsg = DateTime.UtcNow; 

        private bool _disposing;
        private bool _reconnecting;
        private bool _isReconnectionEnabled = true;
        private ClientWebSocket _client;
        private CancellationTokenSource _cancellation;
        private CancellationTokenSource _cancellationTotal;

        private readonly Subject<ResponseMessage> _messageReceivedSubject = new Subject<ResponseMessage>();
        private readonly Subject<ReconnectionType> _reconnectionSubject = new Subject<ReconnectionType>();
        private readonly Subject<DisconnectionType> _disconnectedSubject = new Subject<DisconnectionType>();

        private readonly BlockingCollection<string> _messagesTextToSendQueue = new BlockingCollection<string>();
        private readonly BlockingCollection<byte[]> _messagesBinaryToSendQueue = new BlockingCollection<byte[]>();


        /// <summary>
        /// A simple websocket client with built-in reconnection and error handling
        /// </summary>
        /// <param name="url">Target websocket url (wss://)</param>
        /// <param name="clientFactory">Optional factory for native ClientWebSocket, use it whenever you need some custom features (proxy, settings, etc)</param>
        public WebsocketClient(Uri url, Func<ClientWebSocket> clientFactory = null)
        {
            Validations.Validations.ValidateInput(url, nameof(url));

            _url = url;
            _clientFactory = clientFactory ?? (() => new ClientWebSocket
            {
                Options = {KeepAliveInterval = new TimeSpan(0, 0, 5, 0)}
            }); 
        }

        /// <inheritdoc />
        public Uri Url
        {
            get => _url;
            set
            {
                Validations.Validations.ValidateInput(value, nameof(Url));
                _url = value;
            }
        }

        /// <summary>
        /// Stream with received message (raw format)
        /// </summary>
        public IObservable<ResponseMessage> MessageReceived => _messageReceivedSubject.AsObservable();

        /// <summary>
        /// Stream for reconnection event (triggered after the new connection) 
        /// </summary>
        public IObservable<ReconnectionType> ReconnectionHappened => _reconnectionSubject.AsObservable();

        /// <summary>
        /// Stream for disconnection event (triggered after the connection was lost) 
        /// </summary>
        public IObservable<DisconnectionType> DisconnectionHappened => _disconnectedSubject.AsObservable();

        /// <summary>
        /// Time range in ms, how long to wait before reconnecting if no message comes from server.
        /// Default 60000 ms (1 minute)
        /// </summary>
        public int ReconnectTimeoutMs { get; set; } = 60 * 1000;

        /// <summary>
        /// Time range in ms, how long to wait before reconnecting if last reconnection failed.
        /// Default 60000 ms (1 minute)
        /// </summary>
        public int ErrorReconnectTimeoutMs { get; set; } = 60 * 1000;

        /// <summary>
        /// Enable or disable reconnection functionality (enabled by default)
        /// </summary>
        public bool IsReconnectionEnabled
        {
            get => _isReconnectionEnabled;
            set
            {
                _isReconnectionEnabled = value;

                if (IsStarted)
                {
                    if (_isReconnectionEnabled)
                    {
                        ActivateLastChance();
                    }
                    else
                    {
                        DeactivateLastChance();
                    }
                }
            }
        }

        /// <summary>
        /// Get or set the name of the current websocket client instance.
        /// For logging purpose (in case you use more parallel websocket clients and want to distinguish between them)
        /// </summary>
        public string Name { get; set;}

        /// <summary>
        /// Returns true if Start() method was called at least once. False if not started or disposed
        /// </summary>
        public bool IsStarted { get; private set; }

        /// <summary>
        /// Returns true if client is running and connected to the server
        /// </summary>
        public bool IsRunning { get; private set; }

        /// <inheritdoc />
        public Encoding MessageEncoding { get; set; }

        /// <inheritdoc />
        public ClientWebSocket NativeClient => _client;

        /// <summary>
        /// Terminate the websocket connection and cleanup everything
        /// </summary>
        public void Dispose()
        {
            _disposing = true;
            Logger.Debug(L("Disposing.."));
            try
            {
                _lastChanceTimer?.Dispose();
                _cancellation?.Cancel();
                _cancellationTotal?.Cancel();
                _client?.Abort();
                _client?.Dispose();
                _cancellation?.Dispose();
                _cancellationTotal?.Dispose();
                _messagesTextToSendQueue?.Dispose();
            }
            catch (Exception e)
            {
                Logger.Error(e, L($"Failed to dispose client, error: {e.Message}"));
            }

            IsRunning = false;
            IsStarted = false;
            _disconnectedSubject.OnNext(DisconnectionType.Exit);
        }
       
        /// <summary>
        /// Start listening to the websocket stream on the background thread
        /// </summary>
        public async Task Start()
        {
            if (IsStarted)
            {
                Logger.Debug(L("Client already started, ignoring.."));
                return;
            }
            IsStarted = true;

            Logger.Debug(L("Starting.."));
            _cancellation = new CancellationTokenSource();
            _cancellationTotal = new CancellationTokenSource();

            await StartClient(_url, _cancellation.Token, ReconnectionType.Initial).ConfigureAwait(false);

            StartBackgroundThreadForSendingText();
            StartBackgroundThreadForSendingBinary();
        }

        /// <inheritdoc />
        public async Task<bool> Stop(WebSocketCloseStatus status, string statusDescription)
        {
            if (_client == null)
            {
                IsStarted = false;
                IsRunning = false;
                return false;
            }
                
            await _client.CloseAsync(status, statusDescription, _cancellation?.Token ?? CancellationToken.None);
            DeactivateLastChance();
            IsStarted = false;
            IsRunning = false;
            return true;
        }

        /// <summary>
        /// Send text message to the websocket channel. 
        /// It inserts the message to the queue and actual sending is done on an other thread
        /// </summary>
        /// <param name="message">Text message to be sent</param>
        public Task Send(string message)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            _messagesTextToSendQueue.Add(message);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Send binary message to the websocket channel. 
        /// It inserts the message to the queue and actual sending is done on an other thread
        /// </summary>
        /// <param name="message">Binary message to be sent</param>
        public Task Send(byte[] message)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            _messagesBinaryToSendQueue.Add(message);
            return Task.CompletedTask;
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

            return SendInternal(message);
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
            return SendInternal(message);
        }

        /// <summary>
        /// Force reconnection. 
        /// Closes current websocket stream and perform a new connection to the server.
        /// </summary>
        public async Task Reconnect()
        {
            if (!IsStarted)
            {
                Logger.Debug(L("Client not started, ignoring reconnection.."));
                return;
            }

            try
            {
                _reconnecting = true;
                await Reconnect(ReconnectionType.ByUser).ConfigureAwait(false);

            }
            finally
            {
                _reconnecting = false;
            }
        }

        private async Task SendTextFromQueue()
        {
            try
            {
                foreach (var message in _messagesTextToSendQueue.GetConsumingEnumerable(_cancellationTotal.Token))
                {
                    try
                    {
                        if (_reconnecting)
                        {
                            _messagesTextToSendQueue.Add(message);
                            await Task.Delay(500);
                            continue;
                        }
                        await SendInternal(message).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        Logger.Error(L($"Failed to send text message: '{message}'. Error: {e.Message}"));
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

                Logger.Trace(L($"Sending text thread failed, error: {e.Message}. Creating a new sending thread."));
                StartBackgroundThreadForSendingText();
            }

        }

        private async Task SendBinaryFromQueue()
        {
            try
            {
                foreach (var message in _messagesBinaryToSendQueue.GetConsumingEnumerable(_cancellationTotal.Token))
                {
                    try
                    {
                        if (_reconnecting)
                        {
                            _messagesBinaryToSendQueue.Add(message);
                            await Task.Delay(500);
                            continue;
                        }
                        await SendInternal(message).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        Logger.Error(L($"Failed to send binary message: '{message}'. Error: {e.Message}"));
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

                Logger.Trace(L($"Sending binary thread failed, error: {e.Message}. Creating a new sending thread."));
                StartBackgroundThreadForSendingBinary();
            }

        }

        private void StartBackgroundThreadForSendingText()
        {
#pragma warning disable 4014
            Task.Factory.StartNew(_ => SendTextFromQueue(), TaskCreationOptions.LongRunning, _cancellationTotal.Token);
#pragma warning restore 4014
        }

        private void StartBackgroundThreadForSendingBinary()
        {
#pragma warning disable 4014
            Task.Factory.StartNew(_ => SendBinaryFromQueue(), TaskCreationOptions.LongRunning, _cancellationTotal.Token);
#pragma warning restore 4014
        }

        private async Task SendInternal(string message)
        {
            Logger.Trace(L($"Sending:  {message}"));
            var buffer = GetEncoding().GetBytes(message);
            var messageSegment = new ArraySegment<byte>(buffer);
            var client = await GetClient().ConfigureAwait(false);
            await client
                .SendAsync(messageSegment, WebSocketMessageType.Text, true, _cancellation.Token)
                .ConfigureAwait(false);
        }

        private async Task SendInternal(byte[] message)
        {
            var client = await GetClient().ConfigureAwait(false);
            await client
                .SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Binary, true, _cancellation.Token)
                .ConfigureAwait(false);
        }

        private async Task StartClient(Uri uri, CancellationToken token, ReconnectionType type)
        {
            DeactivateLastChance();
            _client = _clientFactory();
            
            try
            {
                await _client.ConnectAsync(uri, token).ConfigureAwait(false);
                IsRunning = true;
                _reconnectionSubject.OnNext(type);
#pragma warning disable 4014
                Listen(_client, token);
#pragma warning restore 4014
                _lastReceivedMsg = DateTime.UtcNow;
                ActivateLastChance();
            }
            catch (Exception e)
            {
                _disconnectedSubject.OnNext(DisconnectionType.Error);
                Logger.Error(e, L($"Exception while connecting. " +
                                  $"Waiting {ErrorReconnectTimeoutMs/1000} sec before next reconnection try."));
                await Task.Delay(ErrorReconnectTimeoutMs, token).ConfigureAwait(false);
                await Reconnect(ReconnectionType.Error).ConfigureAwait(false);
            }       
        }

        private async Task<ClientWebSocket> GetClient()
        {
            if (_client == null || (_client.State != WebSocketState.Open && _client.State != WebSocketState.Connecting))
            {
                await Reconnect(ReconnectionType.Lost).ConfigureAwait(false);
            }
            return _client;
        }

        private async Task Reconnect(ReconnectionType type)
        {
            IsRunning = false;
            if (_disposing)
                return;

            _reconnecting = true;
            if(type != ReconnectionType.Error)
                _disconnectedSubject.OnNext(TranslateTypeToDisconnection(type));

            _client?.Abort();
            _client?.Dispose();
            _cancellation.Cancel();
            await Task.Delay(1000).ConfigureAwait(false);

            if (!IsReconnectionEnabled)
            {
                // reconnection disabled, do nothing
                IsStarted = false;
                _reconnecting = false;
                return;
            }

            Logger.Debug(L("Reconnecting..."));
            _cancellation = new CancellationTokenSource();
            await StartClient(_url, _cancellation.Token, type).ConfigureAwait(false);
            _reconnecting = false;
        }

        private async Task Listen(ClientWebSocket client, CancellationToken token)
        {
            try
            {
                do
                {
                    var buffer = new ArraySegment<byte>(new byte[8192]);

                    using (var ms = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await _client.ReceiveAsync(buffer, token);
                            if(buffer.Array != null)
                                ms.Write(buffer.Array, buffer.Offset, result.Count);
                        } while (!result.EndOfMessage);

                        ms.Seek(0, SeekOrigin.Begin);

                        ResponseMessage message;
                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            var data = GetEncoding().GetString(ms.ToArray());
                            message = ResponseMessage.TextMessage(data);
                        }
                        else
                        {
                            var data = ms.ToArray();
                            message = ResponseMessage.BinaryMessage(data);
                        }

                        Logger.Trace(L($"Received:  {message}"));
                        _lastReceivedMsg = DateTime.UtcNow;
                        _messageReceivedSubject.OnNext(message);
                    }

                } while (client.State == WebSocketState.Open && !token.IsCancellationRequested);
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
                Logger.Error(e, L($"Error while listening to websocket stream, error: '{e.Message}'"));

                if (_disposing || _reconnecting)
                    return;

                // listening thread is lost, we have to reconnect
#pragma warning disable 4014
                Reconnect(ReconnectionType.Lost);
#pragma warning restore 4014
            }
        }

        private void ActivateLastChance()
        {
            var timerMs = 1000 * 5;
            _lastChanceTimer = new Timer(LastChance, null, timerMs, timerMs);
        }

        private void DeactivateLastChance()
        {
            _lastChanceTimer?.Dispose();
            _lastChanceTimer = null;
        }

        private void LastChance(object state)
        {
            var timeoutMs = Math.Abs(ReconnectTimeoutMs);
            var diffMs = Math.Abs(DateTime.UtcNow.Subtract(_lastReceivedMsg).TotalMilliseconds);
            if (diffMs > timeoutMs)
            {
                if (!IsReconnectionEnabled)
                {
                    // reconnection disabled, do nothing
                    DeactivateLastChance();
                    return;
                }

                Logger.Debug(L($"Last message received more than {timeoutMs:F} ms ago. Hard restart.."));

                DeactivateLastChance();
#pragma warning disable 4014
                Reconnect(ReconnectionType.NoMessageReceived);
#pragma warning restore 4014
            }
        }

        private Encoding GetEncoding()
        {
            if (MessageEncoding == null)
                MessageEncoding = Encoding.UTF8;
            return MessageEncoding;
        }

        private string L(string msg)
        {
            var name = Name ?? "CLIENT";
            return $"[WEBSOCKET {name}] {msg}";
        }

        private static ILog GetLogger()
        {
            try
            {
                return LogProvider.GetCurrentClassLogger();
            }
            catch (Exception e)
            {
                Trace.WriteLine($"[WEBSOCKET] Failed to initialize logger, disabling.. " +
                                $"Error: {e}");
                return LogProvider.NoOpLogger.Instance;
            }
        }

        private DisconnectionType TranslateTypeToDisconnection(ReconnectionType type)
        {
            // beware enum indexes must correspond to each other
            return (DisconnectionType) type;
        }
    }
}
