using System;
using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Blocklisting
{
    [TestFixture]
    public class BlocklistServiceFixture : CoreTest<BlocklistService>
    {
        private DownloadFailedEvent _event;

        [SetUp]
        public void Setup()
        {
            _event = new DownloadFailedEvent
                     {
                         SeriesId = 12345,
                         EpisodeIds = new List<int> { 1 },
                         Quality = new QualityModel(Quality.Bluray720p),
                         SourceTitle = "series.title.s01e01",
                         DownloadClient = "SabnzbdClient",
                         DownloadId = "Sabnzbd_nzo_2dfh73k"
                     };

            _event.Data.Add("publishedDate", DateTime.UtcNow.ToString("s") + "Z");
            _event.Data.Add("size", "1000");
            _event.Data.Add("indexer", "nzbs.org");
            _event.Data.Add("protocol", "1");
            _event.Data.Add("message", "Marked as failed");
        }

        [Test]
        public void should_add_to_repository()
        {
            Subject.Handle(_event);

            Mocker.GetMock<IBlocklistRepository>()
                .Verify(v => v.Insert(It.Is<Blocklist>(b => b.EpisodeIds == _event.EpisodeIds)), Times.Once());
        }

        [Test]
        public void should_add_to_repository_missing_size_and_protocol()
        {
            Subject.Handle(_event);

            _event.Data.Remove("size");
            _event.Data.Remove("protocol");

            Mocker.GetMock<IBlocklistRepository>()
                .Verify(v => v.Insert(It.Is<Blocklist>(b => b.EpisodeIds == _event.EpisodeIds)), Times.Once());
        }

        [Test]
        public void should_not_match_release_from_another_indexer_with_near_published_date()
        {
            var publishedDate = DateTime.UtcNow;
            var release = GivenRelease("NZBFinder", publishedDate.AddSeconds(-10));
            GivenBlocklistedRelease("NZBgeek", publishedDate);

            Assert.That(Subject.Blocklisted(_event.SeriesId, release), Is.False);
        }

        [Test]
        public void should_not_match_release_from_another_indexer_with_identical_published_date()
        {
            var publishedDate = DateTime.UtcNow;
            var release = GivenRelease("NZBFinder", publishedDate);
            GivenBlocklistedRelease("NZBgeek", publishedDate);

            Assert.That(Subject.Blocklisted(_event.SeriesId, release), Is.False);
        }

        [Test]
        public void should_match_same_indexer_with_identical_published_date()
        {
            var publishedDate = DateTime.UtcNow;
            var release = GivenRelease("NZBgeek", publishedDate);
            GivenBlocklistedRelease("NZBgeek", publishedDate);

            Assert.That(Subject.Blocklisted(_event.SeriesId, release), Is.True);
        }

        [Test]
        public void should_not_match_same_indexer_when_published_date_differs()
        {
            var publishedDate = DateTime.UtcNow;
            var release = GivenRelease("NZBgeek", publishedDate.AddSeconds(-10));
            GivenBlocklistedRelease("NZBgeek", publishedDate);

            Assert.That(Subject.Blocklisted(_event.SeriesId, release), Is.False);
        }

        [Test]
        public void should_not_match_same_indexer_when_item_has_no_published_date()
        {
            var publishedDate = DateTime.UtcNow;
            var release = GivenRelease("NZBgeek", publishedDate);
            GivenBlocklistedRelease("NZBgeek", null);

            Assert.That(Subject.Blocklisted(_event.SeriesId, release), Is.False);
        }

        [Test]
        public void should_match_item_without_indexer_with_identical_published_date()
        {
            var publishedDate = DateTime.UtcNow;
            var release = GivenRelease("NZBFinder", publishedDate);
            GivenBlocklistedRelease(null, publishedDate);

            Assert.That(Subject.Blocklisted(_event.SeriesId, release), Is.True);
        }

        [Test]
        public void should_not_match_item_without_indexer_when_published_date_differs()
        {
            var publishedDate = DateTime.UtcNow;
            var release = GivenRelease("NZBFinder", publishedDate.AddSeconds(-10));
            GivenBlocklistedRelease(null, publishedDate);

            Assert.That(Subject.Blocklisted(_event.SeriesId, release), Is.False);
        }

        private ReleaseInfo GivenRelease(string indexer, DateTime publishedDate)
        {
            return new ReleaseInfo
            {
                Title = _event.SourceTitle,
                Indexer = indexer,
                DownloadProtocol = DownloadProtocol.Usenet,
                PublishDate = publishedDate,
                Size = 1000
            };
        }

        private void GivenBlocklistedRelease(string indexer, DateTime? publishedDate)
        {
            Mocker.GetMock<IBlocklistRepository>()
                .Setup(s => s.BlocklistedByTitle(_event.SeriesId, _event.SourceTitle))
                .Returns(new List<Blocklist>
                {
                    new ()
                    {
                        SeriesId = _event.SeriesId,
                        SourceTitle = _event.SourceTitle,
                        Indexer = indexer,
                        Protocol = DownloadProtocol.Usenet,
                        PublishedDate = publishedDate,
                        Size = 1000
                    }
                });
        }
    }
}
