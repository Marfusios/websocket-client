﻿using System;
using System.Collections.Generic;
using System.Text;
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
        /// </summary>
        public async Task Reconnect()
        {
            if (!IsStarted)
            {
                Logger.Debug(L("Client not started, ignoring reconnection.."));
                return;
            }

            try
            {
                await ReconnectSynchronized(ReconnectionType.ByUser).ConfigureAwait(false);

            }
            finally
            {
                _reconnecting = false;
            }
        }


        private async Task ReconnectSynchronized(ReconnectionType type)
        {
            using (await _locker.LockAsync())
            {
                await Reconnect(type);
            }
        }

        private async Task Reconnect(ReconnectionType type)
        {
            IsRunning = false;
            if (_disposing)
                return;

            _reconnecting = true;
            if (type != ReconnectionType.Error)
                _disconnectedSubject.OnNext(TranslateTypeToDisconnection(type));

            _cancellation.Cancel();
            _client?.Abort();
            _client?.Dispose();

            if (!IsReconnectionEnabled)
            {
                // reconnection disabled, do nothing
                IsStarted = false;
                _reconnecting = false;
                return;
            }

            Logger.Debug(L("Reconnecting..."));
            _cancellation = new CancellationTokenSource();
            await StartClient(_url, _cancellation.Token, type).ConfigureAwait(false);
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
            if (!IsReconnectionEnabled)
            {
                // reconnection disabled, do nothing
                DeactivateLastChance();
                return;
            }
            var timeoutMs = Math.Abs(ReconnectTimeoutMs);
            var diffMs = Math.Abs(DateTime.UtcNow.Subtract(_lastReceivedMsg).TotalMilliseconds);
            if (diffMs > timeoutMs)
            {
                Logger.Debug(L($"Last message received more than {timeoutMs:F} ms ago. Hard restart.."));

                DeactivateLastChance();
#pragma warning disable 4014
                ReconnectSynchronized(ReconnectionType.NoMessageReceived);
#pragma warning restore 4014
            }
        }
    }
}
