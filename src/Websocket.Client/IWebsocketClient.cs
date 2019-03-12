using System;
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
        /// Stream with received message (raw format)
        /// </summary>
        IObservable<ResponseMessage> MessageReceived { get; }
            /// <summary>
        /// Stream for reconnection event (triggered after the new connection) 
        /// </summary>
        IObservable<ReconnectionType> ReconnectionHappened { get; }

        /// <summary>
        /// Stream for disconnection event (triggered after the connection was lost) 
        /// </summary>
        IObservable<DisconnectionType> DisconnectionHappened { get; }

        /// <summary>
        /// Time range in ms, how long to wait before reconnecting if no message comes from server.
        /// Default 60000 ms (1 minute)
        /// </summary>
        int ReconnectTimeoutMs { get; set; }

        /// <summary>
        /// Time range in ms, how long to wait before reconnecting if last reconnection failed.
        /// Default 60000 ms (1 minute)
        /// </summary>
        int ErrorReconnectTimeoutMs { get; set; }

        /// <summary>
        /// Get or set the name of the current websocket client instance.
        /// For logging purpose (in case you use more parallel websocket clients and want to distinguish between them)
        /// </summary>
        string Name { get; set;}

        /// <summary>
        /// Returns true if Start() method was called at least once. False if not started or disposed
        /// </summary>
        bool IsStarted { get; }

        /// <summary>
        /// Returns true if client is running and connected to the server
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// Sets used encoding for sending and receiving text messages.
        /// Default is UTF8
        /// </summary>
        Encoding MessageEncoding { get; set; }

        /// <summary>
        /// Start listening to the websocket stream on the background thread
        /// </summary>
        Task Start();

        /// <summary>
        /// Send message to the websocket channel. 
        /// It inserts the message to the queue and actual sending is done on an other thread
        /// </summary>
        /// <param name="message">Message to be sent</param>
        Task Send(string message);

        /// <summary>
        /// Send message to the websocket channel. 
        /// It doesn't use a sending queue, 
        /// beware of issue while sending two messages in the exact same time 
        /// on the full .NET Framework platform
        /// </summary>
        /// <param name="message">Message to be sent</param>
        Task SendInstant(string message);

        /// <summary>
        /// Force reconnection. 
        /// Closes current websocket stream and perform a new connection to the server.
        /// </summary>
        Task Reconnect();
    }
}