using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IO;
using System;
using System.IO;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Exceptions;
using Websocket.Client.Threading;

namespace Websocket.Client
{
    /// <summary>
    /// A simple websocket client with built-in reconnection and error handling
    /// </summary>
    public partial class WebsocketClient : IWebsocketClient
    {
        private readonly ILogger<WebsocketClient> _logger;
        private readonly WebsocketAsyncLock _locker = new WebsocketAsyncLock();
        private readonly Func<Uri, CancellationToken, Task<WebSocket>> _connectionFactory;
        private static readonly RecyclableMemoryStreamManager _memoryStreamManager = new RecyclableMemoryStreamManager();

        private Uri _url;
        private Timer? _lastChanceTimer;
        private DateTime _lastReceivedMsg = DateTime.UtcNow;

        private Timer? _errorReconnectTimer;

        private bool _disposing;
        private bool _reconnecting;
        private bool _stopping;
        private bool _isReconnectionEnabled = true;
        private WebSocket? _client;
        private CancellationTokenSource? _cancellation;
        private CancellationTokenSource? _cancellationTotal;

        private readonly Subject<ResponseMessage> _messageReceivedSubject = new Subject<ResponseMessage>();
        private readonly Subject<ReconnectionInfo> _reconnectionSubject = new Subject<ReconnectionInfo>();
        private readonly Subject<DisconnectionInfo> _disconnectedSubject = new Subject<DisconnectionInfo>();

        /// <summary>
        /// A simple websocket client with built-in reconnection and error handling
        /// </summary>
        /// <param name="url">Target websocket url (wss://)</param>
        /// <param name="clientFactory">Optional factory for native ClientWebSocket, use it whenever you need some custom features (proxy, settings, etc)</param>
        public WebsocketClient(Uri url, Func<ClientWebSocket>? clientFactory = null)
            : this(url, null, GetClientFactory(clientFactory))
        {
        }

        /// <summary>
        /// A simple websocket client with built-in reconnection and error handling
        /// </summary>
        /// <param name="url">Target websocket url (wss://)</param>
        /// <param name="logger">Logger instance, can be null</param>
        /// <param name="clientFactory">Optional factory for native ClientWebSocket, use it whenever you need some custom features (proxy, settings, etc)</param>
        public WebsocketClient(Uri url, ILogger<WebsocketClient>? logger, Func<ClientWebSocket>? clientFactory = null)
            : this(url, logger, GetClientFactory(clientFactory))
        {
        }

        /// <summary>
        /// A simple websocket client with built-in reconnection and error handling
        /// </summary>
        /// <param name="url">Target websocket url (wss://)</param>
        /// <param name="logger">Logger instance, can be null</param>
        /// <param name="connectionFactory">Optional factory for native creating and connecting to a websocket. The method should return a <see cref="WebSocket"/> which is connected. Use it whenever you need some custom features (proxy, settings, etc)</param>
        public WebsocketClient(Uri url, ILogger<WebsocketClient>? logger, Func<Uri, CancellationToken, Task<WebSocket>>? connectionFactory)
        {
            Validations.Validations.ValidateInput(url, nameof(url));

            _logger = logger ?? NullLogger<WebsocketClient>.Instance;
            _url = url;
            _connectionFactory = connectionFactory ?? (async (uri, token) =>
            {
                //var client = new ClientWebSocket
                //{
                //    Options = { KeepAliveInterval = new TimeSpan(0, 0, 5, 0) }
                //};
                var client = new ClientWebSocket();
                await client.ConnectAsync(uri, token).ConfigureAwait(false);
                return client;
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
        public IObservable<ReconnectionInfo> ReconnectionHappened => _reconnectionSubject.AsObservable();

        /// <summary>
        /// Stream for disconnection event (triggered after the connection was lost) 
        /// </summary>
        public IObservable<DisconnectionInfo> DisconnectionHappened => _disconnectedSubject.AsObservable();

        /// <summary>
        /// Time range for how long to wait before reconnecting if no message comes from server.
        /// Set null to disable this feature. 
        /// Default: 1 minute
        /// </summary>
        public TimeSpan? ReconnectTimeout { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Time range for how long to wait before reconnecting if last reconnection failed.
        /// Set null to disable this feature. 
        /// Default: 1 minute
        /// </summary>
        public TimeSpan? ErrorReconnectTimeout { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Time range for how long to wait before reconnecting if connection is lost with a transient error.
        /// Set null to disable this feature. 
        /// Default: null/disabled (immediately)
        /// </summary>
        public TimeSpan? LostReconnectTimeout { get; set; }

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
        public string? Name { get; set; }

        /// <summary>
        /// Returns true if Start() method was called at least once. False if not started or disposed
        /// </summary>
        public bool IsStarted { get; private set; }

        /// <summary>
        /// Returns true if client is running and connected to the server
        /// </summary>
        public bool IsRunning { get; private set; }

        /// <summary>
        /// Enable or disable text message conversion from binary to string (via 'MessageEncoding' property).
        /// Default: true
        /// </summary>
        public bool IsTextMessageConversionEnabled { get; set; } = true;

        /// <summary>
        /// Enable or disable automatic <see cref="MemoryStream.Dispose(bool)"/> of the <see cref="MemoryStream"/> 
        /// after sending data (only available for binary response).
        /// Setting value to false allows you to access the stream directly.
        /// <warning>However, keep in mind that you need to handle the dispose yourself.</warning>
        /// Default: true
        /// </summary>
        public bool IsStreamDisposedAutomatically { get; set; } = true;

        /// <inheritdoc />
        public Encoding? MessageEncoding { get; set; }

        /// <inheritdoc />
        public ClientWebSocket? NativeClient => GetSpecificOrThrow(_client);

        /// <summary>
        /// Terminate the websocket connection and cleanup everything
        /// </summary>
        public void Dispose()
        {
            _disposing = true;
            _logger.LogDebug(L("Disposing.."), Name);
            try
            {
                _messagesTextToSendQueue.Writer.Complete();
                _messagesBinaryToSendQueue.Writer.Complete();
                _lastChanceTimer?.Dispose();
                _errorReconnectTimer?.Dispose();
                _cancellation?.Cancel();
                _cancellationTotal?.Cancel();
                _client?.Abort();
                _client?.Dispose();
                _cancellation?.Dispose();
                _cancellationTotal?.Dispose();
                _messageReceivedSubject.OnCompleted();
                _reconnectionSubject.OnCompleted();
            }
            catch (Exception e)
            {
                _logger.LogError(e, L("Failed to dispose client, error: {error}"), Name, e.Message);
            }

            if (IsRunning)
            {
                _disconnectedSubject.OnNext(DisconnectionInfo.Create(DisconnectionType.Exit, _client, null));
            }

            IsRunning = false;
            IsStarted = false;
            _disconnectedSubject.OnCompleted();
        }

        /// <summary>
        /// Start listening to the websocket stream on the background thread.
        /// In case of connection error it doesn't throw an exception.
        /// Only streams a message via 'DisconnectionHappened' and logs it. 
        /// </summary>
        public Task Start()
        {
            return StartInternal(false);
        }

        /// <summary>
        /// Start listening to the websocket stream on the background thread. 
        /// In case of connection error it throws an exception.
        /// Fail fast approach. 
        /// </summary>
        public Task StartOrFail()
        {
            return StartInternal(true);
        }

        /// <summary>
        /// Stop/close websocket connection with custom close code.
        /// Method doesn't throw exception, only logs it and mark client as closed. 
        /// </summary>
        /// <returns>Returns true if close was initiated successfully</returns>
        public async Task<bool> Stop(WebSocketCloseStatus status, string statusDescription)
        {
            var result = await StopInternal(
                _client,
                status,
                statusDescription,
                null,
                false,
                false).ConfigureAwait(false);
            _disconnectedSubject.OnNext(DisconnectionInfo.Create(DisconnectionType.ByUser, _client, null));
            return result;
        }

        /// <summary>
        /// Stop/close websocket connection with custom close code.
        /// Method could throw exceptions, but client is marked as closed anyway.
        /// </summary>
        /// <returns>Returns true if close was initiated successfully</returns>
        public async Task<bool> StopOrFail(WebSocketCloseStatus status, string statusDescription)
        {
            var result = await StopInternal(
                _client,
                status,
                statusDescription,
                null,
                true,
                false).ConfigureAwait(false);
            _disconnectedSubject.OnNext(DisconnectionInfo.Create(DisconnectionType.ByUser, _client, null));
            return result;
        }

        private static Func<Uri, CancellationToken, Task<WebSocket>>? GetClientFactory(Func<ClientWebSocket>? clientFactory)
        {
            if (clientFactory == null)
                return null;

            return (async (uri, token) =>
            {
                var client = clientFactory();
                await client.ConnectAsync(uri, token).ConfigureAwait(false);
                return client;
            });
        }

        private async Task StartInternal(bool failFast)
        {
            if (_disposing)
            {
                throw new WebsocketException($"Client {Name} is already disposed, starting not possible");
            }

            if (IsStarted)
            {
                _logger.LogDebug(L("Client already started, ignoring.."), Name);
                return;
            }

            IsStarted = true;

            _logger.LogDebug(L("Starting.."), Name);
            _cancellation = new CancellationTokenSource();
            _cancellationTotal = new CancellationTokenSource();

            await StartClient(_url, _cancellation.Token, ReconnectionType.Initial, failFast).ConfigureAwait(false);

            StartBackgroundThreadForSendingText();
            StartBackgroundThreadForSendingBinary();
        }

        private async Task<bool> StopInternal(WebSocket? client, WebSocketCloseStatus status, string statusDescription,
            CancellationToken? cancellation, bool failFast, bool byServer)
        {
            if (_disposing)
            {
                throw new WebsocketException($"Client {Name} is already disposed, stopping not possible");
            }

            DeactivateLastChance();

            if (client == null)
            {
                IsStarted = false;
                IsRunning = false;
                return false;
            }

            if (!IsRunning)
            {
                _logger.LogInformation(L("Client is already stopped"), Name);
                IsStarted = false;
                return false;
            }

            var result = false;
            try
            {
                var cancellationToken = cancellation ?? CancellationToken.None;
                _stopping = true;
                if (byServer)
                    await client.CloseOutputAsync(status, statusDescription, cancellationToken);
                else
                    await client.CloseAsync(status, statusDescription, cancellationToken);
                result = true;
            }
            catch (Exception e)
            {
                _logger.LogError(e, L("Error while stopping client, message: '{error}'"), Name, e.Message);

                if (failFast)
                {
                    // fail fast, propagate exception
                    throw new WebsocketException($"Failed to stop Websocket client {Name}, error: '{e.Message}'", e);
                }
            }
            finally
            {
                IsRunning = false;
                _stopping = false;

                if (!byServer || !IsReconnectionEnabled)
                {
                    // stopped manually or no reconnection, mark client as non-started
                    IsStarted = false;
                }
            }

            return result;
        }

        private async Task StartClient(Uri uri, CancellationToken token, ReconnectionType type, bool failFast)
        {
            DeactivateLastChance();

            try
            {
                _client = await _connectionFactory(uri, token).ConfigureAwait(false);
                _ = Listen(_client, token);
                IsRunning = true;
                IsStarted = true;
                _reconnectionSubject.OnNext(ReconnectionInfo.Create(type));
                _lastReceivedMsg = DateTime.UtcNow;
                ActivateLastChance();
            }
            catch (Exception e)
            {
                var info = DisconnectionInfo.Create(DisconnectionType.Error, _client, e);
                _disconnectedSubject.OnNext(info);

                if (info.CancelReconnection)
                {
                    // reconnection canceled by user, do nothing
                    _logger.LogError(e, L("Exception while connecting. " +
                                       "Reconnecting canceled by user, exiting. Error: '{error}'"), Name, e.Message);
                    return;
                }

                if (failFast)
                {
                    // fail fast, propagate exception
                    // do not reconnect
                    throw new WebsocketException($"Failed to start Websocket client {Name}, error: '{e.Message}'", e);
                }

                if (ErrorReconnectTimeout == null)
                {
                    _logger.LogError(e, L("Exception while connecting. " +
                                       "Reconnecting disabled, exiting. Error: '{error}'"), Name, e.Message);
                    return;
                }

                var timeout = ErrorReconnectTimeout.Value;
                _logger.LogError(e, L("Exception while connecting. " +
                                   "Waiting {timeout} sec before next reconnection try. Error: '{error}'"), Name, timeout.TotalSeconds, e.Message);
                _errorReconnectTimer?.Dispose();
                _errorReconnectTimer = new Timer(ReconnectOnError, e, timeout, Timeout.InfiniteTimeSpan);
            }
        }

        private void ReconnectOnError(object? state)
        {
            // await Task.Delay(timeout, token).ConfigureAwait(false);
            _ = Reconnect(ReconnectionType.Error, false, state as Exception).ConfigureAwait(false);
        }

        private bool IsClientConnected()
        {
            return _client?.State == WebSocketState.Open;
        }

        private async Task Listen(WebSocket client, CancellationToken token)
        {
            Exception? causedException = null;
            try
            {
                // define buffer here and reuse, to avoid more allocation
                const int chunkSize = 1024 * 4;
                var buffer = new Memory<byte>(new byte[chunkSize]);

                do
                {
                    ValueWebSocketReceiveResult result;
                    var ms = (RecyclableMemoryStream)_memoryStreamManager.GetStream();

                    while (true)
                    {
                        result = await client.ReceiveAsync(buffer, token);
                        ms.Write(buffer[..result.Count].Span);

                        if (result.EndOfMessage)
                            break;
                    }

                    ms.Seek(0, SeekOrigin.Begin);

                    ResponseMessage message;
                    bool shouldDisposeStream = true;

                    if (result.MessageType == WebSocketMessageType.Text && IsTextMessageConversionEnabled)
                    {
                        var data = GetEncoding().GetString(ms.ToArray());
                        message = ResponseMessage.TextMessage(data);
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogTrace(L("Received close message"), Name);

                        if (!IsStarted || _stopping)
                        {
                            return;
                        }

                        var info = DisconnectionInfo.Create(DisconnectionType.ByServer, client, null);
                        _disconnectedSubject.OnNext(info);

                        if (info.CancelClosing)
                        {
                            // closing canceled, reconnect if enabled
                            if (IsReconnectionEnabled)
                            {
                                throw new OperationCanceledException($"Websocket connection was closed by server (client: {Name})");
                            }

                            continue;
                        }

                        await StopInternal(client, WebSocketCloseStatus.NormalClosure, "Closing",
                            token, false, true);

                        // reconnect if enabled
                        if (IsReconnectionEnabled && !ShouldIgnoreReconnection(client))
                        {
                            _ = ReconnectSynchronized(ReconnectionType.Lost, false, null);
                        }

                        return;
                    }
                    else
                    {
                        if (IsStreamDisposedAutomatically)
                        {
                            message = ResponseMessage.BinaryMessage(ms.ToArray());
                        }
                        else
                        {
                            message = ResponseMessage.BinaryStreamMessage(ms);
                            shouldDisposeStream = false;
                        }
                    }

                    if (shouldDisposeStream)
                        ms.Dispose();

                    _logger.LogTrace(L("Received:  {message}"), Name, message);
                    _lastReceivedMsg = DateTime.UtcNow;
                    _messageReceivedSubject.OnNext(message);

                } while (client.State == WebSocketState.Open && !token.IsCancellationRequested);
            }
            catch (TaskCanceledException e)
            {
                // task was canceled, ignore
                causedException = e;
            }
            catch (OperationCanceledException e)
            {
                // operation was canceled, ignore
                causedException = e;
            }
            catch (ObjectDisposedException e)
            {
                // client was disposed, ignore
                causedException = e;
            }
            catch (Exception e)
            {
                _logger.LogError(e, L("Error while listening to websocket stream, error: '{error}'"), Name, e.Message);
                causedException = e;
            }

            if (ShouldIgnoreReconnection(client) || !IsStarted)
            {
                // reconnection already in progress or client stopped/disposed, do nothing
                return;
            }

            if (LostReconnectTimeout.HasValue)
            {
                var timeout = LostReconnectTimeout.Value;
                _logger.LogWarning(L("Listening websocket stream is lost. " +
                               "Waiting {timeout} sec before next reconnection try."), Name, timeout.TotalSeconds);
                await Task.Delay(timeout, token).ConfigureAwait(false);
            }

            // listening thread is lost, we have to reconnect
            _ = ReconnectSynchronized(ReconnectionType.Lost, false, causedException);
        }

        private bool ShouldIgnoreReconnection(WebSocket client)
        {
            // reconnection already in progress or client stopped/ disposed,
            var inProgress = _disposing || _reconnecting || _stopping;

            // already reconnected
            var differentClient = client != _client;

            return inProgress || differentClient;
        }

        private Encoding GetEncoding()
        {
            if (MessageEncoding == null)
                MessageEncoding = Encoding.UTF8;
            return MessageEncoding;
        }

        private ClientWebSocket? GetSpecificOrThrow(WebSocket? client)
        {
            if (client == null)
                return null;
            var specific = client as ClientWebSocket;
            if (specific == null)
                throw new WebsocketException("Cannot cast 'WebSocket' client to 'ClientWebSocket', " +
                                             "provide correct type via factory or don't use this property at all.");
            return specific;
        }

        private string L(string msg)
        {
            return $"[WEBSOCKET {{name}}] {msg}";
        }

        private DisconnectionType TranslateTypeToDisconnection(ReconnectionType type)
        {
            // beware enum indexes must correspond to each other
            return (DisconnectionType)type;
        }
    }
}
