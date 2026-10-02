using System.Globalization;
using PPPredictor.Core.DataType;
using PPPredictor.Interfaces;
using System;
using System.Threading.Tasks;
using System.Threading;
using static PPPredictor.Core.DataType.Enums;

namespace PPPredictor.WebSocket
{
    internal class PPPWebSocket<T> : IPPPWebSocket where T : IPPPRawWebsocketData
    {
        public event EventHandler<PPPScoreSetData> OnScoreSet;
        private readonly string leaderboardName;
        private readonly string url;
        private readonly CancellationTokenSource retryCancellation = new CancellationTokenSource();
        private long identifier;
        private long revision;
        private bool stopped;
        private Task startup;
        private Task retry;

        public PPPWebSocket(string url, string leaderboardName)
        {
            this.url = url;
            this.leaderboardName = leaderboardName;
            startup = StartWebSocket();
        }

        private async Task StartWebSocket()
        {
            long requestRevision = revision;
            try
            {
                string userId = (await Plugin.GetUserInfoBS()).platformUserId;
                if (stopped || requestRevision != revision) return;
                identifier = ScoreSocketOwnerCallbacks.Register(leaderboardName, userId, PublishScore, OnSocketError);
                var culture = CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentCulture.Clone());
                var uiCulture = CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentUICulture.Clone());
                ScoreSocketTransportWorker.Start(identifier, url, leaderboardName, typeof(T), culture, uiCulture);
            }
            catch (Exception ex)
            {
                if (!stopped && requestRevision == revision)
                    Plugin.ErrorPrint($"Error creating Websocket for {leaderboardName}: {ex.Message}");
            }
        }

        private void PublishScore(PPPScoreSetData data)
        {
            OnScoreSet?.Invoke(this, data);
        }

        private void OnSocketError()
        {
            if (stopped || (retry != null && !retry.IsCompleted)) return;
            Plugin.ErrorPrint($"Error in Websocket for {leaderboardName} Retry connecting...");
            retry = Retry(revision, retryCancellation.Token);
        }

        private async Task Retry(long requestRevision, CancellationToken cancellation)
        {
            try
            {
                await Task.Delay(5000, cancellation);
                if (stopped || requestRevision != revision) return;
                revision++;
                RetireConnection();
                startup = StartWebSocket();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }

        public void StopWebSocket()
        {
            if (stopped) return;
            stopped = true;
            revision++;
            retryCancellation.Cancel();
            retryCancellation.Dispose();
            RetireConnection();
        }

        private void RetireConnection()
        {
            long retired = identifier;
            identifier = 0;
            if (retired == 0) return;
            ScoreSocketOwnerCallbacks.Unregister(retired);
            ScoreSocketTransportWorker.Stop(retired, leaderboardName,
                leaderboardName == Leaderboard.BeatLeader.ToString());
        }
    }
}
