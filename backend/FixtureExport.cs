using System.Text;
using System.Text.Json;

/// <summary>
/// One-off export of upcoming home games to a JSON file, in the same shape as GET /api/games.
/// Uses the same parser as the API, so the static fixtures.json and the live API always agree.
/// </summary>
public static class FixtureExport
{
    private const int MaxScheduleBytes = 3 * 1024 * 1024;

    /// <returns>0 on success; 1 when the schedule could not be read or listed no games.</returns>
    public static async Task<int> RunAsync(string outputPath)
    {
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri("https://www.jackjumpers.com.au"),
                Timeout = TimeSpan.FromSeconds(30),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BoxwoodStayAndPlay/1.0");

            using var response = await client.GetAsync("/schedule", HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length > MaxScheduleBytes)
            {
                throw new InvalidDataException("The schedule page is larger than expected.");
            }

            var games = JackJumpersSchedule.ParseUpcomingHomeGames(Encoding.UTF8.GetString(bytes), DateTime.UtcNow.Date);
            if (games.Count == 0)
            {
                // An empty list usually means the page layout changed. Keep the site's fallback instead.
                Console.Error.WriteLine("No upcoming home games found on the schedule page; fixtures file not written.");
                return 1;
            }

            var json = JsonSerializer.Serialize(games, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(outputPath, json);
            Console.WriteLine($"Wrote {games.Count} upcoming home games to {outputPath}.");
            return 0;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            Console.Error.WriteLine($"Could not read the JackJumpers schedule: {exception.Message}");
            return 1;
        }
    }
}
