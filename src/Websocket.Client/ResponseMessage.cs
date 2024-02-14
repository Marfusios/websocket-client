using System.IO;
using System.Net.WebSockets;

namespace Websocket.Client
{
    /// <summary>
    /// Received message, could be Text or Binary
    /// </summary>
    public class ResponseMessage
    {
        private readonly byte[]? _binary;

        private ResponseMessage(MemoryStream? memoryStream, byte[]? binary, string? text, WebSocketMessageType messageType)
        {
            Stream = memoryStream;
            _binary = binary;
            Text = text;
            MessageType = messageType;
        }

        /// <summary>
        /// Received text message (only if type = <see cref="WebSocketMessageType.Text"/>)
        /// </summary>
        public string? Text { get; }

        /// <summary>
        /// Received text message (only if type = <see cref="WebSocketMessageType.Binary"/>)
        /// </summary>
        public byte[]? Binary => Stream is null ? _binary : Stream.ToArray();

        /// <summary>
        /// Received stream message (only if type = <see cref="WebSocketMessageType.Binary"/> and <see cref="WebsocketClient.IsStreamDisposedAutomatically"/> = false)
        /// </summary>
        public MemoryStream? Stream { get; }

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
                return Text ?? string.Empty;
            }

            return $"Type binary, length: {Binary?.Length}";
        }

        /// <summary>
        /// Create text response message
        /// </summary>
        public static ResponseMessage TextMessage(string? data)
        {
            return new ResponseMessage(null, null, data, WebSocketMessageType.Text);
        }

        /// <summary>
        /// Create binary response message
        /// </summary>
        public static ResponseMessage BinaryMessage(byte[]? data)
        {
            return new ResponseMessage(null, data, null, WebSocketMessageType.Binary);
        }

        /// <summary>
        /// Create stream response message
        /// </summary>
        public static ResponseMessage BinaryStreamMessage(MemoryStream? memoryStream)
        {
            return new ResponseMessage(memoryStream, null, null, WebSocketMessageType.Binary);
        }
    }
}
