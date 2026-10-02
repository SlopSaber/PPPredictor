using IPA.Utilities.Async;
using PPPredictor.Core.DataType;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PPPredictor.WebSocket
{
    internal static class ScoreSocketOwnerCallbacks
    {
        private sealed class Entry
        {
            internal readonly string Leaderboard;
            internal readonly string UserId;
            internal readonly Action<PPPScoreSetData> Score;
            internal readonly Action Error;

            internal Entry(string leaderboard, string userId, Action<PPPScoreSetData> score, Action error)
            {
                Leaderboard = leaderboard;
                UserId = userId;
                Score = score;
                Error = error;
            }
        }

        // Entries and their native callback cohort are accessed only on the owner.
        private static readonly Dictionary<long, Entry> entries = new Dictionary<long, Entry>();
        private static long nextIdentifier;

        internal static long Register(string leaderboard, string userId, Action<PPPScoreSetData> score, Action error)
        {
            long identifier = ++nextIdentifier;
            entries.Add(identifier, new Entry(leaderboard, userId, score, error));
            return identifier;
        }

        internal static void Unregister(long identifier)
        {
            entries.Remove(identifier);
        }

        internal static void PostScore(long identifier, PPPScoreSetData data)
        {
            Post(() =>
            {
                if (!entries.TryGetValue(identifier, out Entry entry) || data.userId != entry.UserId) return;
                try
                {
                    entry.Score(data);
                }
                catch (Exception ex)
                {
                    Plugin.ErrorPrint($"Error in Websocket for {entry.Leaderboard} OnMessage: {ex.Message}");
                }
            });
        }

        internal static void PostError(long identifier)
        {
            Post(() =>
            {
                if (entries.TryGetValue(identifier, out Entry entry))
                    entry.Error();
            });
        }

        internal static void PostMessageError(long identifier, string message)
        {
            Post(() =>
            {
                if (entries.TryGetValue(identifier, out Entry entry))
                    Plugin.ErrorPrint($"Error in Websocket for {entry.Leaderboard} OnMessage: {message}");
            });
        }

        internal static void PostCreationError(long identifier, string message)
        {
            Post(() =>
            {
                if (entries.TryGetValue(identifier, out Entry entry))
                    Plugin.ErrorPrint($"Error creating Websocket for {entry.Leaderboard}: {message}");
            });
        }

        internal static void PostCleanupError(string message)
        {
            Post(() => Plugin.ErrorPrint(message));
        }

        private static void Post(Action callback)
        {
            try
            {
                _ = UnityMainThreadTaskScheduler.Factory.StartNew(callback);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (TaskSchedulerException)
            {
            }
        }
    }
}
