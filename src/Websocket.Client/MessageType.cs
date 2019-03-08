using System.Net.WebSockets;

namespace Websocket.Client
{
    public class MessageType
    {
        public byte[] RawData;
        public string Data;

        public WebSocketMessageType WebSocketMessageType { get; internal set; }
    }
}