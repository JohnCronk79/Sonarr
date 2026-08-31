using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider.Events;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.IndexerSearch
{
    internal static class AutomaticSearchApiCallTracker
    {
        private static readonly AsyncLocal<Scope> Current = new AsyncLocal<Scope>();

        public static Scope Begin()
        {
            return new Scope(Current.Value);
        }

        public static void RecordApiCall()
        {
            Current.Value?.RecordApiCall();
        }

        internal sealed class Scope : IDisposable
        {
            private readonly Scope _previous;
            private long _apiCalls;
            private bool _disposed;

            public Scope(Scope previous)
            {
                _previous = previous;
                Current.Value = this;
            }

            public long ApiCalls => Interlocked.Read(ref _apiCalls);

            public void RecordApiCall()
            {
                Interlocked.Increment(ref _apiCalls);
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                Current.Value = _previous;
                _disposed = true;
            }
        }
    }

    public interface IAutomaticSearchResultCache
    {
        Task<AutomaticSearchCacheResult> GetOrFetch(string key, IEnumerable<int> episodeIds, Func<Task<IList<ReleaseInfo>>> fetch, bool forceRefresh = false);
        string GetKey(IIndexer indexer, SearchCriteriaBase criteria);
        void RetainForEpisodes(IEnumerable<int> searchedEpisodeIds, IEnumerable<int> retainedEpisodeIds);
        void RecordSearchDuration(long elapsedMilliseconds);
        void Clear();
        AutomaticSearchCacheStatus GetStatus();
    }

    public class AutomaticSearchCacheStatus
    {
        public bool Enabled { get; set; }
        public int CacheSizeMb { get; set; }
        public long UsedBytes { get; set; }
        public int CachedSearches { get; set; }
        public int CachedReports { get; set; }
        public long CacheHits { get; set; }
        public long CacheMisses { get; set; }
        public long SearchesDropped { get; set; }
        public long ApiCalls { get; set; }
        public long ApiCallsSaved { get; set; }
        public long PeakUsedBytes { get; set; }
        public double HitRate { get; set; }
        public long CacheTimeSavedMilliseconds { get; set; }
        public long SearchTimeMilliseconds { get; set; }
    }

    public class AutomaticSearchCacheResult
    {
        public AutomaticSearchCacheResult(IList<ReleaseInfo> reports, bool cacheHit)
        {
            Reports = reports;
            CacheHit = cacheHit;
        }

        public IList<ReleaseInfo> Reports { get; }
        public bool CacheHit { get; }
    }

    public class AutomaticSearchResultCache : IAutomaticSearchResultCache,
                                              IHandle<ConfigSavedEvent>,
                                              IHandle<ProviderUpdatedEvent<IIndexer>>,
                                              IHandle<ProviderDeletedEvent<IIndexer>>,
                                              IHandle<EpisodeImportedEvent>
    {
        private static readonly TimeSpan InactivityLifetime = TimeSpan.FromHours(1);
        private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(5);

        private readonly object _sync = new object();
        private readonly Dictionary<string, CacheEntry> _entries = new Dictionary<string, CacheEntry>();
        private readonly HashSet<string> _loadedKeys = new HashSet<string>();
        private readonly ConcurrentDictionary<string, Lazy<Task<IList<ReleaseInfo>>>> _inFlight = new ConcurrentDictionary<string, Lazy<Task<IList<ReleaseInfo>>>>();
        private readonly IConfigService _configService;
        private readonly IEpisodeService _episodeService;
        private readonly Logger _logger;
        private readonly Timer _expirationTimer;
        private readonly Timer _reconciliationTimer;
        private int _reportCount;
        private long _usedBytes;
        private long _hits;
        private long _misses;
        private long _initialLoads;
        private long _refreshes;
        private long _searchesDropped;
        private long _apiCalls;
        private long _apiCallsSaved;
        private long _peakUsedBytes;
        private long _cacheTimeSavedMilliseconds;
        private long _searchTimeMilliseconds;
        private int _generation;
        private DateTime _lastActivity = DateTime.UtcNow;
        private bool _configuredEnabled;
        private int _configuredCacheSizeMb;

        public AutomaticSearchResultCache(IConfigService configService, IEpisodeService episodeService, Logger logger)
        {
            _configService = configService;
            _episodeService = episodeService;
            _logger = logger;
            _configuredEnabled = _configService.EnableAutomaticSearchResultCache;
            _configuredCacheSizeMb = _configService.AutomaticSearchCacheSize;
            _expirationTimer = new Timer(ExpireInactiveCache, null, InactivityLifetime, Timeout.InfiniteTimeSpan);
            _reconciliationTimer = new Timer(ReconcileCompletedEpisodes, null, ReconciliationInterval, ReconciliationInterval);
        }

        public async Task<AutomaticSearchCacheResult> GetOrFetch(string key, IEnumerable<int> episodeIds, Func<Task<IList<ReleaseInfo>>> fetch, bool forceRefresh = false)
        {
            var searchedEpisodeIds = new HashSet<int>(episodeIds ?? Array.Empty<int>());
            ExpireIfInactive();
            MarkActivity();

            if (!forceRefresh && TryGet(key, out var cachedReports, out var apiCallsSaved, out var fetchDurationMilliseconds))
            {
                TrackEpisodes(key, searchedEpisodeIds);
                var hits = Interlocked.Increment(ref _hits);
                Interlocked.Add(ref _apiCallsSaved, apiCallsSaved);
                Interlocked.Add(ref _cacheTimeSavedMilliseconds, fetchDurationMilliseconds);
                _logger.Debug("Automatic search cache hit for {0}: {1} reports (hits={2}, misses={3})", key, cachedReports.Count, hits, Interlocked.Read(ref _misses));
                return new AutomaticSearchCacheResult(cachedReports, true);
            }

            if (forceRefresh)
            {
                Interlocked.Increment(ref _refreshes);
                Remove(key);
            }

            var generation = Volatile.Read(ref _generation);
            var candidate = new Lazy<Task<IList<ReleaseInfo>>>(() => FetchAndStore(key, searchedEpisodeIds, fetch, generation), LazyThreadSafetyMode.ExecutionAndPublication);
            var pending = _inFlight.GetOrAdd(key, candidate);

            if (!forceRefresh && ReferenceEquals(candidate, pending))
            {
                bool previouslyLoaded;

                lock (_sync)
                {
                    previouslyLoaded = _loadedKeys.Contains(key);
                    _loadedKeys.Add(key);
                }

                if (previouslyLoaded)
                {
                    Interlocked.Increment(ref _misses);
                }
                else
                {
                    Interlocked.Increment(ref _initialLoads);
                }
            }

            try
            {
                var reports = await pending.Value;
                return new AutomaticSearchCacheResult(reports.ToList(), false);
            }
            finally
            {
                ((ICollection<KeyValuePair<string, Lazy<Task<IList<ReleaseInfo>>>>>)_inFlight)
                    .Remove(new KeyValuePair<string, Lazy<Task<IList<ReleaseInfo>>>>(key, pending));
            }
        }

        public string GetKey(IIndexer indexer, SearchCriteriaBase criteria)
        {
            var parts = new List<string>
            {
                indexer.Definition.Id.ToString(),
                criteria.GetType().Name,
                criteria.Series.Id.ToString(),
                criteria.Series.TvdbId.ToString(),
                criteria.SearchMode.ToString(),
                Join(criteria.SceneTitles),
                Join((criteria.Episodes ?? new List<NzbDrone.Core.Tv.Episode>()).Select(v => $"{v.Id}:{v.SeasonNumber}:{v.EpisodeNumber}:{v.SceneSeasonNumber}:{v.SceneEpisodeNumber}:{v.AbsoluteEpisodeNumber}:{v.SceneAbsoluteEpisodeNumber}:{v.AirDate}:{v.Title}"))
            };

            switch (criteria)
            {
                case SingleEpisodeSearchCriteria single:
                    parts.Add(single.SeasonNumber.ToString());
                    parts.Add(single.EpisodeNumber.ToString());
                    break;
                case SeasonSearchCriteria season:
                    parts.Add(season.SeasonNumber.ToString());
                    break;
                case DailyEpisodeSearchCriteria daily:
                    parts.Add(daily.AirDate.ToString("yyyy-MM-dd"));
                    break;
                case DailySeasonSearchCriteria dailySeason:
                    parts.Add(dailySeason.Year.ToString());
                    break;
                case AnimeEpisodeSearchCriteria anime:
                    parts.Add(anime.SeasonNumber.ToString());
                    parts.Add(anime.EpisodeNumber.ToString());
                    parts.Add(anime.AbsoluteEpisodeNumber.ToString());
                    parts.Add(anime.IsSeasonSearch.ToString());
                    break;
                case AnimeSeasonSearchCriteria animeSeason:
                    parts.Add(animeSeason.SeasonNumber.ToString());
                    break;
                case SpecialEpisodeSearchCriteria special:
                    parts.Add(Join(special.EpisodeQueryTitles));
                    break;
            }

            // UserInvokedSearch is deliberately omitted so a user-started automatic
            // search and a subsequent failed-download retry share the same reports.
            return string.Join("|", parts).SHA256Hash().Substring(0, 16);
        }

        public void Clear()
        {
            Clear(false);
        }

        public void RecordSearchDuration(long elapsedMilliseconds)
        {
            if (elapsedMilliseconds > 0)
            {
                Interlocked.Add(ref _searchTimeMilliseconds, elapsedMilliseconds);
            }
        }

        public void RetainForEpisodes(IEnumerable<int> searchedEpisodeIds, IEnumerable<int> retainedEpisodeIds)
        {
            var searched = new HashSet<int>(searchedEpisodeIds ?? Array.Empty<int>());
            var retained = new HashSet<int>(retainedEpisodeIds ?? Array.Empty<int>());

            if (searched.Count == 0)
            {
                return;
            }

            var removedEntries = 0;
            var removedBytes = 0L;

            lock (_sync)
            {
                foreach (var pair in _entries.ToList())
                {
                    var entry = pair.Value;

                    if (!entry.EpisodeIds.Overlaps(searched))
                    {
                        continue;
                    }

                    entry.EpisodeIds.RemoveWhere(id => searched.Contains(id) && !retained.Contains(id));

                    if (entry.EpisodeIds.Count == 0)
                    {
                        removedEntries++;
                        removedBytes += entry.EstimatedBytes;
                        RemoveEntry(pair.Key, entry, true);
                    }
                }
            }

            if (removedEntries > 0)
            {
                _logger.Debug("Automatic search cache retired {0} completed search entries ({1} bytes) after processing search outcomes", removedEntries, removedBytes);
            }
        }

        public void Handle(EpisodeImportedEvent message)
        {
            if (message?.EpisodeInfo?.Episodes == null)
            {
                return;
            }

            RetireEpisodes(message.EpisodeInfo.Episodes.Select(episode => episode.Id), "episode-imported");
        }

        public void Handle(ConfigSavedEvent message)
        {
            var enabled = _configService.EnableAutomaticSearchResultCache;
            var cacheSizeMb = _configService.AutomaticSearchCacheSize;

            if (enabled != _configuredEnabled || cacheSizeMb != _configuredCacheSizeMb)
            {
                _configuredEnabled = enabled;
                _configuredCacheSizeMb = cacheSizeMb;
                Clear(true);
                _logger.Info("Automatic search cache configuration changed; cache and statistics reset (enabled={0}, size={1} MB)", enabled, cacheSizeMb);
            }
        }

        public void Handle(ProviderUpdatedEvent<IIndexer> message)
        {
            Clear();
        }

        public void Handle(ProviderDeletedEvent<IIndexer> message)
        {
            Clear();
        }

        public AutomaticSearchCacheStatus GetStatus()
        {
            ExpireIfInactive();

            lock (_sync)
            {
                var hits = Interlocked.Read(ref _hits);
                var misses = Interlocked.Read(ref _misses);
                var requests = hits + misses;

                return new AutomaticSearchCacheStatus
                {
                    Enabled = _configService.EnableAutomaticSearchResultCache,
                    CacheSizeMb = _configService.AutomaticSearchCacheSize,
                    UsedBytes = _usedBytes,
                    CachedSearches = _entries.Count,
                    CachedReports = _reportCount,
                    CacheHits = hits,
                    CacheMisses = misses,
                    SearchesDropped = Interlocked.Read(ref _searchesDropped),
                    ApiCalls = Interlocked.Read(ref _apiCalls),
                    ApiCallsSaved = Interlocked.Read(ref _apiCallsSaved),
                    PeakUsedBytes = Interlocked.Read(ref _peakUsedBytes),
                    HitRate = requests == 0 ? 0 : Math.Round(hits * 100.0 / requests, 1),
                    CacheTimeSavedMilliseconds = Interlocked.Read(ref _cacheTimeSavedMilliseconds),
                    SearchTimeMilliseconds = Interlocked.Read(ref _searchTimeMilliseconds)
                };
            }
        }

        private static string Join(IEnumerable<string> values)
        {
            return string.Join(",", (values ?? Array.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim().ToLowerInvariant())
                .OrderBy(v => v, StringComparer.Ordinal));
        }

        private bool TryGet(string key, out IList<ReleaseInfo> reports, out long apiCallsSaved, out long fetchDurationMilliseconds)
        {
            lock (_sync)
            {
                if (!_entries.TryGetValue(key, out var entry))
                {
                    reports = null;
                    apiCallsSaved = 0;
                    fetchDurationMilliseconds = 0;
                    return false;
                }

                entry.LastAccessed = DateTime.UtcNow;
                reports = entry.Reports.ToList();
                apiCallsSaved = entry.ApiCalls;
                fetchDurationMilliseconds = entry.FetchDurationMilliseconds;
                return true;
            }
        }

        private async Task<IList<ReleaseInfo>> FetchAndStore(string key, HashSet<int> episodeIds, Func<Task<IList<ReleaseInfo>>> fetch, int generation)
        {
            IList<ReleaseInfo> fetchedReports;
            long apiCalls;
            var fetchStopwatch = Stopwatch.StartNew();

            using (var apiCallScope = AutomaticSearchApiCallTracker.Begin())
            {
                fetchedReports = await fetch();
                apiCalls = apiCallScope.ApiCalls;
            }

            fetchStopwatch.Stop();

            Interlocked.Add(ref _apiCalls, apiCalls);

            var reports = fetchedReports?.ToList() ?? new List<ReleaseInfo>();
            var estimatedBytes = EstimateSize(reports);

            lock (_sync)
            {
                if (generation != _generation)
                {
                    _logger.Debug("Automatic search cache discarded stale in-flight result {0} after invalidation", key);
                    return reports;
                }

                Remove(key);

                var capacityBytes = _configService.AutomaticSearchCacheSize * 1024L * 1024L;
                while (_usedBytes + estimatedBytes > capacityBytes && _entries.Count > 0)
                {
                    var oldest = _entries.OrderBy(v => v.Value.LastAccessed).First();
                    RemoveEntry(oldest.Key, oldest.Value, false);
                    Interlocked.Increment(ref _searchesDropped);
                }

                var now = DateTime.UtcNow;
                _entries[key] = new CacheEntry(reports, episodeIds, estimatedBytes, apiCalls, fetchStopwatch.ElapsedMilliseconds, now);
                _reportCount += reports.Count;
                _usedBytes += estimatedBytes;

                if (_usedBytes > _peakUsedBytes)
                {
                    Interlocked.Exchange(ref _peakUsedBytes, _usedBytes);
                }
            }

            _logger.Debug("Automatic search cache stored {0}: {1} reports, estimated {2} bytes (entries={3}, reports={4}, usedBytes={5}, refreshes={6}, searchesDropped={7})",
                key,
                reports.Count,
                estimatedBytes,
                GetEntryCount(),
                GetReportCount(),
                GetUsedBytes(),
                Interlocked.Read(ref _refreshes),
                Interlocked.Read(ref _searchesDropped));

            return reports;
        }

        private void Remove(string key)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    RemoveEntry(key, entry, false);
                }
            }
        }

        private void TrackEpisodes(string key, IEnumerable<int> episodeIds)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    entry.EpisodeIds.UnionWith(episodeIds);
                }
            }
        }

        private void RetireEpisodes(IEnumerable<int> episodeIds, string reason)
        {
            var retired = new HashSet<int>(episodeIds ?? Array.Empty<int>());

            if (retired.Count == 0)
            {
                return;
            }

            var removedEntries = 0;
            var removedBytes = 0L;

            lock (_sync)
            {
                foreach (var pair in _entries.ToList())
                {
                    var entry = pair.Value;
                    entry.EpisodeIds.ExceptWith(retired);

                    if (entry.EpisodeIds.Count == 0)
                    {
                        removedEntries++;
                        removedBytes += entry.EstimatedBytes;
                        RemoveEntry(pair.Key, entry, true);
                    }
                }
            }

            if (removedEntries > 0)
            {
                _logger.Debug("Automatic search cache retired {0} entries ({1} bytes) after {2}", removedEntries, removedBytes, reason);
            }
        }

        private void ReconcileCompletedEpisodes(object state)
        {
            ReconcileCompletedEpisodes();
        }

        internal void ReconcileCompletedEpisodes()
        {
            if (!_configService.EnableAutomaticSearchResultCache)
            {
                return;
            }

            int[] trackedEpisodeIds;

            lock (_sync)
            {
                trackedEpisodeIds = _entries.Values
                    .SelectMany(entry => entry.EpisodeIds)
                    .Distinct()
                    .ToArray();
            }

            if (trackedEpisodeIds.Length == 0)
            {
                return;
            }

            try
            {
                var completedEpisodeIds = _episodeService.GetEpisodes(trackedEpisodeIds)
                    .Where(episode => episode.HasFile)
                    .Select(episode => episode.Id)
                    .ToArray();

                RetireEpisodes(completedEpisodeIds, "database-reconciliation");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to reconcile completed episodes in the automatic search cache; the next five-minute pass will retry");
            }
        }

        private void MarkActivity()
        {
            lock (_sync)
            {
                _lastActivity = DateTime.UtcNow;
                _expirationTimer.Change(InactivityLifetime, Timeout.InfiniteTimeSpan);
            }
        }

        private void ExpireInactiveCache(object state)
        {
            ExpireIfInactive();
        }

        private void ExpireIfInactive()
        {
            lock (_sync)
            {
                var remaining = InactivityLifetime - (DateTime.UtcNow - _lastActivity);
                if (remaining > TimeSpan.Zero)
                {
                    _expirationTimer.Change(remaining, Timeout.InfiniteTimeSpan);
                    return;
                }

                if (_entries.Count > 0)
                {
                    Interlocked.Increment(ref _generation);
                    _inFlight.Clear();
                    _entries.Clear();
                    _loadedKeys.Clear();
                    _reportCount = 0;
                    _usedBytes = 0;
                    _logger.Debug("Automatic search result cache expired after one hour of inactivity");
                }
            }
        }

        private void RemoveEntry(string key, CacheEntry entry, bool forgetLoadedKey)
        {
            if (_entries.Remove(key))
            {
                _reportCount -= entry.Reports.Count;
                _usedBytes -= entry.EstimatedBytes;

                if (forgetLoadedKey)
                {
                    _loadedKeys.Remove(key);
                }
            }
        }

        private void Clear(bool resetStatistics)
        {
            Interlocked.Increment(ref _generation);
            _inFlight.Clear();

            lock (_sync)
            {
                _entries.Clear();
                _loadedKeys.Clear();
                _reportCount = 0;
                _usedBytes = 0;
                _lastActivity = DateTime.UtcNow;
                _expirationTimer.Change(InactivityLifetime, Timeout.InfiniteTimeSpan);
            }

            if (resetStatistics)
            {
                Interlocked.Exchange(ref _hits, 0);
                Interlocked.Exchange(ref _misses, 0);
                Interlocked.Exchange(ref _initialLoads, 0);
                Interlocked.Exchange(ref _refreshes, 0);
                Interlocked.Exchange(ref _searchesDropped, 0);
                Interlocked.Exchange(ref _apiCalls, 0);
                Interlocked.Exchange(ref _apiCallsSaved, 0);
                Interlocked.Exchange(ref _peakUsedBytes, 0);
                Interlocked.Exchange(ref _cacheTimeSavedMilliseconds, 0);
                Interlocked.Exchange(ref _searchTimeMilliseconds, 0);
            }

            _logger.Debug("Automatic search result cache cleared");
        }

        private static long EstimateSize(IEnumerable<ReleaseInfo> reports)
        {
            return reports.Sum(EstimateSize);
        }

        private static long EstimateSize(ReleaseInfo report)
        {
            // ReleaseInfo instances retain parsed metadata and derived-type fields.
            // A conservative 10 KB baseline keeps the RAM budget meaningful without
            // serializing every report or imposing a report-count ceiling.
            const int objectOverhead = 10 * 1024;
            var characters = (report.Guid?.Length ?? 0) +
                             (report.Title?.Length ?? 0) +
                             (report.DownloadUrl?.Length ?? 0) +
                             (report.InfoUrl?.Length ?? 0) +
                             (report.CommentUrl?.Length ?? 0) +
                             (report.Indexer?.Length ?? 0) +
                             (report.ImdbId?.Length ?? 0) +
                             (report.Origin?.Length ?? 0) +
                             (report.Source?.Length ?? 0) +
                             (report.Container?.Length ?? 0) +
                             (report.Codec?.Length ?? 0) +
                             (report.Resolution?.Length ?? 0);

            return objectOverhead + (characters * sizeof(char)) + ((report.Languages?.Count ?? 0) * 64L);
        }

        private int GetEntryCount()
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }

        private int GetReportCount()
        {
            lock (_sync)
            {
                return _reportCount;
            }
        }

        private long GetUsedBytes()
        {
            lock (_sync)
            {
                return _usedBytes;
            }
        }

        private class CacheEntry
        {
            public CacheEntry(List<ReleaseInfo> reports, IEnumerable<int> episodeIds, long estimatedBytes, long apiCalls, long fetchDurationMilliseconds, DateTime lastAccessed)
            {
                Reports = reports;
                EpisodeIds = new HashSet<int>(episodeIds ?? Array.Empty<int>());
                EstimatedBytes = estimatedBytes;
                ApiCalls = apiCalls;
                FetchDurationMilliseconds = fetchDurationMilliseconds;
                LastAccessed = lastAccessed;
            }

            public List<ReleaseInfo> Reports { get; }
            public HashSet<int> EpisodeIds { get; }
            public long EstimatedBytes { get; }
            public long ApiCalls { get; }
            public long FetchDurationMilliseconds { get; }
            public DateTime LastAccessed { get; set; }
        }
    }
}
