using System;
using System.Threading;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public interface IDownloadClientPollingMetrics
    {
        void RecordTerminalStateDetected();
        void ResetStatistics();
        DownloadClientPollingStatus GetStatus();
    }

    public class DownloadClientPollingStatus
    {
        public long DetectedStateChanges { get; set; }
        public long EstimatedWaitMilliseconds { get; set; }
        public long EstimatedWaitSavedMilliseconds { get; set; }
    }

    public class DownloadClientPollingMetrics : IDownloadClientPollingMetrics
    {
        private const int DefaultPollingIntervalSeconds = 60;

        private readonly IConfigService _configService;
        private long _detectedStateChanges;
        private long _estimatedWaitMilliseconds;
        private long _estimatedWaitSavedMilliseconds;

        public DownloadClientPollingMetrics(IConfigService configService)
        {
            _configService = configService;
        }

        public void RecordTerminalStateDetected()
        {
            var pollingIntervalSeconds = _configService.EnableCustomDownloadClientPollingInterval ?
                _configService.DownloadClientPollingInterval :
                DefaultPollingIntervalSeconds;

            // A completion or failure can occur anywhere between two polls. Half
            // of the configured interval is therefore the expected detection wait.
            var expectedWaitMilliseconds = pollingIntervalSeconds * 500L;
            var expectedDefaultWaitMilliseconds = DefaultPollingIntervalSeconds * 500L;

            Interlocked.Increment(ref _detectedStateChanges);
            Interlocked.Add(ref _estimatedWaitMilliseconds, expectedWaitMilliseconds);
            Interlocked.Add(ref _estimatedWaitSavedMilliseconds, Math.Max(0, expectedDefaultWaitMilliseconds - expectedWaitMilliseconds));
        }

        public DownloadClientPollingStatus GetStatus()
        {
            return new DownloadClientPollingStatus
            {
                DetectedStateChanges = Interlocked.Read(ref _detectedStateChanges),
                EstimatedWaitMilliseconds = Interlocked.Read(ref _estimatedWaitMilliseconds),
                EstimatedWaitSavedMilliseconds = Interlocked.Read(ref _estimatedWaitSavedMilliseconds)
            };
        }

        public void ResetStatistics()
        {
            Interlocked.Exchange(ref _detectedStateChanges, 0);
            Interlocked.Exchange(ref _estimatedWaitMilliseconds, 0);
            Interlocked.Exchange(ref _estimatedWaitSavedMilliseconds, 0);
        }
    }
}
