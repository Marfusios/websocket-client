using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;

namespace Websocket.Client
{
    /// <summary>
    /// A simple websocket client with built-in reconnection and error handling
    /// </summary>
    public interface IWebsocketClient : IDisposable
    {
        /// <summary>
        /// Get or set target websocket url
        /// </summary>
        Uri Url { get; set; }

        /// <summary>
        /// Stream with received message (raw format)
        /// </summary>
        IObservable<ResponseMessage> MessageReceived { get; }

        /// <summary>
        /// Stream for reconnection event (triggered after the new connection) 
        /// </summary>
        IObservable<ReconnectionInfo> ReconnectionHappened { get; }

        /// <summary>
        /// Stream for disconnection event (triggered after the connection was lost) 
        /// </summary>
        IObservable<DisconnectionInfo> DisconnectionHappened { get; }

        /// <summary>
        /// Time range for how long to wait before reconnecting if no message comes from server.
        /// Set null to disable this feature. 
        /// Default: 1 minute.
        /// </summary>
        TimeSpan? ReconnectTimeout { get; set; }

        /// <summary>
        /// Time range for how long to wait before reconnecting if last reconnection failed.
        /// Set null to disable this feature. 
        /// Default: 1 minute.
        /// </summary>
        TimeSpan? ErrorReconnectTimeout { get; set; }

        /// <summary>
        /// Time range for how long to wait before reconnecting if connection is lost with a transient error.
        /// Set null to disable this feature. 
        /// Default: null/disabled (immediately)
        /// </summary>
        TimeSpan? LostReconnectTimeout { get; set; }

        /// <summary>
        /// Get or set the name of the current websocket client instance.
        /// For logging purpose (in case you use more parallel websocket clients and want to distinguish between them)
        /// </summary>
        string? Name { get; set; }

        /// <summary>
        /// Returns true if Start() method was called at least once. False if not started or disposed
        /// </summary>
        bool IsStarted { get; }

        /// <summary>
        /// Returns true if client is running and connected to the server
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// Enable or disable reconnection functionality (enabled by default)
        /// </summary>
        bool IsReconnectionEnabled { get; set; }

        /// <summary>
        /// Enable or disable text message conversion from binary to string (via 'MessageEncoding' property).
        /// Default: true
        /// </summary>
        bool IsTextMessageConversionEnabled { get; set; }

        /// <summary>
        /// Returns currently used native websocket client.
        /// Use with caution, on every reconnection there will be a new instance. 
        /// </summary>
        ClientWebSocket? NativeClient { get; }

        /// <summary>
        /// Sets used encoding for sending and receiving text messages. 
        /// Default: UTF8
        /// </summary>
        Encoding? MessageEncoding { get; set; }

        /// <summary>
        /// Start listening to the websocket stream on the background thread.
        /// In case of connection error it doesn't throw an exception.
        /// Only streams a message via 'DisconnectionHappened' and logs it. 
        /// </summary>
        Task Start();

        /// <summary>
        /// Start listening to the websocket stream on the background thread. 
        /// In case of connection error it throws an exception.
        /// Fail fast approach. 
        /// </summary>
        Task StartOrFail();

        /// <summary>
        /// Stop/close websocket connection with custom close code.
        /// Method doesn't throw exception, only logs it and mark client as closed. 
        /// </summary>
        /// <returns>Returns true if close was initiated successfully</returns>
        Task<bool> Stop(WebSocketCloseStatus status, string statusDescription);

        /// <summary>
        /// Stop/close websocket connection with custom close code.
        /// Method could throw exceptions, but client is marked as closed anyway.
        /// </summary>
        /// <returns>Returns true if close was initiated successfully</returns>
        Task<bool> StopOrFail(WebSocketCloseStatus status, string statusDescription);

        /// <summary>
        /// Send message to the websocket channel. 
        /// It inserts the message to the queue and actual sending is done on another thread
        /// </summary>
        /// <param name="message">Message to be sent</param>
        /// <returns>true if the message was written to the queue</returns>
        bool Send(string message);

        /// <summary>
        /// Send binary message to the websocket channel. 
        /// It inserts the message to the queue and actual sending is done on another thread
        /// </summary>
        /// <param name="message">Binary message to be sent</param>
        /// <returns>true if the message was written to the queue</returns>
        bool Send(byte[] message);

        /// <summary>
        /// Send binary message to the websocket channel. 
        /// It inserts the message to the queue and actual sending is done on another thread
        /// </summary>
        /// <param name="message">Binary message to be sent</param>
        /// <returns>true if the message was written to the queue</returns>
        bool Send(ArraySegment<byte> message);

        /// <summary>
        /// Send message to the websocket channel. 
        /// It doesn't use a sending queue, 
        /// beware of issue while sending two messages in the exact same time 
        /// on the full .NET Framework platform
        /// </summary>
        /// <param name="message">Message to be sent</param>
        Task SendInstant(string message);

        /// <summary>
        /// Send binary message to the websocket channel. 
        /// It doesn't use a sending queue, 
        /// beware of issue while sending two messages in the exact same time 
        /// on the full .NET Framework platform
        /// </summary>
        /// <param name="message">Message to be sent</param>
        Task SendInstant(byte[] message);

        /// <summary>
        /// Send already converted text message to the websocket channel. 
        /// Use this method to avoid double serialization of the text message.
        /// It inserts the message to the queue and actual sending is done on another thread
        /// </summary>
        /// <param name="message">Message to be sent</param>
        /// <returns>true if the message was written to the queue</returns>
        bool SendAsText(byte[] message);

        /// <summary>
        /// Send already converted text message to the websocket channel. 
        /// Use this method to avoid double serialization of the text message.
        /// It inserts the message to the queue and actual sending is done on another thread
        /// </summary>
        /// <param name="message">Message to be sent</param>
        /// <returns>true if the message was written to the queue</returns>
        bool SendAsText(ArraySegment<byte> message);

        /// <summary>
        /// Force reconnection. 
        /// Closes current websocket stream and perform a new connection to the server.
        /// In case of connection error it doesn't throw an exception, but tries to reconnect indefinitely. 
        /// </summary>
        Task Reconnect();

        /// <summary>
        /// Force reconnection. 
        /// Closes current websocket stream and perform a new connection to the server.
        /// In case of connection error it throws an exception and doesn't perform any other reconnection try. 
        /// </summary>
        Task ReconnectOrFail();

        /// <summary>
        /// Stream/publish fake message (via 'MessageReceived' observable).
        /// Use for testing purposes to simulate a server message. 
        /// </summary>
        /// <param name="message">Message to be stream</param>
        void StreamFakeMessage(ResponseMessage message);
    }
}