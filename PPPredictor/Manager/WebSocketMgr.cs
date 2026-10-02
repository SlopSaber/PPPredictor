using PPPredictor.Core.DataType;
using PPPredictor.Data;
using PPPredictor.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Zenject;
using static PPPredictor.Core.DataType.Enums;

namespace PPPredictor.WebSocket
{
    internal class WebSocketMgr : IInitializable, IDisposable
    {
        private readonly IPPPredictorMgr _ppPredictorMgr;
        private List<IPPPWebSocket> _lsWebSockets = new List<IPPPWebSocket>();
        private Dictionary<string, Task> dctWaitingRefresh = new Dictionary<string, Task>();
        private CancellationTokenSource refreshCancellation = new CancellationTokenSource();
        private long refreshRevision;
        private bool disposed;

        internal WebSocketOverlayServer OverlayServer;

        public WebSocketMgr(IPPPredictorMgr ppPredictorMgr)
        {
            this._ppPredictorMgr = ppPredictorMgr;
            RestartOverlayServer();
        }

        public void CreateScoreWebSockets()
        {
            if (disposed) return;
            if (Plugin.ProfileInfo.IsScoreSaberEnabled)
            {
                PPPWebSocket<PPPWsScoreSaberCommand> socket = new PPPWebSocket<PPPWsScoreSaberCommand>("wss://scoresaber.com/ws", Leaderboard.ScoreSaber.ToString());
                socket.OnScoreSet += PPPWebsocket_OnScoreSet;
                _lsWebSockets.Add(socket);
            }
            if (Plugin.ProfileInfo.IsBeatLeaderEnabled)
            {
                PPPWebSocket<PPPWsBeatLeaderData> socket = new PPPWebSocket<PPPWsBeatLeaderData>("wss://sockets.api.beatleader.com/scores", Leaderboard.BeatLeader.ToString());
                socket.OnScoreSet += PPPWebsocket_OnScoreSet;
                _lsWebSockets.Add(socket);
            }
        }

        private void PPPWebsocket_OnScoreSet(object sender, PPPScoreSetData data)
        {
            if (disposed || !_lsWebSockets.Contains(sender as IPPPWebSocket)) return;
            _ppPredictorMgr.ScoreSet(data.leaderboardName, data);
            if (Plugin.ProfileInfo.IsHitBloqEnabled)
            {
                AddDelayedRefresh(Leaderboard.HitBloq ,data);
            }
        }

        private void AddDelayedRefresh(Leaderboard leaderboard, PPPScoreSetData data)
        {
            if (disposed || refreshCancellation == null) return;
            string key = $"{leaderboard}_{data.hash}";
            if (!dctWaitingRefresh.ContainsKey(key))
            {
                dctWaitingRefresh.Add(key, WaitForRefresh(leaderboard, data, key, refreshRevision, refreshCancellation.Token));
            }
        }

        private async Task WaitForRefresh(Leaderboard leaderboard, PPPScoreSetData data, string key,
            long revision, CancellationToken cancellation)
        {
            try
            {
                await Task.Delay(5000, cancellation);
                if (!disposed && revision == refreshRevision)
                    _ppPredictorMgr.ScoreSet(leaderboard.ToString(), data);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                if (revision == refreshRevision)
                    dctWaitingRefresh.Remove(key);
            }
        }

        internal void RestartOverlayServer()
        {
            OverlayServer?.CloseSocket();
            OverlayServer = new WebSocketOverlayServer();
            OverlayServer.StartSocket();
        }

        #region init dispose
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            OverlayServer.CloseSocket();
            CloseScoreWebSockets();
        }

        public void CloseScoreWebSockets()
        {
            refreshRevision++;
            refreshCancellation?.Cancel();
            refreshCancellation?.Dispose();
            refreshCancellation = disposed ? null : new CancellationTokenSource();
            dctWaitingRefresh.Clear();
            foreach (var socket in _lsWebSockets)
            {
                socket.StopWebSocket();
                socket.OnScoreSet -= PPPWebsocket_OnScoreSet;
            }
            _lsWebSockets.Clear();
        }

        public void Initialize()
        {
            //throw new NotImplementedException();
        }
        #endregion
    }
}
