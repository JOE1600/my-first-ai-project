using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// Fetches upcoming home games from the official schedule with defensive limits, and keeps the
/// last good list so a slow, broken or tampered upstream page does not take the picker down.
/// </summary>
public sealed class ScheduleService
{
    private const string CacheKey = "jackjumpers-upcoming-home-games";
    private const int MaxScheduleBytes = 3 * 1024 * 1024;
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StaleFallbackFor = TimeSpan.FromHours(24);

    private readonly IHttpClientFactory clients;
    private readonly IMemoryCache cache;
    private readonly ILogger<ScheduleService> logger;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private (IReadOnlyList<UpcomingGame> Games, DateTimeOffset FetchedAt)? lastGood;

    public ScheduleService(IHttpClientFactory clients, IMemoryCache cache, ILogger<ScheduleService> logger)
    {
        this.clients = clients;
        this.cache = cache;
        this.logger = logger;
    }

    /// <returns>The upcoming games, or null when neither the upstream nor a recent fallback is available.</returns>
    public async Task<IReadOnlyList<UpcomingGame>?> GetUpcomingGamesAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<UpcomingGame>? cached) && cached is not null)
        {
            return cached;
        }

        // One upstream request at a time, so a burst of visitors cannot fan out into a burst of fetches.
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(CacheKey, out cached) && cached is not null)
            {
                return cached;
            }

            var games = await FetchAsync(cancellationToken);
            cache.Set(CacheKey, games, FreshFor);
            lastGood = (games, DateTimeOffset.UtcNow);
            return games;
        }
        catch (Exception exception) when (exception is HttpRequestException or RegexMatchTimeoutException
                                              or InvalidDataException
                                              || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogError(exception, "Unable to retrieve the JackJumpers schedule.");
            if (lastGood is { } fallback && DateTimeOffset.UtcNow - fallback.FetchedAt < StaleFallbackFor)
            {
                logger.LogWarning("Serving the last good schedule from {FetchedAt}.", fallback.FetchedAt);
                return fallback.Games;
            }

            return null;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public static string ChoiceValue(UpcomingGame game) =>
        $"{game.DisplayDate} - JackJumpers vs {game.Opponent} - {game.Tipoff}";

    private async Task<IReadOnlyList<UpcomingGame>> FetchAsync(CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient("JackJumpers")
            .GetAsync("/schedule", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaxScheduleBytes)
        {
            throw new InvalidDataException("The schedule page is larger than expected.");
        }

        // Read at most MaxScheduleBytes even if the server lies about (or omits) Content-Length.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxScheduleBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length
               && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
        {
            total += read;
        }

        if (total > MaxScheduleBytes)
        {
            throw new InvalidDataException("The schedule page is larger than expected.");
        }

        return JackJumpersSchedule.ParseUpcomingHomeGames(Encoding.UTF8.GetString(buffer, 0, total), DateTime.UtcNow.Date);
    }
}
