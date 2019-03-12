using System.Net.WebSockets;

namespace Websocket.Client
{
    /// <summary>
    /// Received message, could be Text or Binary
    /// </summary>
    public class ResponseMessage
    {
        private ResponseMessage(byte[] binary, string text, WebSocketMessageType messageType)
        {
            Binary = binary;
            Text = text;
            MessageType = messageType;
        }

        /// <summary>
        /// Received text message (only if type = WebSocketMessageType.Text)
        /// </summary>
        public string Text { get; }

        /// <summary>
        /// Received text message (only if type = WebSocketMessageType.Binary)
        /// </summary>
        public byte[] Binary { get; }

        /// <summary>
        /// Current message type (Text or Binary)
        /// </summary>
        public WebSocketMessageType MessageType { get; }

        /// <summary>
        /// Return string info about the message
        /// </summary>
        public override string ToString()
        {
            if (MessageType == WebSocketMessageType.Text)
            {
                return Text;
            }

            return $"Type binary, length: {Binary?.Length}";
        }

        /// <summary>
        /// Create text response message
        /// </summary>
        public static ResponseMessage TextMessage(string data)
        {
            return new ResponseMessage(null, data, WebSocketMessageType.Text);
        }

        /// <summary>
        /// Create binary response message
        /// </summary>
        public static ResponseMessage BinaryMessage(byte[] data)
        {
            return new ResponseMessage(data, null, WebSocketMessageType.Binary);
        }
    }
}