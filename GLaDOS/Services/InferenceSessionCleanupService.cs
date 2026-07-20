using GLaDOS.Core.Interfaces;
using GLaDOS.Core.Models;

namespace GLaDOS.Services;

public sealed class InferenceSessionCleanupService(IEnumerable<LanguageModel> models) : BackgroundService
{
    // Must run more frequently than the interactive-session watchdog so a lost
    // Potato process gives its VRAM back within a few seconds.
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (IModelSessionUsageProvider model in models.OfType<IModelSessionUsageProvider>())
            {
                model.ReleaseInactiveSessions();
            }
        }
    }
}
