using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.TrackedDownloads;

namespace NzbDrone.Core.Test.Download.TrackedDownloads
{
    [TestFixture]
    public class DownloadClientPollingMetricsFixture
    {
        [Test]
        public void should_estimate_wait_saved_against_the_default_interval()
        {
            var configService = new Mock<IConfigService>();
            configService.SetupGet(v => v.EnableCustomDownloadClientPollingInterval).Returns(true);
            configService.SetupGet(v => v.DownloadClientPollingInterval).Returns(10);
            var subject = new DownloadClientPollingMetrics(configService.Object);

            subject.RecordTerminalStateDetected();

            var status = subject.GetStatus();
            status.DetectedStateChanges.Should().Be(1);
            status.EstimatedWaitMilliseconds.Should().Be(5000);
            status.EstimatedWaitSavedMilliseconds.Should().Be(25000);
        }

        [Test]
        public void should_report_no_savings_at_the_default_interval()
        {
            var configService = new Mock<IConfigService>();
            configService.SetupGet(v => v.EnableCustomDownloadClientPollingInterval).Returns(false);
            var subject = new DownloadClientPollingMetrics(configService.Object);

            subject.RecordTerminalStateDetected();

            var status = subject.GetStatus();
            status.EstimatedWaitMilliseconds.Should().Be(30000);
            status.EstimatedWaitSavedMilliseconds.Should().Be(0);
        }

        [Test]
        public void should_reset_cumulative_polling_statistics()
        {
            var configService = new Mock<IConfigService>();
            configService.SetupGet(v => v.EnableCustomDownloadClientPollingInterval).Returns(true);
            configService.SetupGet(v => v.DownloadClientPollingInterval).Returns(10);
            var subject = new DownloadClientPollingMetrics(configService.Object);

            subject.RecordTerminalStateDetected();
            subject.ResetStatistics();

            var status = subject.GetStatus();
            status.DetectedStateChanges.Should().Be(0);
            status.EstimatedWaitMilliseconds.Should().Be(0);
            status.EstimatedWaitSavedMilliseconds.Should().Be(0);
        }
    }
}
