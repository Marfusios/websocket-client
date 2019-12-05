using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Exceptions;
using Websocket.Client.Logging;
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
        private Uri _url;
        private Timer _lastChanceTimer;
        private readonly Func<Uri, CancellationToken, Task<WebSocket>> _connectionFactory;

        private DateTime _lastReceivedMsg = DateTime.UtcNow; 

        private bool _disposing;
        private bool _reconnecting;
        private bool _stopping;
        private bool _isReconnectionEnabled = true;
        private WebSocket _client;
        private CancellationTokenSource _cancellation;
        private CancellationTokenSource _cancellationTotal;

        private readonly Subject<ResponseMessage> _messageReceivedSubject = new Subject<ResponseMessage>();
        private readonly Subject<ReconnectionType> _reconnectionSubject = new Subject<ReconnectionType>();
        private readonly Subject<DisconnectionType> _disconnectedSubject = new Subject<DisconnectionType>();

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
                var client = new ClientWebSocket
                {
                    Options = { KeepAliveInterval = new TimeSpan(0, 0, 5, 0) }
                };
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
        public IObservable<ReconnectionType> ReconnectionHappened => _reconnectionSubject.AsObservable();

        /// <summary>
        /// Stream for disconnection event (triggered after the connection was lost) 
        /// </summary>
        public IObservable<DisconnectionType> DisconnectionHappened => _disconnectedSubject.AsObservable();

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

        /// <inheritdoc />
        public Encoding MessageEncoding { get; set; }

        /// <inheritdoc />
        public ClientWebSocket NativeClient => GetSpecificOrThrow(_client);

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
                _messagesBinaryToSendQueue?.Dispose();
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

        /// <inheritdoc />
        public async Task<bool> Stop(WebSocketCloseStatus status, string statusDescription)
        {
            if (_client == null)
            {
                IsStarted = false;
                IsRunning = false;
                return false;
            }
            
            DeactivateLastChance();

            try
            {
                _stopping = true;
                await _client.CloseAsync(status, statusDescription, _cancellation?.Token ?? CancellationToken.None);
            }
            catch (Exception e)
            {
                Logger.Error(L($"Error while stopping client, message: '{e.Message}'"));
            }

            IsStarted = false;
            IsRunning = false;
            _stopping = false;
            return true;
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

        private async Task StartClient(Uri uri, CancellationToken token, ReconnectionType type, bool failFast)
        {
            DeactivateLastChance();
            
            try
            {
                _client = await _connectionFactory(uri, token).ConfigureAwait(false);
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

                if (failFast)
                {
                    // fail fast, propagate exception
                    // do not reconnect
                    throw new WebsocketException($"Failed to start Websocket client, error: '{e.Message}'", e);
                }

                if (ErrorReconnectTimeout == null)
                {
                    Logger.Error(e, L($"Exception while connecting. " +
                                      $"Reconnecting disable, exiting. "));
                    return;
                }

                var timeout = ErrorReconnectTimeout.Value;
                Logger.Error(e, L($"Exception while connecting. " +
                                  $"Waiting {timeout.Seconds} sec before next reconnection try."));
                await Task.Delay(timeout, token).ConfigureAwait(false);
                await Reconnect(ReconnectionType.Error, false).ConfigureAwait(false);
            }       
        }

        private bool IsClientConnected()
        {
            return _client.State == WebSocketState.Open;
        }

        private async Task Listen(WebSocket client, CancellationToken token)
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
                            result = await client.ReceiveAsync(buffer, token);
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
            catch (ObjectDisposedException)
            {
                // client was disposed, ignore
            }
            catch (Exception e)
            {
                Logger.Error(e, L($"Error while listening to websocket stream, error: '{e.Message}'"));
            }


            if (_disposing || _reconnecting || _stopping || !IsStarted)
            {
                // reconnection already in progress or client stopped/disposed, do nothing
                return;
            }
                
            if (client != _client)
            {
                // already reconnected, do nothing
                return;
            }

            // listening thread is lost, we have to reconnect
#pragma warning disable 4014
            ReconnectSynchronized(ReconnectionType.Lost, false);
#pragma warning restore 4014
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
