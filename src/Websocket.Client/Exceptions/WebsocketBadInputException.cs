using System;

namespace Websocket.Client.Exceptions
{
    /// <summary>
    /// Custom exception that indicates bad user/client input
    /// </summary>
    public class WebsocketBadInputException : WebsocketException
    {
        /// <inheritdoc />
        public WebsocketBadInputException()
        {
        }

        /// <inheritdoc />
        public WebsocketBadInputException(string message) : base(message)
        {
        }

        /// <inheritdoc />
        public WebsocketBadInputException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
