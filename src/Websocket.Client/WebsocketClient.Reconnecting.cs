﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Logging;

namespace Websocket.Client
{
    public partial class WebsocketClient
    {

        /// <summary>
        /// Force reconnection. 
        /// Closes current websocket stream and perform a new connection to the server.
        /// In case of connection error it doesn't throw an exception, but tries to reconnect indefinitely. 
        /// </summary>
        public Task Reconnect()
        {
            return ReconnectInternal(false);
        }

        /// <summary>
        /// Force reconnection. 
        /// Closes current websocket stream and perform a new connection to the server.
        /// In case of connection error it throws an exception and doesn't perform any other reconnection try. 
        /// </summary>
        public Task ReconnectOrFail()
        {
            return ReconnectInternal(true);
        }

        private async Task ReconnectInternal(bool failFast)
        {
            if (!IsStarted)
            {
                Logger.Debug(L("Client not started, ignoring reconnection.."));
                return;
            }

            try
            {
                await ReconnectSynchronized(ReconnectionType.ByUser, failFast, null).ConfigureAwait(false);
            }
            finally
            {
                _reconnecting = false;
            }
        }

        private async Task ReconnectSynchronized(ReconnectionType type, bool failFast, Exception causedException)
        {
            using (await _locker.LockAsync())
            {
                await Reconnect(type, failFast, causedException);
            }
        }

        private async Task Reconnect(ReconnectionType type, bool failFast, Exception causedException)
        {
            IsRunning = false;
            if (_disposing)
                return;

            _reconnecting = true;

            var disType = TranslateTypeToDisconnection(type);
            var disInfo = DisconnectionInfo.Create(disType, _client, causedException);
            if (type != ReconnectionType.Error)
            {
                _disconnectedSubject.OnNext(disInfo);
                if (disInfo.CancelReconnection)
                {
                    // reconnection canceled by user, do nothing
                    Logger.Info(L($"Reconnecting canceled by user, exiting."));
                }
            }
                
            _cancellation.Cancel();
            try
            {
                _client?.Abort();
            }
            catch (Exception e)
            {
                Logger.Error(e, L($"Exception while aborting client. " + $"Error: '{e.Message}'"));
            }
            _client?.Dispose();

            if (!IsReconnectionEnabled || disInfo.CancelReconnection)
            {
                // reconnection disabled, do nothing
                IsStarted = false;
                _reconnecting = false;
                return;
            }

            Logger.Debug(L("Reconnecting..."));
            _cancellation = new CancellationTokenSource();
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

        private void LastChance(object state)
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
                Logger.Debug(L($"Last message received more than {timeoutMs:F} ms ago. Hard restart.."));

                DeactivateLastChance();
                _ = ReconnectSynchronized(ReconnectionType.NoMessageReceived, false, null);
            }
        }
    }
}
