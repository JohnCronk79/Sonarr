using System;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Update;

namespace Sonarr.Api.V3.System
{
    public class SystemResource
    {
        public string AppName { get; set; }
        public string InstanceName { get; set; }
        public string Version { get; set; }
        public DateTime BuildTime { get; set; }
        public bool IsDebug { get; set; }
        public bool IsProduction { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsUserInteractive { get; set; }
        public string StartupPath { get; set; }
        public string AppData { get; set; }
        public string OsName { get; set; }
        public string OsVersion { get; set; }
        public bool IsNetCore { get; set; }
        public bool IsLinux { get; set; }
        public bool IsOsx { get; set; }
        public bool IsWindows { get; set; }
        public bool IsDocker { get; set; }
        public RuntimeMode Mode { get; set; }
        public string Branch { get; set; }
        public AuthenticationType Authentication { get; set; }
        public Version SqliteVersion { get; set; }
        public int MigrationVersion { get; set; }
        public string UrlBase { get; set; }
        public Version RuntimeVersion { get; set; }
        public string RuntimeName { get; set; }
        public DateTime StartTime { get; set; }
        public string PackageVersion { get; set; }
        public string PackageAuthor { get; set; }
        public UpdateMechanism PackageUpdateMechanism { get; set; }
        public string PackageUpdateMechanismMessage { get; set; }
        public Version DatabaseVersion { get; set; }
        public DatabaseType DatabaseType { get; set; }
        public bool AutomaticSearchCacheEnabled { get; set; }
        public int AutomaticSearchCacheSizeMb { get; set; }
        public long AutomaticSearchCacheUsedBytes { get; set; }
        public int AutomaticSearchCacheSearches { get; set; }
        public int AutomaticSearchCacheReports { get; set; }
        public long AutomaticSearchCacheHits { get; set; }
        public long AutomaticSearchCacheMisses { get; set; }
        public long AutomaticSearchCacheSearchesDropped { get; set; }
        public long AutomaticSearchCacheApiCalls { get; set; }
        public long AutomaticSearchCacheApiCallsSaved { get; set; }
        public long AutomaticSearchCachePeakUsedBytes { get; set; }
        public double AutomaticSearchCacheHitRate { get; set; }
        public long AutomaticSearchCacheTimeSavedMilliseconds { get; set; }
        public long AutomaticSearchPollingWaitSavedMilliseconds { get; set; }
        public long AutomaticSearchObservedTimeMilliseconds { get; set; }
    }
}
