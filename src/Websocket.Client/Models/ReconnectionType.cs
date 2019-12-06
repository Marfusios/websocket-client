// ReSharper disable once CheckNamespace
namespace Websocket.Client
{
    /// <summary>
    /// Type that specify happened reconnection
    /// </summary>
    public enum ReconnectionType
    {
        /// <summary>
        /// Type used for initial connection to websocket stream
        /// </summary>
        Initial = 0,

        /// <summary>
        /// Type used when connection to websocket was lost in meantime
        /// </summary>
        Lost = 1,

        /// <summary>
        /// Type used when connection to websocket was lost by not receiving any message in given time-range
        /// </summary>
        NoMessageReceived = 2, 

        /// <summary>
        /// Type used after unsuccessful previous reconnection
        /// </summary>
        Error = 3,

        /// <summary>
        /// Type used when reconnection was requested by user
        /// </summary>
        ByUser = 4,

        /// <summary>
        /// Type used when reconnection was requested by server
        /// </summary>
        ByServer = 5
    }
}
