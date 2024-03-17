using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Websocket.Client
{
    public partial class WebsocketClient
    {

        /// <summary>
        /// Force reconnection. 
        /// Closes current websocket stream and perform a new connection to the server.
        /// In case of connection error it doesn't throw an exception, but tries to reconnect indefinitely. 
        /// </summary>
        public Task Reconnect(CancellationToken cancellation = default)
        {
            return ReconnectInternal(false, cancellation);
        }

        /// <summary>
        /// Force reconnection. 
        /// Closes current websocket stream and perform a new connection to the server.
        /// In case of connection error it throws an exception and doesn't perform any other reconnection try. 
        /// </summary>
        public Task ReconnectOrFail(CancellationToken cancellation = default)
        {
            return ReconnectInternal(true, cancellation);
        }

        private async Task ReconnectInternal(bool failFast, CancellationToken cancellation)
        {
            if (!IsStarted)
            {
                _logger.LogDebug(L("Client not started, ignoring reconnection.."), Name);
                return;
            }

            try
            {
                await ReconnectSynchronized(ReconnectionType.ByUser, failFast, null, cancellation).ConfigureAwait(false);
            }
            finally
            {
                _reconnecting = false;
            }
        }

        private async Task ReconnectSynchronized(ReconnectionType type, bool failFast, Exception? causedException, CancellationToken cancellation)
        {
            using (await _locker.LockAsync(cancellation))
            {
                await Reconnect(type, failFast, causedException, cancellation);
            }
        }

        private async Task Reconnect(ReconnectionType type, bool failFast, Exception? causedException, CancellationToken cancellation)
        {
            IsRunning = false;
            if (_disposing || !IsStarted)
            {
                // client already disposed or stopped manually
                return;
            }

            _reconnecting = true;

            var disType = TranslateTypeToDisconnection(type);
            var disInfo = DisconnectionInfo.Create(disType, _client, causedException);
            if (type != ReconnectionType.Error)
            {
                _disconnectedSubject.OnNext(disInfo);
                if (disInfo.CancelReconnection)
                {
                    // reconnection canceled by user, do nothing
                    _logger.LogInformation(L("Reconnecting canceled by user, exiting."), Name);
                }
            }

            _cancellation?.Cancel();
            try
            {
                _client?.Abort();
            }
            catch (Exception e)
            {
                _logger.LogError(e, L("Exception while aborting client. Error: '{error}'"), Name, e.Message);
            }
            _client?.Dispose();

            if (!IsReconnectionEnabled || disInfo.CancelReconnection)
            {
                // reconnection disabled, do nothing
                IsStarted = false;
                _reconnecting = false;
                return;
            }

            _logger.LogDebug(L("Reconnecting..."), Name);
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            await StartClient(_url, _cancellation.Token, type, failFast).ConfigureAwait(false);
            _reconnecting = false;
        }

        private void ActivateLastChance()
        {
            var timerMs = 1000 * 1;
            _lastChanceTimer = new Timer(LastChance, null, timerMs, timerMs);
        }

        private void DeactivateLastChance()
        {
            _lastChanceTimer?.Dispose();
            _lastChanceTimer = null;
        }

        private void LastChance(object? state)
        {
            if (!IsReconnectionEnabled || ReconnectTimeout == null)
            {
                // reconnection disabled, do nothing
                DeactivateLastChance();
                return;
            }

            var timeoutMs = Math.Abs(ReconnectTimeout.Value.TotalMilliseconds);
            var diffMs = Math.Abs(DateTime.UtcNow.Subtract(_lastReceivedMsg).TotalMilliseconds);
            if (diffMs > timeoutMs)
            {
                _logger.LogDebug(L("Last message received more than {timeoutMs} ms ago. Hard restart.."), Name, timeoutMs.ToString("F"));

                DeactivateLastChance();
                _ = ReconnectSynchronized(ReconnectionType.NoMessageReceived, false, null, _cancellation?.Token ?? CancellationToken.None);
            }
        }
    }
}
