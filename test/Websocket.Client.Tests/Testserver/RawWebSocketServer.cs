using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Websocket.Client.Tests.TestServer
{
    internal sealed class RawWebSocketServer : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);

        public RawWebSocketServer()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = new Uri($"ws://127.0.0.1:{port}/");
        }

        public Uri Url { get; }

        public async Task<TcpClient> AcceptConnectionAsync(CancellationToken cancellationToken)
        {
            var connection = await _listener.AcceptTcpClientAsync(cancellationToken);
            try
            {
                var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                string key = null;
                while (true)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null)
                        throw new EndOfStreamException("The client disconnected during the WebSocket handshake.");
                    if (line.Length == 0)
                        break;
                    if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                        key = line.Substring("Sec-WebSocket-Key:".Length).Trim();
                }

                if (key == null)
                    throw new InvalidOperationException("The WebSocket handshake did not contain a key.");

                var accept = Convert.ToBase64String(SHA1.HashData(
                    Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
                await stream.WriteAsync(response, cancellationToken);
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _listener.Stop();
        }
    }
}
