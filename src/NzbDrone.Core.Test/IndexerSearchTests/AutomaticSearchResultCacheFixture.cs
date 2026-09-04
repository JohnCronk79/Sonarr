using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class AutomaticSearchResultCacheFixture
    {
        private AutomaticSearchResultCache _subject;
        private Mock<IConfigService> _configService;
        private Mock<IEpisodeService> _episodeService;
        private bool _cacheEnabled;
        private int _cacheSizeMb;

        [SetUp]
        public void SetUp()
        {
            _cacheEnabled = true;
            _cacheSizeMb = 256;
            _configService = new Mock<IConfigService>();
            _episodeService = new Mock<IEpisodeService>();
            _configService.SetupGet(v => v.EnableAutomaticSearchResultCache).Returns(() => _cacheEnabled);
            _configService.SetupGet(v => v.AutomaticSearchCacheSize).Returns(() => _cacheSizeMb);
            _subject = new AutomaticSearchResultCache(_configService.Object, _episodeService.Object, LogManager.GetCurrentClassLogger());
        }

        [Test]
        public async Task should_reuse_cached_reports()
        {
            var fetchCount = 0;
            var reports = new List<ReleaseInfo> { new ReleaseInfo { Guid = "one" } };

            async Task<IList<ReleaseInfo>> Fetch()
            {
                fetchCount++;
                await Task.CompletedTask;
                return reports;
            }

            var first = await _subject.GetOrFetch("key", new[] { 1 }, Fetch);
            var second = await _subject.GetOrFetch("key", new[] { 1 }, Fetch);

            first.CacheHit.Should().BeFalse();
            second.CacheHit.Should().BeTrue();
            second.Reports.Should().ContainSingle(v => v.Guid == "one");
            fetchCount.Should().Be(1);
            _subject.GetStatus().CacheHits.Should().Be(1);
            _subject.GetStatus().CacheMisses.Should().Be(0);
        }

        [Test]
        public async Task should_measure_time_saved_by_a_cache_hit()
        {
            async Task<IList<ReleaseInfo>> Fetch()
            {
                await Task.Delay(20);
                return new List<ReleaseInfo> { new ReleaseInfo { Guid = "one" } };
            }

            await _subject.GetOrFetch("key", new[] { 1 }, Fetch);
            await _subject.GetOrFetch("key", new[] { 1 }, Fetch);

            _subject.GetStatus().CacheTimeSavedMilliseconds.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task should_count_a_previously_loaded_search_evicted_for_capacity_as_a_miss()
        {
            var configService = new Mock<IConfigService>();
            var episodeService = new Mock<IEpisodeService>();
            configService.SetupGet(v => v.EnableAutomaticSearchResultCache).Returns(true);
            configService.SetupGet(v => v.AutomaticSearchCacheSize).Returns(1);
            var subject = new AutomaticSearchResultCache(configService.Object, episodeService.Object, LogManager.GetCurrentClassLogger());

            IList<ReleaseInfo> Reports(string prefix)
            {
                var reports = new List<ReleaseInfo>();

                for (var i = 0; i < 80; i++)
                {
                    reports.Add(new ReleaseInfo { Guid = $"{prefix}-{i}" });
                }

                return reports;
            }

            await subject.GetOrFetch("first", new[] { 1 }, () => Task.FromResult(Reports("first")));
            await subject.GetOrFetch("second", new[] { 2 }, () => Task.FromResult(Reports("second")));
            var reloaded = await subject.GetOrFetch("first", new[] { 1 }, () => Task.FromResult(Reports("first-reloaded")));

            reloaded.CacheHit.Should().BeFalse();
            subject.GetStatus().CacheMisses.Should().Be(1);
        }

        [Test]
        public async Task should_cache_result_sets_larger_than_the_former_per_entry_limit()
        {
            var fetchCount = 0;
            var reports = new List<ReleaseInfo>();

            for (var i = 0; i < 6001; i++)
            {
                reports.Add(new ReleaseInfo { Guid = i.ToString() });
            }

            Task<IList<ReleaseInfo>> Fetch()
            {
                fetchCount++;
                return Task.FromResult<IList<ReleaseInfo>>(reports);
            }

            await _subject.GetOrFetch("large", new[] { 1 }, Fetch);
            var cached = await _subject.GetOrFetch("large", new[] { 1 }, Fetch);

            cached.CacheHit.Should().BeTrue();
            cached.Reports.Should().HaveCount(6001);
            fetchCount.Should().Be(1);
        }

        [Test]
        public async Task should_force_one_fresh_fetch()
        {
            var fetchCount = 0;

            Task<IList<ReleaseInfo>> Fetch()
            {
                fetchCount++;
                return Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { new ReleaseInfo { Guid = fetchCount.ToString() } });
            }

            await _subject.GetOrFetch("key", new[] { 1 }, Fetch);
            var refreshed = await _subject.GetOrFetch("key", new[] { 1 }, Fetch, true);
            var cached = await _subject.GetOrFetch("key", new[] { 1 }, Fetch);

            refreshed.CacheHit.Should().BeFalse();
            refreshed.Reports.Should().ContainSingle(v => v.Guid == "2");
            cached.CacheHit.Should().BeTrue();
            cached.Reports.Should().ContainSingle(v => v.Guid == "2");
            fetchCount.Should().Be(2);
        }

        [Test]
        public async Task should_collapse_concurrent_fetches_for_the_same_key()
        {
            var fetchCount = 0;
            var fetchStarted = new TaskCompletionSource<bool>();
            var releaseFetch = new TaskCompletionSource<bool>();

            async Task<IList<ReleaseInfo>> Fetch()
            {
                fetchCount++;
                fetchStarted.TrySetResult(true);
                await releaseFetch.Task;
                return new List<ReleaseInfo>();
            }

            var first = _subject.GetOrFetch("key", new[] { 1 }, Fetch);
            await fetchStarted.Task;
            var second = _subject.GetOrFetch("key", new[] { 1 }, Fetch);

            releaseFetch.SetResult(true);
            await Task.WhenAll(first, second);

            fetchCount.Should().Be(1);
        }

        [Test]
        public async Task clear_should_prevent_an_in_flight_result_from_repopulating_the_cache()
        {
            var fetchCount = 0;
            var fetchStarted = new TaskCompletionSource<bool>();
            var releaseFetch = new TaskCompletionSource<bool>();

            async Task<IList<ReleaseInfo>> Fetch()
            {
                fetchCount++;
                fetchStarted.TrySetResult(true);
                await releaseFetch.Task;
                return new List<ReleaseInfo>();
            }

            var first = _subject.GetOrFetch("key", new[] { 1 }, Fetch);
            await fetchStarted.Task;
            _subject.Clear();
            var second = _subject.GetOrFetch("key", new[] { 1 }, Fetch);
            releaseFetch.SetResult(true);
            await Task.WhenAll(first, second);
            await _subject.GetOrFetch("key", new[] { 1 }, Fetch);

            fetchCount.Should().Be(2);
        }

        [Test]
        public async Task should_retire_a_search_after_all_tracked_episodes_are_imported()
        {
            var fetchCount = 0;

            Task<IList<ReleaseInfo>> Fetch()
            {
                fetchCount++;
                return Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { new ReleaseInfo { Guid = fetchCount.ToString() } });
            }

            await _subject.GetOrFetch("season", new[] { 1, 2 }, Fetch);
            _subject.RetainForEpisodes(new[] { 1, 2 }, new[] { 2 });

            var beforeImport = await _subject.GetOrFetch("season", new[] { 2 }, Fetch);
            beforeImport.CacheHit.Should().BeTrue();

            var imported = new LocalEpisode
            {
                Episodes = new List<Episode> { new Episode { Id = 2 } }
            };

            _subject.Handle(new EpisodeImportedEvent(imported, new EpisodeFile(), new List<DeletedEpisodeFile>(), true, null));

            _subject.GetStatus().CachedSearches.Should().Be(0);
            _subject.GetStatus().UsedBytes.Should().Be(0);

            var afterImport = await _subject.GetOrFetch("season", new[] { 2 }, Fetch);
            afterImport.CacheHit.Should().BeFalse();
            _subject.GetStatus().CacheMisses.Should().Be(0);
            fetchCount.Should().Be(2);
        }

        [Test]
        public async Task should_retire_searches_that_produced_no_grab_or_pending_release()
        {
            var fetchCount = 0;

            Task<IList<ReleaseInfo>> Fetch()
            {
                fetchCount++;
                return Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>());
            }

            await _subject.GetOrFetch("no-result", new[] { 3 }, Fetch);
            _subject.RetainForEpisodes(new[] { 3 }, System.Array.Empty<int>());

            _subject.GetStatus().CachedSearches.Should().Be(0);

            var reloaded = await _subject.GetOrFetch("no-result", new[] { 3 }, Fetch);
            reloaded.CacheHit.Should().BeFalse();
            _subject.GetStatus().CacheMisses.Should().Be(0);
            fetchCount.Should().Be(2);
        }

        [Test]
        public async Task reconciliation_should_retire_entries_for_episodes_that_already_have_files()
        {
            _episodeService
                .Setup(service => service.GetEpisodes(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Episode> { new Episode { Id = 4, EpisodeFileId = 10 } });

            await _subject.GetOrFetch("reconcile", new[] { 4 }, () =>
                Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { new ReleaseInfo { Guid = "one" } }));

            _subject.ReconcileCompletedEpisodes();

            _subject.GetStatus().CachedSearches.Should().Be(0);
            _subject.GetStatus().UsedBytes.Should().Be(0);
            _subject.GetStatus().CacheMisses.Should().Be(0);
        }

        [Test]
        public void cache_key_should_ignore_user_invoked_search()
        {
            var indexer = new Mock<IIndexer>();
            indexer.SetupGet(v => v.Definition).Returns(new IndexerDefinition { Id = 12 });

            var automatic = new SingleEpisodeSearchCriteria
            {
                Series = new Series { Id = 2, TvdbId = 3 },
                SceneTitles = new List<string> { "Example" },
                SeasonNumber = 1,
                EpisodeNumber = 4,
                UserInvokedSearch = false
            };

            var userStarted = new SingleEpisodeSearchCriteria
            {
                Series = automatic.Series,
                SceneTitles = automatic.SceneTitles,
                SeasonNumber = automatic.SeasonNumber,
                EpisodeNumber = automatic.EpisodeNumber,
                UserInvokedSearch = true
            };

            _subject.GetKey(indexer.Object, automatic).Should().Be(_subject.GetKey(indexer.Object, userStarted));
        }

        [Test]
        public void cache_key_should_distinguish_different_queries()
        {
            var indexer = new Mock<IIndexer>();
            indexer.SetupGet(v => v.Definition).Returns(new IndexerDefinition { Id = 12 });

            var first = new SingleEpisodeSearchCriteria
            {
                Series = new Series { Id = 2, TvdbId = 3 },
                SceneTitles = new List<string> { "Example" },
                SeasonNumber = 1,
                EpisodeNumber = 4
            };

            var second = new SingleEpisodeSearchCriteria
            {
                Series = first.Series,
                SceneTitles = first.SceneTitles,
                SeasonNumber = first.SeasonNumber,
                EpisodeNumber = 5
            };

            _subject.GetKey(indexer.Object, first).Should().NotBe(_subject.GetKey(indexer.Object, second));
        }

        [Test]
        public async Task should_retain_cumulative_statistics_when_cache_entries_are_cleared()
        {
            await _subject.GetOrFetch("series", new[] { 5 }, () =>
                Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { new ReleaseInfo { Guid = "one" } }));
            await _subject.GetOrFetch("series", new[] { 5 }, () =>
                Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>()));

            var before = _subject.GetStatus();
            _subject.Clear();
            var after = _subject.GetStatus();

            before.CacheHits.Should().Be(1);
            before.CachedReports.Should().Be(1);
            before.PeakUsedBytes.Should().BeGreaterThan(0);
            after.CachedSearches.Should().Be(0);
            after.UsedBytes.Should().Be(0);
            after.CacheHits.Should().Be(before.CacheHits);
            after.PeakUsedBytes.Should().Be(before.PeakUsedBytes);
        }

        [Test]
        public async Task should_reset_statistics_without_clearing_cached_reports()
        {
            await _subject.GetOrFetch("series", new[] { 6 }, () =>
                Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { new ReleaseInfo { Guid = "one" } }));
            await _subject.GetOrFetch("series", new[] { 6 }, () =>
                Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>()));
            _subject.RecordSearchDuration(500);

            var usedBytes = _subject.GetStatus().UsedBytes;
            _subject.ResetStatistics();
            var status = _subject.GetStatus();

            status.CachedSearches.Should().Be(1);
            status.CachedReports.Should().Be(1);
            status.UsedBytes.Should().Be(usedBytes);
            status.PeakUsedBytes.Should().Be(usedBytes);
            status.CacheHits.Should().Be(0);
            status.CacheMisses.Should().Be(0);
            status.ApiCalls.Should().Be(0);
            status.ApiCallsSaved.Should().Be(0);
            status.CacheTimeSavedMilliseconds.Should().Be(0);
            status.SearchTimeMilliseconds.Should().Be(0);
        }

        [Test]
        public async Task should_retain_cumulative_statistics_when_cache_configuration_changes()
        {
            await _subject.GetOrFetch("series", new[] { 7 }, () =>
                Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { new ReleaseInfo { Guid = "one" } }));
            await _subject.GetOrFetch("series", new[] { 7 }, () =>
                Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>()));

            var before = _subject.GetStatus();
            _cacheSizeMb = 512;
            _subject.Handle(new ConfigSavedEvent());
            var after = _subject.GetStatus();

            after.CacheSizeMb.Should().Be(512);
            after.CachedSearches.Should().Be(0);
            after.CacheHits.Should().Be(before.CacheHits);
            after.PeakUsedBytes.Should().Be(before.PeakUsedBytes);
        }
    }
}
