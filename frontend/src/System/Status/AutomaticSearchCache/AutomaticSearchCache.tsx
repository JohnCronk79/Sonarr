import classNames from 'classnames';
import React, { useEffect } from 'react';
import { useDispatch, useSelector } from 'react-redux';
import AppState from 'App/State/AppState';
import FieldSet from 'Components/FieldSet';
import Icon from 'Components/Icon';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ProgressBar from 'Components/ProgressBar';
import Tooltip from 'Components/Tooltip/Tooltip';
import { icons, kinds, sizes } from 'Helpers/Props';
import { fetchStatus } from 'Store/Actions/systemActions';
import translate from 'Utilities/String/translate';
import styles from './AutomaticSearchCache.css';

interface MetricProps {
  className: string;
  label: string;
  tooltip: React.ReactNode;
  value: string;
  warning?: boolean;
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
    <Tooltip
      className={className}
      bodyClassName={styles.tooltipBody}
      kind={kinds.INVERSE}
      showArrow={false}
      anchor={
        <>
          <span className={styles.label}>{label}</span>
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
        </>
      }
      tooltip={tooltip}
    />
  );
}

function AutomaticSearchCache() {
  const dispatch = useDispatch();
  const { isFetching, isPopulated, item } = useSelector(
    (state: AppState) => state.system.status
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
      <FieldSet legend={translate('AutomaticSearchCache')}>
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
  const externalApiPercentage = getPercentage(externalApiCalls, totalApiCalls);
  const cacheTimeSaved = item.automaticSearchCacheTimeSavedMilliseconds ?? 0;
  const pollingWaitSaved =
    item.automaticSearchPollingWaitSavedMilliseconds ?? 0;
  const totalTimeSaved = cacheTimeSaved + pollingWaitSaved;
  const observedTime = item.automaticSearchObservedTimeMilliseconds ?? 0;
  const totalPotentialTime = observedTime + totalTimeSaved;
  const timeSavedPercentage = getPercentage(totalTimeSaved, totalPotentialTime);

  const capacityTooltip = translate(
    'AutomaticSearchCacheCapacitySummaryTooltip',
    {
      cacheSize: `${cacheSizeMb.toLocaleString()} MB`,
      peak: formatMb(peakUsedBytes),
      peakUtilization: peakUtilization.toFixed(1),
      used: formatMb(usedBytes),
      utilization: utilization.toFixed(1),
    }
  );
  const lookupBarTooltip = translate('AutomaticSearchCacheLookupBarTooltip', {
    hits: cacheHits.toLocaleString(),
    misses: cacheMisses.toLocaleString(),
    percentage: cacheHitPercentage.toFixed(1),
  });
  const apiBarTooltip = translate('AutomaticSearchCacheApiBarTooltip', {
    actual: externalApiCalls.toLocaleString(),
    percentage: externalApiPercentage.toFixed(1),
    saved: apiCallsSaved.toLocaleString(),
    total: totalApiCalls.toLocaleString(),
  });
  const timeBarTooltip = translate('AutomaticSearchCacheTimeBarTooltip', {
    observed: formatDuration(observedTime),
    percentage: timeSavedPercentage.toFixed(1),
    saved: formatDuration(totalTimeSaved),
    total: formatDuration(totalPotentialTime),
  });

  return (
    <FieldSet legend={translate('AutomaticSearchCache')}>
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
        <Tooltip
          className={styles.bar}
          bodyClassName={styles.tooltipBody}
          kind={kinds.INVERSE}
          showArrow={false}
          anchor={
            <ProgressBar
              progress={utilization}
              kind={kinds.PRIMARY}
              size={sizes.MEDIUM}
              width={134}
            />
          }
          tooltip={capacityTooltip}
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
        <span />
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
          tooltip={
            <>
              {translate('AutomaticSearchCacheMissesTooltip')}
              <br />
              {translate('AutomaticSearchCacheInitialLoadsTooltip')}
            </>
          }
        />
        <Tooltip
          className={styles.bar}
          bodyClassName={styles.tooltipBody}
          kind={kinds.INVERSE}
          showArrow={false}
          anchor={
            <ProgressBar
              containerClassName={
                cacheMisses > 0 ? styles.warningBar : undefined
              }
              progress={cacheHitPercentage}
              kind={kinds.PRIMARY}
              size={sizes.MEDIUM}
              width={134}
            />
          }
          tooltip={lookupBarTooltip}
        />
      </div>

      <div className={styles.metricRow}>
        <Metric
          className={styles.metricLeft}
          label={translate('ExternalApiCalls')}
          value={externalApiCalls.toLocaleString()}
          tooltip={translate('AutomaticSearchCacheApiCallsTooltip')}
        />
        <Metric
          className={styles.metricMiddle}
          label={translate('ApiCallsSaved')}
          value={apiCallsSaved.toLocaleString()}
          tooltip={translate('AutomaticSearchCacheApiCallsSavedTooltip')}
        />
        <Metric
          className={styles.metricRight}
          label={translate('TotalApiCalls')}
          value={totalApiCalls.toLocaleString()}
          tooltip={translate('AutomaticSearchCacheTotalApiCallsTooltip')}
        />
        <Tooltip
          className={styles.bar}
          bodyClassName={styles.tooltipBody}
          kind={kinds.INVERSE}
          showArrow={false}
          anchor={
            <ProgressBar
              progress={externalApiPercentage}
              kind={kinds.PRIMARY}
              size={sizes.MEDIUM}
              width={134}
            />
          }
          tooltip={apiBarTooltip}
        />
      </div>

      <div className={styles.spacerRow} aria-hidden="true" />

      <div className={styles.metricRow}>
        <Metric
          className={styles.metricLeft}
          label={translate('CacheTimeSaved')}
          value={formatDuration(cacheTimeSaved)}
          tooltip={translate('AutomaticSearchCacheTimeSavedTooltip')}
        />
        <Metric
          className={styles.metricMiddle}
          label={translate('PollingWaitSaved')}
          value={formatDuration(pollingWaitSaved)}
          tooltip={translate('AutomaticSearchPollingWaitSavedTooltip')}
        />
        <Metric
          className={styles.metricRight}
          label={translate('TotalTimeSaved')}
          value={formatDuration(totalTimeSaved)}
          tooltip={translate('AutomaticSearchTotalTimeSavedTooltip')}
        />
        <Tooltip
          className={styles.bar}
          bodyClassName={styles.tooltipBody}
          kind={kinds.INVERSE}
          showArrow={false}
          anchor={
            <ProgressBar
              progress={timeSavedPercentage}
              kind={kinds.PRIMARY}
              size={sizes.MEDIUM}
              width={134}
            />
          }
          tooltip={timeBarTooltip}
        />
      </div>
    </FieldSet>
  );
}

export default AutomaticSearchCache;
