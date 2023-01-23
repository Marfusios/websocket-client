using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Exceptions;
using Websocket.Client.Logging;
using Websocket.Client.Models;
using Websocket.Client.Threading;

namespace Websocket.Client
{
    /// <summary>
    /// A simple websocket client with built-in reconnection and error handling
    /// </summary>
    public partial class WebsocketClient : IWebsocketClient
    {
        private static readonly ILog Logger = GetLogger();

        private readonly WebsocketAsyncLock _locker = new WebsocketAsyncLock();
        private readonly Func<Uri, CancellationToken, Task<WebSocket>> _connectionFactory;

        private Uri _url;
        private Timer _lastChanceTimer;
        private DateTime _lastReceivedMsg = DateTime.UtcNow; 

        private bool _disposing;
        private bool _reconnecting;
        private bool _stopping;
        private bool _isReconnectionEnabled = true;
        private WebSocket _client;
        private CancellationTokenSource _cancellation;
        private CancellationTokenSource _cancellationTotal;

        private readonly Subject<ResponseMessage> _messageReceivedSubject = new Subject<ResponseMessage>();
        private readonly Subject<ReconnectionInfo> _reconnectionSubject = new Subject<ReconnectionInfo>();
        private readonly Subject<DisconnectionInfo> _disconnectedSubject = new Subject<DisconnectionInfo>();


        /// <summary>
        /// 
        /// </summary>
        /// <param name="clientFactory"></param>

        public WebsocketClient(Func<ClientWebSocket> clientFactory = null)
            : this(GetClientFactory(clientFactory))
        {
        }

        /// <summary>
        /// 
        /// </summary>
        public WebsocketClient(Func<Uri, CancellationToken, Task<WebSocket>> connectionFactory = null)
        {                        
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

        /// <summary>
        /// A simple websocket client with built-in reconnection and error handling
        /// </summary>
        /// <param name="url">Target websocket url (wss://)</param>
        /// <param name="clientFactory">Optional factory for native ClientWebSocket, use it whenever you need some custom features (proxy, settings, etc)</param>
        public WebsocketClient(Uri url, Func<ClientWebSocket> clientFactory = null)
            : this(url, GetClientFactory(clientFactory))
        {
        }

        /// <summary>
        /// A simple websocket client with built-in reconnection and error handling
        /// </summary>
        /// <param name="url">Target websocket url (wss://)</param>
        /// <param name="connectionFactory">Optional factory for native creating and connecting to a websocket. The method should return a <see cref="WebSocket"/> which is connected. Use it whenever you need some custom features (proxy, settings, etc)</param>
        public WebsocketClient(Uri url, Func<Uri, CancellationToken, Task<WebSocket>> connectionFactory)
        {
            Validations.Validations.ValidateInput(url, nameof(url));

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
        /// Time range in ms, how long to wait before reconnecting if no message comes from server.
        /// Set null to disable this feature. 
        /// Default: 1 minute
        /// </summary>
        public TimeSpan? ReconnectTimeout { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Time range in ms, how long to wait before reconnecting if last reconnection failed.
        /// Set null to disable this feature. 
        /// Default: 1 minute
        /// </summary>
        public TimeSpan? ErrorReconnectTimeout { get; set; } = TimeSpan.FromMinutes(1);

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

        /// <summary>
        /// Enable or disable text message conversion from binary to string (via 'MessageEncoding' property).
        /// Default: true
        /// </summary>
        public bool IsTextMessageConversionEnabled { get; set; } = true;

        /// <inheritdoc />
        public Encoding MessageEncoding { get; set; }

        /// <inheritdoc />
        public ClientWebSocket NativeClient => GetSpecificOrThrow(_client);

        /// <inheritdoc />
        public Predicate<string> IsSubprotocolMessage { get; set; } = null;

        /// <summary>
        /// Terminate the websocket connection and cleanup everything
        /// </summary>
        public void Dispose()
        {
            _disposing = true;
            Logger.Debug(L("Disposing.."));
            try
            {
                _messagesTextToSendQueue?.Writer.Complete();
                _messagesBinaryToSendQueue?.Writer.Complete();
                _lastChanceTimer?.Dispose();
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
                Logger.Error(e, L($"Failed to dispose client, error: {e.Message}"));
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

        private static Func<Uri, CancellationToken, Task<WebSocket>> GetClientFactory(Func<ClientWebSocket> clientFactory)
        {
            if (clientFactory == null)
                return null;

            return (async (uri, token) => {
                var client = clientFactory();
                await client.ConnectAsync(uri, token).ConfigureAwait(false);
                return client;
            });
        }

        private async Task StartInternal(bool failFast)
        {
            if(_disposing)
            {
                throw new WebsocketException(L("Client is already disposed, starting not possible"));
            }

            if (IsStarted)
            {
                Logger.Debug(L("Client already started, ignoring.."));
                return;
            }

            IsStarted = true;

            Logger.Debug(L("Starting.."));
            _cancellation = new CancellationTokenSource();
            _cancellationTotal = new CancellationTokenSource();

            await StartClient(_url, _cancellation.Token, ReconnectionType.Initial, failFast).ConfigureAwait(false);

            StartBackgroundThreadForSendingText();
            StartBackgroundThreadForSendingBinary();
        }

        private async Task<bool> StopInternal(WebSocket client, WebSocketCloseStatus status, string statusDescription, 
            CancellationToken? cancellation, bool failFast, bool byServer)
        {
            if (_disposing)
            {
                throw new WebsocketException(L("Client is already disposed, stopping not possible"));
            }

            if(!IsRunning)
            {
                Logger.Info(L("Client is already stopped"));

                return false;
            }

            var result = false;
            if (client == null)
            {
                IsStarted = false;
                IsRunning = false;
                return false;
            }

            DeactivateLastChance();

            try
            {
                var cancellationToken = cancellation ?? CancellationToken.None;
                _stopping = true;
                if(byServer)
                    await client.CloseOutputAsync(status, statusDescription, cancellationToken);
                else
                    await client.CloseAsync(status, statusDescription, cancellationToken);
                result = true;
            }
            catch (Exception e)
            {
                Logger.Error(e, L($"Error while stopping client, message: '{e.Message}'"));

                if (failFast)
                {
                    // fail fast, propagate exception
                    throw new WebsocketException($"Failed to stop Websocket client, error: '{e.Message}'", e);
                }
            }
            finally
            {
                IsStarted = false;
                IsRunning = false;
                _stopping = false;
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
                    Logger.Error(e, L($"Exception while connecting. " +
                                      $"Reconnecting canceled by user, exiting. Error: '{e.Message}'"));
                    return;
                }

                if (failFast)
                {
                    // fail fast, propagate exception
                    // do not reconnect
                    throw new WebsocketException($"Failed to start Websocket client, error: '{e.Message}'", e);
                }

                if (ErrorReconnectTimeout == null)
                {
                    Logger.Error(e, L($"Exception while connecting. " +
                                      $"Reconnecting disabled, exiting. Error: '{e.Message}'"));
                    return;
                }

                var timeout = ErrorReconnectTimeout.Value;
                Logger.Error(e, L($"Exception while connecting. " +
                                  $"Waiting {timeout.TotalSeconds} sec before next reconnection try. Error: '{e.Message}'"));
                await Task.Delay(timeout, token).ConfigureAwait(false);
                await Reconnect(ReconnectionType.Error, false, e).ConfigureAwait(false);
            }       
        }

        private bool IsClientConnected()
        {
            return _client.State == WebSocketState.Open;
        }

        private async Task Listen(WebSocket client, CancellationToken token)
        { 
            Exception causedException = null;
            try
            {
                // define buffer here and reuse, to avoid more allocation
                const int chunkSize = 1024 * 4;
                var buffer = new ArraySegment<byte>(new byte[chunkSize]);

                do
                {
                    WebSocketReceiveResult result;
                    byte[] resultArrayWithTrailing = null;
                    var resultArraySize = 0;
                    var isResultArrayCloned = false;
                    MemoryStream ms = null;

                    while (true)
                    {
                        result = await client.ReceiveAsync(buffer, token);
                        var currentChunk = buffer.Array;
                        var currentChunkSize = result.Count;

                        var isFirstChunk = resultArrayWithTrailing == null;
                        if (isFirstChunk)
                        {
                            // first chunk, use buffer as reference, do not allocate anything
                            resultArraySize += currentChunkSize;
                            resultArrayWithTrailing = currentChunk;
                            isResultArrayCloned = false;
                        }
                        else if(currentChunk == null)
                        {
                            // weird chunk, do nothing
                        }
                        else
                        {
                            // received more chunks, lets merge them via memory stream
                            if (ms == null)
                            {
                                // create memory stream and insert first chunk
                                ms = new MemoryStream();
                                ms.Write(resultArrayWithTrailing, 0, resultArraySize);
                            }

                            // insert current chunk
                            ms.Write(currentChunk, buffer.Offset, currentChunkSize);
                        }

                        if (result.EndOfMessage)
                        {
                            break;
                        }

                        if(isResultArrayCloned)
                            continue;

                        // we got more chunks incoming, need to clone first chunk
                        resultArrayWithTrailing = resultArrayWithTrailing?.ToArray();
                        isResultArrayCloned = true;
                    }

                    ms?.Seek(0, SeekOrigin.Begin);

                    ResponseMessage message;
                    if (result.MessageType == WebSocketMessageType.Text && IsTextMessageConversionEnabled)
                    {
                        var data = ms != null ?
                            GetEncoding().GetString(ms.ToArray()) :
                            resultArrayWithTrailing != null ?
                                GetEncoding().GetString(resultArrayWithTrailing, 0, resultArraySize) :
                                null;

                        message = ResponseMessage.TextMessage(data);
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Logger.Trace(L($"Received close message"));

                        if(!IsStarted || _stopping)
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
                                throw new OperationCanceledException("Websocket connection was closed by server");
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
                        if (ms != null)
                        {
                            message = ResponseMessage.BinaryMessage(ms.ToArray());
                        }
                        else
                        {
                            Array.Resize(ref resultArrayWithTrailing, resultArraySize);
                            message = ResponseMessage.BinaryMessage(resultArrayWithTrailing);
                        }
                    }

                    ms?.Dispose();

                    Logger.Trace(L($"Received:  {message}"));
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
                Logger.Error(e, L($"Error while listening to websocket stream, error: '{e.Message}'"));
                causedException = e;
            }


            if (ShouldIgnoreReconnection(client) || !IsStarted)
            {
                // reconnection already in progress or client stopped/disposed, do nothing
                return;
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

        private ClientWebSocket GetSpecificOrThrow(WebSocket client)
        {
            if (client == null)
                return null;
            var specific = client as ClientWebSocket;
            if(specific == null)
                throw new WebsocketException("Cannot cast 'WebSocket' client to 'ClientWebSocket', " +
                                             "provide correct type via factory or don't use this property at all.");
            return specific;
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
