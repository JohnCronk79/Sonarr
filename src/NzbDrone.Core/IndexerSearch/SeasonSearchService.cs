using System.Linq;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.IndexerSearch
{
    public class SeasonSearchService : IExecute<SeasonSearchCommand>
    {
        private readonly ISearchForReleases _releaseSearchService;
        private readonly IProcessDownloadDecisions _processDownloadDecisions;
        private readonly IAutomaticSearchResultCache _automaticSearchResultCache;
        private readonly IEpisodeService _episodeService;
        private readonly Logger _logger;

        public SeasonSearchService(ISearchForReleases releaseSearchService,
                                   IProcessDownloadDecisions processDownloadDecisions,
                                   IAutomaticSearchResultCache automaticSearchResultCache,
                                   IEpisodeService episodeService,
                                   Logger logger)
        {
            _releaseSearchService = releaseSearchService;
            _processDownloadDecisions = processDownloadDecisions;
            _automaticSearchResultCache = automaticSearchResultCache;
            _episodeService = episodeService;
            _logger = logger;
        }

        public void Execute(SeasonSearchCommand message)
        {
            var searchedEpisodeIds = (_episodeService.GetEpisodesBySeason(message.SeriesId, message.SeasonNumber) ?? Enumerable.Empty<Episode>())
                .Select(episode => episode.Id)
                .ToArray();
            var decisions = _releaseSearchService.SeasonSearch(message.SeriesId, message.SeasonNumber, false, true, message.Trigger == CommandTrigger.Manual, false).GetAwaiter().GetResult();
            var processed = _processDownloadDecisions.ProcessDecisions(decisions).GetAwaiter().GetResult();
            var retainedEpisodeIds = processed.Grabbed
                .Concat(processed.Pending)
                .SelectMany(decision => decision.RemoteEpisode.Episodes)
                .Select(episode => episode.Id)
                .Distinct()
                .ToArray();

            _automaticSearchResultCache.RetainForEpisodes(searchedEpisodeIds, retainedEpisodeIds);

            _logger.ProgressInfo("Season search completed. {0} reports downloaded.", processed.Grabbed.Count);
        }
    }
}
