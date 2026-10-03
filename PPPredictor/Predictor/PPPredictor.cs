using PPPredictor.Core;
using PPPredictor.Core.Calculator;
using PPPredictor.Core.DataType;
using PPPredictor.Core.DataType.BeatSaberEncapsulation;
using PPPredictor.Core.DataType.LeaderBoard;
using PPPredictor.Data;
using PPPredictor.Data.DisplayInfos;
using PPPredictor.Interfaces;
using PPPredictor.Converter;
using SongCore.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using IPA.Utilities.Async;
using static PPPredictor.Core.DataType.Enums;
using PPPredictor.Core.DataType.MapPool;
using SongCore;

namespace PPPredictor.Utilities
{
    internal class PPPredictor : IPPPredictor, IDisposable
    {
        internal Leaderboard leaderboardName;
        #region internal values
        private float _percentage;
        private PPPBeatMapInfo _currentBeatMapInfo = new PPPBeatMapInfo();
        private bool _rankGainRunning = false;
        private RankRequest _pendingRankRequest;
        private bool _isActive = false;
        private DisplayPPInfo _ppDisplay = new DisplayPPInfo();
        private PPGainResult _ppGainResult = new PPGainResult();
        private bool _disposed;
        private long _rankRevision;
        private CancellationTokenSource _rankDebounceCancellation;
        private Task _rankDebounceTask;
        private Task _rankTask;
        private readonly CalculatorInstance calculatorInstance;
        private PPPMapPoolShort currentMapPool;
        private long _mapPoolIconRevision;
        private Task _mapPoolIconLoad;
        private PPPMapPoolShort _mapPoolIconLoadPool;
        private long _mapPoolIconLoadRevision;
        private List<PPPMapPoolShort> lsMapPools = new List<PPPMapPoolShort>();
        #endregion

        public string LeaderBoardName
        {
            get
            {
                return _leaderboardInfo.LeaderboardName;
            }
        }
        public string LeaderBoardIcon
        {
            get { return _leaderboardInfo.LeaderboardIcon; }
        }
        public string MapPoolIcon
        {
            get
            {
                string mapPoolIcon = currentMapPool?.IconUrl ?? string.Empty;
                return !string.IsNullOrEmpty(mapPoolIcon) ? mapPoolIcon : _leaderboardInfo.LeaderboardIcon;
            }
        }
        public byte[] MapPoolIconData
        {
            get { return currentMapPool?.IconData; }
            set { currentMapPool.IconData = value; }
        }

        internal PPPLeaderboardInfo _leaderboardInfo;
        private GameplayModifiers _gameplayModifiers;

        public event EventHandler<bool> OnDataLoading;
        public event EventHandler<DisplaySessionInfo> OnDisplaySessionInfo;
        public event EventHandler<DisplayPPInfo> OnDisplayPPInfo;
        public event EventHandler OnMapPoolRefreshed;

        #region
        public PPPredictor(Leaderboard leaderBoard, CalculatorInstance calculatorInstance)
        {
            leaderboardName = leaderBoard;
            this.calculatorInstance = calculatorInstance;
            _leaderboardInfo = new PPPLeaderboardInfo(leaderBoard);
            lsMapPools = this.calculatorInstance.GetMapPools(leaderboardName);
            //Select current map pool from save data
            int index = 0;
            if(Plugin.ProfileInfo.MapPoolSelection.TryGetValue(leaderboardName.ToString(), out string value))
            {
                index = Math.Max(lsMapPools.FindIndex(x => x.Id == value), index);
            }
            currentMapPool = lsMapPools[index];
            currentMapPool.SelectedByLoading = true;

            //_ppCalculator.OnMapPoolRefreshed += PPCalculator_OnMapPoolRefreshed;

        }
        #endregion

        #region getter/setter
        public float Percentage
        {
            get => _percentage;
            set
            {
                _percentage = value;
            }
        }
        #region MapPools
        public List<object> MapPoolOptions
        {
            get
            {
                return lsMapPools.Select(f => (object)f).ToList();
            }
        }
        public object CurrentMapPool
        {
            get => (object)currentMapPool;
            set
            {
                if (_disposed) return;
                bool isCurrentMapPoolChanging = IsCurrentMapPoolChanging(value);
                if (isCurrentMapPoolChanging || !ReferenceEquals(currentMapPool, value))
                    InvalidateRankRequests();
                _mapPoolIconRevision++;
                currentMapPool = (PPPMapPoolShort)value;
                UpdateMapPoolDetails();
                if (isCurrentMapPoolChanging)
                {
                    currentMapPool.SelectedByLoading = false;
                    this.RefreshCurrentData(10, true);
                }
                if(currentMapPool != null)
                {
                    Plugin.ProfileInfo.MapPoolSelection[leaderboardName.ToString()] = currentMapPool.Id;
                }
                SetActive(true, isCurrentMapPoolChanging);
            }
        }
        #endregion
        public string PPSuffix
        {
            get => _leaderboardInfo.PpSuffix;
        }
        #endregion

        #region eventHandling

        public void ChangeGameplayModifiers(GameplaySetupViewController gameplaySetupViewController)
        {
            if (currentMapPool != null && gameplaySetupViewController != null && gameplaySetupViewController.gameplayModifiers != null)
            {
                _gameplayModifiers = gameplaySetupViewController.gameplayModifiers;
                _currentBeatMapInfo = calculatorInstance.ApplyModifiersToBeatmapInfo(leaderboardName, currentMapPool.Id, _currentBeatMapInfo, Converter.Converter.ConvertGameplayModifiers(_gameplayModifiers));
                _currentBeatMapInfo.MaxPP = -1;
                CalculatePP();
            }
        }

        public async void DifficultyChanged(BeatmapLevel selectedBeatmapLevel, BeatmapKey beatmapKey)
        {
            await UpdateCurrentBeatMapInfos(selectedBeatmapLevel, beatmapKey);
        }

        public async Task UpdateCurrentBeatMapInfos(BeatmapLevel selectedBeatmapLevel, BeatmapKey beatmapKey)
        {
            _currentBeatMapInfo = new PPPBeatMapInfo(selectedBeatmapLevel != null ? Collections.GetCustomLevelHash(selectedBeatmapLevel.levelID) : null, Converter.Converter.ConvertBeatmapKey(beatmapKey));
            await UpdateCurrentBeatMapInfos();
            CalculatePP();
        }

        private async Task UpdateCurrentBeatMapInfos()
        {
            _currentBeatMapInfo = await calculatorInstance.GetBeatMapInfoAsync(leaderboardName, currentMapPool.Id, _currentBeatMapInfo);
            _currentBeatMapInfo.SelectedMapSearchString = !string.IsNullOrEmpty(_currentBeatMapInfo.CustomLevelHash) ? PPCalculator.CreateSeachString(_currentBeatMapInfo.CustomLevelHash, "SOLO" + _currentBeatMapInfo.BeatmapKey.serializedName, Core.ParsingUtil.ParseDifficultyNameToInt(_currentBeatMapInfo.BeatmapKey.difficulty.ToString())) : string.Empty;
            _currentBeatMapInfo.OldDotsEnabled = IsOldDotsActive(_currentBeatMapInfo.BeatmapKey);
            _currentBeatMapInfo = GetModifiedBeatMapInfo(_gameplayModifiers);
            _currentBeatMapInfo.MaxPP = -1;
        }
        #endregion

        #region event sending
        private void IsDataLoading(bool isDataLoading)
        {
            if (!_disposed) OnDataLoading?.Invoke(this, isDataLoading);
        }
        private void SendDisplayPPInfo(DisplayPPInfo displayPPInfo)
        {
            if (!_disposed && _isActive) OnDisplayPPInfo?.Invoke(this, displayPPInfo);
        }
        private void SendDisplaySessionInfo(DisplaySessionInfo displaySessionInfo)
        {
            if (!_disposed && _isActive) OnDisplaySessionInfo?.Invoke(this, displaySessionInfo);
        }
        private void PPCalculator_OnMapPoolRefreshed(object sender, EventArgs e)
        {
            if (!_disposed) OnMapPoolRefreshed?.Invoke(this, null);
        }
        #endregion
        public double CalculatePPatPercentage(double percentage, PPPBeatMapInfo beatMapInfo, bool levelFailed = false, bool levelPaused = false)
        {
            var v = calculatorInstance.CalculatePPatPercentage(leaderboardName, currentMapPool.Id, beatMapInfo, percentage, levelFailed, levelPaused);
            return v;
        }

        public double CalculateMaxPP()
        {
            var v = calculatorInstance.CalculateMaxPP(leaderboardName, currentMapPool.Id, _currentBeatMapInfo);
            return v;
        }

        public PPPBeatMapInfo GetModifiedBeatMapInfo(GameplayModifiers gameplayModifiers, bool levelFailed = false, bool levelPaused = false)
        {
            PPPBeatMapInfo pppBeatMapInfo = calculatorInstance.ApplyModifiersToBeatmapInfo(leaderboardName, currentMapPool.Id, _currentBeatMapInfo, Converter.Converter.ConvertGameplayModifiers(gameplayModifiers), levelFailed, levelPaused);
            return pppBeatMapInfo;
        }

        public bool IsOldDotsActive(Core.DataType.BeatSaberEncapsulation.BeatmapKey beatmapKey)
        {
            return beatmapKey?.serializedName?.Contains(Core.Constants.OldDots) ?? false;
        }

        public double CalculatePPGain(double pp)
        {
            PPGainResult ppGainResult = calculatorInstance.GetPlayerScorePPGain(leaderboardName, currentMapPool.Id, _currentBeatMapInfo.SelectedMapSearchString, pp);
            return ppGainResult.PpDisplayValue;
        }

        public bool IsRanked()
        {
            try
            {
                return _currentBeatMapInfo.BaseStarRating.IsRanked() && (_leaderboardInfo.HasOldDotRanking || !_currentBeatMapInfo.OldDotsEnabled);
            }
            catch (Exception ex)
            {
                Plugin.ErrorPrint($"IsRanked {ex.Message}");
                return false;
            }
        }

        public double? GetPersonalBest()
        {
            return calculatorInstance.GetPersonalBest(leaderboardName, currentMapPool.Id, _currentBeatMapInfo.SelectedMapSearchString);
        }

        internal bool IsCurrentMapPoolChanging(object value)
        {
            
            var currentPool = CurrentMapPool as PPPMapPoolShort;
            var newMapPool = value as PPPMapPoolShort;
            return (currentPool == null || (currentPool != null && ((newMapPool != null && currentPool.Id != newMapPool.Id) || currentMapPool.SelectedByLoading)));
        }

        public void CalculatePP()
        {
            if (_disposed || currentMapPool == null) return;
            InvalidateRankRequests();
            if (_currentBeatMapInfo.MaxPP == -1) _currentBeatMapInfo.MaxPP = CalculateMaxPP();
            double pp = CalculatePPatPercentage(_percentage, _currentBeatMapInfo);
            _ppGainResult = calculatorInstance.GetPlayerScorePPGain(leaderboardName, currentMapPool.Id, _currentBeatMapInfo.SelectedMapSearchString, pp);
            double ppGains = PPCalculator.Zeroizer(_ppGainResult.PpDisplayValue);
            _ppDisplay = new DisplayPPInfo();
            if (_currentBeatMapInfo.MaxPP > 0 && pp >= _currentBeatMapInfo.MaxPP)
            {
                _ppDisplay.PPRaw = $"<color=\"yellow\">{pp:F2}{PPSuffix}</color>";
            }
            else
            {
                _ppDisplay.PPRaw = $"{pp:F2}{PPSuffix}";
            }
            _ppDisplay.PPGain = $"{ppGains:+0.##;-0.##;0}{PPSuffix}";
            _ppDisplay.PPGainDiffColor = DisplayHelper.GetDisplayColor(ppGains, false, true);

            var request = new RankRequest(_rankRevision, currentMapPool, _ppDisplay, _ppGainResult.PpTotal);
            DisplayRankGain(null, request.Display);
            if (!IsCurrentRankRequest(request)) return;
            _rankDebounceCancellation = new CancellationTokenSource();
            CancellationToken token = _rankDebounceCancellation.Token;
            Task delay = Task.Delay(500, token);
            // The owner scheduler also owns continuations after the delay and calculator worker.
            _rankDebounceTask = UnityMainThreadTaskScheduler.Factory.StartNew(
                () => DebounceRankAsync(request, token, delay)).Unwrap();
        }

        private async Task DebounceRankAsync(RankRequest request, CancellationToken token, Task delay)
        {
            try
            {
                await delay;
                if (token.IsCancellationRequested || !IsCurrentRankRequest(request)) return;
                if (_rankGainRunning)
                    _pendingRankRequest = request;
                else
                    _rankTask = RunRankRequestsAsync(request);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (IsCurrentRankRequest(request)) Plugin.ErrorPrint($"Rank debounce {ex.Message}");
            }
        }

        private async Task RunRankRequestsAsync(RankRequest request)
        {
            _rankGainRunning = true;
            try
            {
                while (request != null && !_disposed)
                {
                    try
                    {
                        RankGainResult result = await calculatorInstance.GetPlayerRankGain(
                            leaderboardName, request.PoolId, request.PpTotal);
                        if (IsCurrentRankRequest(request)) DisplayRankGain(result, request.Display);
                    }
                    catch (Exception ex)
                    {
                        if (IsCurrentRankRequest(request)) Plugin.ErrorPrint($"Rank gain {ex.Message}");
                    }

                    request = _pendingRankRequest;
                    _pendingRankRequest = null;
                    if (request != null && (!(request.PpTotal > 0) || !IsCurrentRankRequest(request)))
                        request = null;
                }
            }
            finally
            {
                _rankGainRunning = false;
            }
        }

        private bool IsCurrentRankRequest(RankRequest request)
        {
            return !_disposed && request.Revision == _rankRevision
                && ReferenceEquals(request.Pool, currentMapPool)
                && string.Equals(request.PoolId, currentMapPool?.Id, StringComparison.Ordinal)
                && ReferenceEquals(request.Display, _ppDisplay);
        }

        private void InvalidateRankRequests()
        {
            _rankRevision++;
            _pendingRankRequest = null;
            _rankDebounceCancellation?.Cancel();
            _rankDebounceCancellation?.Dispose();
            _rankDebounceCancellation = null;
        }

        private sealed class RankRequest
        {
            internal readonly long Revision;
            internal readonly PPPMapPoolShort Pool;
            internal readonly string PoolId;
            internal readonly DisplayPPInfo Display;
            internal readonly double PpTotal;

            internal RankRequest(long revision, PPPMapPoolShort pool, DisplayPPInfo display, double ppTotal)
            {
                Revision = revision;
                Pool = pool;
                PoolId = pool.Id;
                Display = display;
                PpTotal = ppTotal;
            }
        }

        private void DisplayRankGain(RankGainResult rankGainResult, DisplayPPInfo ppDisplay)
        {
            if (rankGainResult != null)
            {
                ppDisplay.PredictedRankDiffColor = DisplayHelper.GetDisplayColor(rankGainResult.RankGainGlobal, false);
                ppDisplay.PredictedCountryRankDiffColor = _leaderboardInfo.IsCountryRankEnabled ? DisplayHelper.GetDisplayColor(rankGainResult.RankGainCountry, false) : DisplayHelper.ColorCountryRankDisabled;
                if (rankGainResult.IsRankGainCanceledByLimit)
                {
                    ppDisplay.PredictedRank = $"<{rankGainResult.RankGlobal:N0}";
                    ppDisplay.PredictedRankDiff = rankGainResult.RankGainGlobal.ToString(">+#;<-#;0");
                    ppDisplay.PredictedCountryRank = $"<{rankGainResult.RankCountry:N0}";
                    ppDisplay.PredictedCountryRankDiff = rankGainResult.RankGainCountry.ToString(">+#;<-#;0");
                }
                else
                {
                    ppDisplay.PredictedRank = $"{rankGainResult.RankGlobal:N0}";
                    ppDisplay.PredictedRankDiff = rankGainResult.RankGainGlobal.ToString("+#;-#;0");
                    ppDisplay.PredictedCountryRank = $"{rankGainResult.RankCountry:N0}";
                    ppDisplay.PredictedCountryRankDiff = rankGainResult.RankGainCountry.ToString("+#;-#;0");
                }
            }
            else
            {
                ppDisplay.PredictedRank = "...";
                ppDisplay.PredictedRankDiff = "?";
                ppDisplay.PredictedRankDiffColor = DisplayHelper.GetDisplayColor(0, false);
                ppDisplay.PredictedCountryRank = "...";
                ppDisplay.PredictedCountryRankDiff = "?";
                ppDisplay.PredictedCountryRankDiffColor = _leaderboardInfo.IsCountryRankEnabled ? DisplayHelper.GetDisplayColor(0, false) : DisplayHelper.ColorCountryRankDisabled;
            }
            SendDisplayPPInfo(ppDisplay);
        }

        private async Task DisplaySession(bool doResetSession)
        {
            (PPPPlayer sessionPlayer, PPPPlayer currentPlayer) = await calculatorInstance.UpdatePlayer(leaderboardName, currentMapPool.Id, doResetSession);
            DisplaySessionInfo sessionDisplay = new DisplaySessionInfo();
            if (sessionPlayer != null && currentPlayer != null)
            {
                if (Plugin.ProfileInfo.DisplaySessionValues)
                {
                    sessionDisplay.SessionRank = $"{sessionPlayer.Rank:N0}";
                    sessionDisplay.SessionCountryRank = $"{sessionPlayer.CountryRank:N0}";
                    sessionDisplay.SessionPP = $"{sessionPlayer.Pp:F2}{_leaderboardInfo.PpSuffix}";
                }
                else
                {
                    sessionDisplay.SessionRank = $"{currentPlayer.Rank:N0}";
                    sessionDisplay.SessionCountryRank = $"{currentPlayer.CountryRank:N0}";
                    sessionDisplay.SessionPP = $"{currentPlayer.Pp:F2}{_leaderboardInfo.PpSuffix}";
                }
                sessionDisplay.SessionCountryRankDiff = (currentPlayer.CountryRank - sessionPlayer.CountryRank).ToString("+#;-#;0");
                sessionDisplay.SessionCountryRankDiffColor = _leaderboardInfo.IsCountryRankEnabled ? DisplayHelper.GetDisplayColor((currentPlayer.CountryRank - sessionPlayer.CountryRank), true) : DisplayHelper.ColorCountryRankDisabled;
                sessionDisplay.SessionRankDiff = (currentPlayer.Rank - sessionPlayer.Rank).ToString("+#;-#;0");
                sessionDisplay.SessionRankDiffColor = DisplayHelper.GetDisplayColor((currentPlayer.Rank - sessionPlayer.Rank), true);
                sessionDisplay.SessionPPDiff = $"{PPCalculator.Zeroizer(currentPlayer.Pp - sessionPlayer.Pp):+0.##;-0.##;0}{_leaderboardInfo.PpSuffix}";
                sessionDisplay.SessionPPDiffColor = DisplayHelper.GetDisplayColor((currentPlayer.Pp - sessionPlayer.Pp), false);
            }
            sessionDisplay.CountryRankFontColor = _leaderboardInfo.IsCountryRankEnabled ? DisplayHelper.ColorWhite : DisplayHelper.ColorCountryRankDisabled;
            SendDisplaySessionInfo(sessionDisplay);
        }

        public void ScoreSet(PPPScoreSetData data)
        {
            if(calculatorInstance.IsScoreSetOnCurrentMapPool(leaderboardName, currentMapPool.Id, data)) 
                RefreshCurrentData(1, false, true);
        }

        public async void RefreshCurrentData(int fetchLength, bool refreshStars = false, bool fetchOnePage = false)
        {
            await UpdateCurrentAndCheckResetSession(false);
            IsDataLoading(true);
            await calculatorInstance.GetPlayerScores(leaderboardName, currentMapPool.Id, fetchLength, _leaderboardInfo.LargePageSize, fetchOnePage);
            if (refreshStars) //MapPool change to a pool that has never been selected before;
            {
                await UpdateCurrentBeatMapInfos();
            }
            CalculatePP();
            IsDataLoading(false);
        }

        public async Task UpdateCurrentAndCheckResetSession(bool doResetSession)
        {
            IsDataLoading(true);
            await DisplaySession(doResetSession);
            IsDataLoading(false);
        }

        public async void ResetDisplay(bool resetAll)
        {
            if (_disposed) return;
            InvalidateRankRequests();
            await UpdateCurrentAndCheckResetSession(resetAll);
            IsDataLoading(true);
            await calculatorInstance.GetPlayerScores(leaderboardName, currentMapPool.Id, 100, 100);
            CalculatePP();
            IsDataLoading(false);
        }

        public void SetActive(bool setActive, bool hasPoolChanged = false)
        {
            if (_disposed) return;
            if (_isActive && setActive && !hasPoolChanged) return;
            if (_isActive != setActive || hasPoolChanged) InvalidateRankRequests();
            _isActive = setActive;
            if (setActive || hasPoolChanged)
            {
                _ = DisplaySession(false);
                CalculatePP();
            }
        }

        private async void UpdateMapPoolDetails()
        {
            if (currentMapPool != null)
            {
                IsDataLoading(true);
                await calculatorInstance.UpdateMapPoolDetails(leaderboardName, currentMapPool.Id);
                IsDataLoading(false);
            }
        }

        public Task GetMapPoolIconData()
        {
            PPPMapPoolShort pool = currentMapPool;
            long revision = _mapPoolIconRevision;
            if (_mapPoolIconLoad != null && !_mapPoolIconLoad.IsCompleted
                && ReferenceEquals(pool, _mapPoolIconLoadPool)
                && revision == _mapPoolIconLoadRevision)
                return _mapPoolIconLoad;

            _mapPoolIconLoadPool = pool;
            _mapPoolIconLoadRevision = revision;
            _mapPoolIconLoad = LoadMapPoolIconData(pool, MapPoolIcon, revision);
            return _mapPoolIconLoad;
        }

        private async Task LoadMapPoolIconData(PPPMapPoolShort pool, string iconUrl, long revision)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    using (var response = await client.GetAsync(iconUrl))
                    {
                        byte[] rawData = await response.Content.ReadAsByteArrayAsync();
                        byte[] resizedData = await DisplayHelper.ResizeImageAsync(rawData, 128, 128);
                        if (revision == _mapPoolIconRevision && ReferenceEquals(pool, currentMapPool)
                            && string.Equals(iconUrl, MapPoolIcon, StringComparison.Ordinal))
                            pool.IconData = resizedData;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.ErrorPrint($"GetMapPoolIconData {iconUrl} Error: {ex.Message}");
            }
        }

        public PPPMapPoolShort FindPoolWithSyncURL(string syncUrl)
        {
            return calculatorInstance.FindPoolWithSyncURL(leaderboardName, syncUrl);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _isActive = false;
            _mapPoolIconRevision++;
            InvalidateRankRequests();
        }
    }
}
