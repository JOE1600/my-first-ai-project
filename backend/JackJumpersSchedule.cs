using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

public static class JackJumpersSchedule
{
    private const string ScheduleUrl = "https://www.jackjumpers.com.au";
    private const int MaxGames = 20;
    // Every pattern has a timeout so a hostile or malformed page cannot pin the CPU (ReDoS).
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex FixtureLink = new(
        "<a\\b[^>]*href=[\"'](?<href>/schedule/[^\"']+)[\"'][^>]*>(?<content>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
        MatchTimeout);
    private static readonly Regex HomeFixtureSlug = new(
        "^tasmania-jackjumpers-v-(?<opponent>[a-z0-9]+(?:-[a-z0-9]+){0,5})-men-(?<date>\\d{2}-\\d{2}-\\d{4})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        MatchTimeout);
    private static readonly Regex Markup = new("<[^>]+>", RegexOptions.Compiled, MatchTimeout);
    private static readonly Regex TipoffPattern = new(
        "\\b\\d{1,2}:\\d{2}\\s*(?:am|pm)\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        MatchTimeout);
    private static readonly Regex Whitespace = new("\\s+", RegexOptions.Compiled, MatchTimeout);

    public static IReadOnlyList<UpcomingGame> ParseUpcomingHomeGames(string scheduleHtml, DateTime todayUtc)
    {
        var games = new List<UpcomingGame>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match link in FixtureLink.Matches(scheduleHtml))
        {
            var href = WebUtility.HtmlDecode(link.Groups["href"].Value);
            var slug = href.TrimEnd('/').Split('/').Last();
            var fixture = HomeFixtureSlug.Match(slug);
            if (!fixture.Success
                || !DateTime.TryParseExact(
                    fixture.Groups["date"].Value,
                    "dd-MM-yyyy",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var gameDate)
                || gameDate.Date < todayUtc.Date
                || !seen.Add(slug))
            {
                continue;
            }

            var opponent = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                fixture.Groups["opponent"].Value.Replace('-', ' '))
                .Replace("36Ers", "36ers", StringComparison.Ordinal);
            var content = WebUtility.HtmlDecode(Markup.Replace(link.Groups["content"].Value, " "));
            var tipoff = TipoffPattern.Match(content);
            var normalizedTipoff = tipoff.Success
                ? Whitespace.Replace(tipoff.Value, " ").ToLowerInvariant()
                : "Time to be confirmed";

            games.Add(new UpcomingGame(
                slug,
                opponent,
                gameDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                normalizedTipoff,
                gameDate.ToString("ddd, d MMM yyyy", CultureInfo.InvariantCulture),
                $"{ScheduleUrl}/schedule/{slug}"));
        }

        return games.OrderBy(game => game.GameDate, StringComparer.Ordinal).Take(MaxGames).ToArray();
    }
}
