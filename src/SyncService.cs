using Cronos;

namespace FileSyncServer
{
    public class SyncService(FileSyncConfig cfg, ILogger<SyncService> log) : BackgroundService
    {
        private readonly FileSyncConfig _cfg = cfg;
        private readonly ILogger<SyncService> _logger = log;
        private readonly HttpClient _client = new()
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var schedules = _cfg.Config.Sync.Schedule
                .Select(s => CronExpression.Parse(s))
                .ToList();

            _logger.LogInformation("🕓 Sync scheduler started with {Count} cron rules", schedules.Count);

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var nextRuns = schedules
                    .Select(c => c.GetNextOccurrence(now, TimeZoneInfo.Local))
                    .Where(t => t.HasValue)
                    .Select(t => t!.Value)
                    .ToList();

                var nextRun = nextRuns.Count != 0 ? nextRuns.Min() : now.AddHours(12);
                if (nextRun < now)
                    nextRun = now.AddMinutes(1);

                var delay = nextRun - now;
                _logger.LogInformation(
                    "Next sync in {Delay:dd\\.hh\\:mm\\:ss} at {NextRun}",
                    delay,
                    nextRun.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
                );

                await DelayUntil(nextRun, _logger, stoppingToken);
                await SyncAll();
            }
        }

        public async Task SyncAll()
        {
            _logger.LogInformation("🔄 Starting synchronization...");
            var mirrorBasePath = FileServerExtensions.NormalizePath(_cfg.Files.Mirror!.BasePath);

            foreach (var category in _cfg.Files.Mirror.Data)
            {
                var dir = Path.Combine(mirrorBasePath, category.Key);
                Directory.CreateDirectory(dir);

                foreach (var kv in category.Value)
                {
                    var localFile = Path.Combine(dir, kv.Key);
                    var remoteUrl = kv.Value;

                    await SyncFile(localFile, remoteUrl);
                }
            }

            _logger.LogInformation("✅ Synchronization finished.");
        }

        private async Task SyncFile(string localPath, string url)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);

                if (File.Exists(localPath))
                {
                    var info = new FileInfo(localPath);
                    req.Headers.IfModifiedSince = info.LastWriteTimeUtc;
                }

                var resp = await _client.SendAsync(req);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    _logger.LogInformation("No update for {File}", localPath);
                    return;
                }

                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(localPath, bytes);
                _logger.LogInformation("Updated {File} ({Size} bytes)", localPath, bytes.Length);
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("Timeout while downloading {Url}", url);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing {File}", localPath);
            }
        }

        /// <summary>
        /// Safely delays the log until a specified time.
        /// Supports very long intervals and rounds the log to the nearest minute.
        /// </summary>
        private static async Task DelayUntil(DateTime nextRun, ILogger logger, CancellationToken stoppingToken)
        {
            TimeSpan delay = nextRun - DateTime.UtcNow;
            if (delay <= TimeSpan.Zero)
                return;

            var maxDelay = TimeSpan.FromMilliseconds(int.MaxValue);

            while (delay > TimeSpan.Zero)
            {
                var chunk = delay > maxDelay ? maxDelay : delay;

                if (chunk > TimeSpan.FromMinutes(1))
                {
                    var rounded = TimeSpan.FromMinutes(Math.Ceiling(chunk.TotalMinutes));
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("⏳ [DelayUntil] Waiting {Delay:dd\\.hh\\:mm} until next run...", rounded);
                }

                try
                {
                    await Task.Delay(chunk, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    logger.LogInformation("Delay cancelled.");
                    return;
                }

                delay = nextRun - DateTime.UtcNow;
            }
        }
    }
}
