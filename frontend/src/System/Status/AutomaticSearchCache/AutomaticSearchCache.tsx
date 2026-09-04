import classNames from 'classnames';
import React, { useCallback, useEffect, useState } from 'react';
import { useDispatch, useSelector } from 'react-redux';
import AppState from 'App/State/AppState';
import FieldSet from 'Components/FieldSet';
import Icon from 'Components/Icon';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ProgressBar from 'Components/ProgressBar';
import Tooltip from 'Components/Tooltip/Tooltip';
import { icons, kinds, sizes } from 'Helpers/Props';
import { fetchStatus } from 'Store/Actions/systemActions';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import styles from './AutomaticSearchCache.css';

interface MetricProps {
  className: string;
  label: string;
  tooltip: React.ReactNode;
  value: string;
  warning?: boolean;
}

interface StatusBarProps {
  containerClassName?: string;
  progress: number;
  tooltip: React.ReactNode;
}

function formatMb(bytes: number) {
  return `${Math.round(bytes / 1024 / 1024).toLocaleString()} MB`;
}

function formatDuration(milliseconds: number) {
  const totalSeconds = Math.max(0, Math.round(milliseconds / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  if (hours > 0) {
    return `${hours}h ${minutes}m ${seconds}s`;
  }

  if (minutes > 0) {
    return `${minutes}m ${seconds}s`;
  }

  return `${seconds}s`;
}

function getPercentage(value: number, total: number) {
  return total > 0 ? Math.min(100, Math.max(0, (value / total) * 100)) : 0;
}

function Metric({
  className,
  label,
  tooltip,
  value,
  warning = false,
}: MetricProps) {
  return (
    <div className={className}>
      <Tooltip
        className={styles.labelTooltip}
        bodyClassName={styles.tooltipBody}
        kind={kinds.INVERSE}
        showArrow={false}
        anchor={<span className={styles.label}>{label}</span>}
        tooltip={tooltip}
      />
      <span className={styles.metricSpacer} aria-hidden="true" />
      <span
        className={classNames(
          styles.value,
          warning ? styles.warningValue : undefined
        )}
      >
        {value}
        {warning ? <Icon name={icons.DANGER} kind={kinds.WARNING} /> : null}
      </span>
    </div>
  );
}

function StatusBar({ containerClassName, progress, tooltip }: StatusBarProps) {
  return (
    <div className={styles.barCell}>
      <Tooltip
        className={styles.barTooltip}
        bodyClassName={styles.tooltipBody}
        kind={kinds.INVERSE}
        showArrow={false}
        anchor={
          <ProgressBar
            containerClassName={containerClassName}
            progress={progress}
            kind={kinds.PRIMARY}
            size={sizes.MEDIUM}
          />
        }
        tooltip={tooltip}
      />
    </div>
  );
}

function AutomaticSearchCache() {
  const dispatch = useDispatch();
  const [isResettingStatistics, setIsResettingStatistics] = useState(false);
  const { isFetching, isPopulated, item } = useSelector(
    (state: AppState) => state.system.status
  );

  const onResetStatisticsPress = useCallback(() => {
    setIsResettingStatistics(true);

    createAjaxRequest({
      url: '/system/status/automaticsearchcache/resetstatistics',
      method: 'POST',
    }).request.always(() => {
      setIsResettingStatistics(false);
      dispatch(fetchStatus());
    });
  }, [dispatch]);

  const legend = (
    <span className={styles.heading}>
      <span>{translate('AutomaticSearchCache')}</span>
      <SpinnerButton
        isSpinning={isResettingStatistics}
        size={sizes.SMALL}
        onPress={onResetStatisticsPress}
      >
        {translate('ResetStatistics')}
      </SpinnerButton>
    </span>
  );

  useEffect(() => {
    dispatch(fetchStatus());

    const interval = window.setInterval(() => {
      dispatch(fetchStatus());
    }, 1000);

    return () => window.clearInterval(interval);
  }, [dispatch]);

  if (isFetching && !isPopulated) {
    return (
      <FieldSet legend={legend}>
        <LoadingIndicator />
      </FieldSet>
    );
  }

  const cacheSizeMb = item.automaticSearchCacheSizeMb ?? 256;
  const usedBytes = item.automaticSearchCacheUsedBytes ?? 0;
  const peakUsedBytes = item.automaticSearchCachePeakUsedBytes ?? 0;
  const capacityBytes = cacheSizeMb * 1024 * 1024;
  const utilization = getPercentage(usedBytes, capacityBytes);
  const peakUtilization = getPercentage(peakUsedBytes, capacityBytes);
  const cacheHits = item.automaticSearchCacheHits ?? 0;
  const cacheMisses = item.automaticSearchCacheMisses ?? 0;
  const cacheLookups = cacheHits + cacheMisses;
  const cacheHitPercentage = getPercentage(cacheHits, cacheLookups);
  const externalApiCalls = item.automaticSearchCacheApiCalls ?? 0;
  const apiCallsSaved = item.automaticSearchCacheApiCallsSaved ?? 0;
  const totalApiCalls = externalApiCalls + apiCallsSaved;
  const apiCallsSavedPercentage = getPercentage(apiCallsSaved, totalApiCalls);
  const cacheTimeSaved = item.automaticSearchCacheTimeSavedMilliseconds ?? 0;
  const pollingWaitSaved =
    item.automaticSearchPollingWaitSavedMilliseconds ?? 0;
  const totalTimeSaved = cacheTimeSaved + pollingWaitSaved;
  const observedTime = item.automaticSearchObservedTimeMilliseconds ?? 0;
  const baselineTime = observedTime + totalTimeSaved;
  const timeSavedPercentage = getPercentage(totalTimeSaved, baselineTime);

  return (
    <FieldSet legend={legend}>
      <div className={styles.metricRow}>
        <Metric
          className={styles.metricLeft}
          label={translate('CacheSize')}
          value={`${cacheSizeMb.toLocaleString()} MB`}
          tooltip={translate('AutomaticSearchCacheSizeTooltip')}
        />
        <Metric
          className={styles.metricMiddle}
          label={translate('CacheUsed')}
          value={formatMb(usedBytes)}
          tooltip={translate('AutomaticSearchCacheUsedTooltip')}
        />
        <Metric
          className={styles.metricRight}
          label={translate('Utilization')}
          value={`${utilization.toFixed(1)}%`}
          tooltip={translate('AutomaticSearchCacheUtilizationTooltip')}
        />
        <StatusBar
          progress={utilization}
          tooltip={translate('AutomaticSearchCacheCapacitySummaryTooltip', {
            cacheSize: `${cacheSizeMb.toLocaleString()} MB`,
            peak: formatMb(peakUsedBytes),
            peakUtilization: peakUtilization.toFixed(1),
            used: formatMb(usedBytes),
            utilization: utilization.toFixed(1),
          })}
        />
      </div>

      <div className={styles.metricRow}>
        <Metric
          className={styles.metricLeft}
          label={translate('PeakCacheUsed')}
          value={formatMb(peakUsedBytes)}
          tooltip={translate('AutomaticSearchCachePeakUsedTooltip')}
        />
        <Metric
          className={styles.metricMiddle}
          label={translate('CachedReports')}
          value={(item.automaticSearchCacheReports ?? 0).toLocaleString()}
          tooltip={translate('AutomaticSearchCacheReportsTooltip')}
        />
        <Metric
          className={styles.metricRight}
          label={translate('SearchesInCache')}
          value={(item.automaticSearchCacheSearches ?? 0).toLocaleString()}
          tooltip={translate('AutomaticSearchCachedSearchesTooltip')}
        />
        <span className={styles.barCell} />
      </div>

      <div className={styles.metricRow}>
        <Metric
          className={styles.metricLeft}
          label={translate('CacheLookups')}
          value={cacheLookups.toLocaleString()}
          tooltip={translate('AutomaticSearchCacheLookupsTooltip')}
        />
        <Metric
          className={styles.metricMiddle}
          label={translate('CacheHits')}
          value={cacheHits.toLocaleString()}
          tooltip={translate('AutomaticSearchCacheHitsTooltip')}
        />
        <Metric
          className={styles.metricRight}
          label={translate('CacheMisses')}
          value={cacheMisses.toLocaleString()}
          warning={cacheMisses > 0}
          tooltip={translate('AutomaticSearchCacheMissesTooltip')}
        />
        <StatusBar
          containerClassName={cacheMisses > 0 ? styles.warningBar : undefined}
          progress={cacheHitPercentage}
          tooltip={translate('AutomaticSearchCacheLookupBarTooltip', {
            hits: cacheHits.toLocaleString(),
            misses: cacheMisses.toLocaleString(),
            percentage: cacheHitPercentage.toFixed(1),
          })}
        />
      </div>

      <div className={styles.metricRow}>
        <Metric
          className={styles.metricLeft}
          label={translate('TotalApiCalls')}
          value={totalApiCalls.toLocaleString()}
          tooltip={translate('AutomaticSearchCacheTotalApiCallsTooltip')}
        />
        <Metric
          className={styles.metricMiddle}
          label={translate('ExternalApiCalls')}
          value={externalApiCalls.toLocaleString()}
          tooltip={translate('AutomaticSearchCacheApiCallsTooltip')}
        />
        <Metric
          className={styles.metricRight}
          label={translate('ApiCallsSaved')}
          value={apiCallsSaved.toLocaleString()}
          tooltip={translate('AutomaticSearchCacheApiCallsSavedTooltip')}
        />
        <StatusBar
          progress={apiCallsSavedPercentage}
          tooltip={translate('AutomaticSearchCacheApiBarTooltip', {
            actual: externalApiCalls.toLocaleString(),
            percentage: apiCallsSavedPercentage.toFixed(1),
            saved: apiCallsSaved.toLocaleString(),
            total: totalApiCalls.toLocaleString(),
          })}
        />
      </div>

      <div className={styles.spacerRow} aria-hidden="true" />

      <div className={styles.metricRow}>
        <Metric
          className={styles.metricLeft}
          label="Baseline Time"
          value={formatDuration(baselineTime)}
          tooltip={translate('AutomaticSearchTotalTimeSavedTooltip')}
        />
        <Metric
          className={styles.metricMiddle}
          label={translate('CacheTimeSaved')}
          value={formatDuration(cacheTimeSaved)}
          tooltip={translate('AutomaticSearchCacheTimeSavedTooltip')}
        />
        <Metric
          className={styles.metricRight}
          label={translate('PollingWaitSaved')}
          value={formatDuration(pollingWaitSaved)}
          tooltip={translate('AutomaticSearchPollingWaitSavedTooltip')}
        />
        <StatusBar
          progress={timeSavedPercentage}
          tooltip={translate('AutomaticSearchCacheTimeBarTooltip', {
            observed: formatDuration(observedTime),
            percentage: timeSavedPercentage.toFixed(1),
            saved: formatDuration(totalTimeSaved),
            total: formatDuration(baselineTime),
          })}
        />
      </div>
    </FieldSet>
  );
}

export default AutomaticSearchCache;
