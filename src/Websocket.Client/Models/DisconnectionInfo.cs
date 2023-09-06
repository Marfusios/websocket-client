using System;
using System.Net.WebSockets;

// ReSharper disable once CheckNamespace
namespace Websocket.Client
{
    /// <summary>
    /// Info about happened disconnection
    /// </summary>
    public class DisconnectionInfo
    {
        /// <summary>
        /// Info about happened disconnection
        /// </summary>
        public DisconnectionInfo(DisconnectionType type, WebSocketCloseStatus? closeStatus,
            string? closeStatusDescription, string? subProtocol, Exception? exception)
        {
            Type = type;
            CloseStatus = closeStatus;
            CloseStatusDescription = closeStatusDescription;
            SubProtocol = subProtocol;
            Exception = exception;
        }

        /// <summary>
        /// Disconnection reason
        /// </summary>
        public DisconnectionType Type { get; }

        /// <summary>
        /// Indicates the reason why the remote endpoint initiated the close handshake 
        /// </summary>
        public WebSocketCloseStatus? CloseStatus { get; }

        /// <summary>
        /// Allows the remote endpoint to describe the reason why the connection was closed 
        /// </summary>
        public string? CloseStatusDescription { get; }

        /// <summary>
        /// The subprotocol that was negotiated during the opening handshake
        /// </summary>
        public string? SubProtocol { get; }

        /// <summary>
        /// Exception that cause disconnection, can be null
        /// </summary>
        public Exception? Exception { get; }


        /// <summary>
        /// Set to true if you want to cancel ongoing reconnection
        /// </summary>
        public bool CancelReconnection { get; set; }

        /// <summary>
        /// Set to true if you want to cancel ongoing connection close (only when Type = ByServer)
        /// </summary>
        public bool CancelClosing { get; set; }


        /// <summary>
        /// Simple factory method
        /// </summary>
        public static DisconnectionInfo Create(DisconnectionType type, WebSocket? client, Exception? exception)
        {
            return new DisconnectionInfo(type, client?.CloseStatus, client?.CloseStatusDescription,
                client?.SubProtocol, exception);
        }
    }
}
