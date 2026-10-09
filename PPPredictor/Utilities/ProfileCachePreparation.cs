using PPPredictor.Core.DataType.Score;
using PPPredictor.Data;
using System;
using System.Collections.Generic;

namespace PPPredictor.Utilities
{
    internal static class ProfileCachePreparation
    {
        private const int MinimumEntries = 4096;
        private const int BatchSize = 4096;
        private const int MinimumWorkerBatch = 2048;
        private const long MinimumNowTicks = -ProfileInfo.RefetchMapInfoAfterDays * TimeSpan.TicksPerDay;

        internal static bool TryPrune(List<ShortScore> source, out List<ShortScore> result)
        {
            result = null;
            if (source == null || source.GetType() != typeof(List<ShortScore>) || source.Count < MinimumEntries)
                return false;

            result = new List<ShortScore>();
            var rows = new ShortScore[BatchSize];
            ProfileFileWorker.CacheDatePair[] dates = null;
            int count = 0;
            foreach (ShortScore row in source)
            {
                if (dates == null)
                    dates = new ProfileFileWorker.CacheDatePair[BatchSize];
                DateTime fetched = row.FetchTime;
                DateTime now = DateTime.Now;
                if (now.Ticks < MinimumNowTicks)
                    now.AddDays(ProfileInfo.RefetchMapInfoAfterDays);
                dates[count] = new ProfileFileWorker.CacheDatePair(fetched, now);
                rows[count] = row;
                count++;
                if (count == BatchSize)
                {
                    Append(result, rows, dates, count);
                    dates = null;
                    count = 0;
                }
            }
            if (count > 0)
                Append(result, rows, dates, count);
            return true;
        }

        private static void Append(List<ShortScore> result, ShortScore[] rows,
            ProfileFileWorker.CacheDatePair[] dates, int count)
        {
            bool[] keep = null;
            if (count >= MinimumWorkerBatch)
            {
                try
                {
                    keep = ProfileFileWorker.PrepareCache(dates, count);
                }
                catch (Exception)
                {
                    // The request owns its unchanged dates, including after an interrupted wait.
                }
            }
            for (int index = 0; index < count; index++)
            {
                ProfileFileWorker.CacheDatePair pair = dates[index];
                bool retained = keep != null ? keep[index] :
                    pair.fetchTime > pair.now.AddDays(ProfileInfo.RefetchMapInfoAfterDays);
                if (retained)
                    result.Add(rows[index]);
            }
        }
    }
}
