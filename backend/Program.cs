using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var databaseDirectory = Path.Combine(builder.Environment.ContentRootPath, "app_data");
Directory.CreateDirectory(databaseDirectory);
GuestDataStore.RestrictToOwner(databaseDirectory);
var databasePath = Path.Combine(databaseDirectory, "boxwood.db");
var connectionString = $"Data Source={databasePath}";
var security = builder.Configuration.GetSection("Security").Get<SecurityOptions>() ?? new SecurityOptions();
var dataOptions = builder.Configuration.GetSection("Data").Get<DataOptions>() ?? new DataOptions();

// The manager key only ever comes from the environment, never from source or appsettings.
var adminApiKey = Environment.GetEnvironmentVariable("BOXWOOD_ADMIN_API_KEY");
var adminKeyProblem = string.IsNullOrWhiteSpace(adminApiKey)
    ? "BOXWOOD_ADMIN_API_KEY is not set, so the enquiry list is disabled."
    : adminApiKey.Length < security.MinimumAdminKeyLength
        ? $"BOXWOOD_ADMIN_API_KEY is shorter than {security.MinimumAdminKeyLength} characters."
        : null;
if (adminKeyProblem is not null && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        $"{adminKeyProblem} Generate one with: openssl rand -base64 32");
}

// Guest names, emails and notes are encrypted with a key that also only lives in the environment.
var cipher = GuestDataCipher.FromBase64Key(Environment.GetEnvironmentVariable("BOXWOOD_DATA_KEY"));
if (!cipher.IsEnabled && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "BOXWOOD_DATA_KEY is not set, so guest data would be stored unencrypted. Generate one with: openssl rand -base64 32");
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = security.MaxRequestBodyBytes;
    kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
    kestrel.Limits.MaxRequestLineSize = 4 * 1024;
    kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
});

// Only believe X-Forwarded-For from proxies we were told about; otherwise anyone could
// pick their own "IP address" and dodge rate limits and bans.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var proxy in security.TrustedProxies)
    {
        options.KnownProxies.Add(IPAddress.Parse(proxy));
    }

    foreach (var network in security.TrustedNetworks)
    {
        var parts = network.Split('/');
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(
            IPAddress.Parse(parts[0]), int.Parse(parts[1])));
    }
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins(security.AllowedOrigins)
            .WithMethods("GET", "POST")
            .WithHeaders("Content-Type", "X-Admin-Key"));
});

builder.Services.AddSingleton(security);
builder.Services.AddSingleton<ClientGuard>();
builder.Services.AddSingleton(cipher);
builder.Services.AddSingleton(dataOptions);
builder.Services.AddHostedService(services => new GuestDataMaintenance(
    connectionString,
    databaseDirectory,
    dataOptions,
    services.GetRequiredService<ILogger<GuestDataMaintenance>>()));
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("JackJumpers", client =>
{
    client.BaseAddress = new Uri("https://www.jackjumpers.com.au");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BoxwoodStayAndPlay/1.0");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));

var app = builder.Build();
if (adminKeyProblem is not null)
{
    app.Logger.LogWarning("{Problem} This is only allowed in Development.", adminKeyProblem);
}

if (!cipher.IsEnabled)
{
    app.Logger.LogWarning("BOXWOOD_DATA_KEY is not set: guest data is stored unencrypted. This is only allowed in Development.");
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseApiSecurityHeaders();
if (security.TrustedProxies.Length > 0 || security.TrustedNetworks.Length > 0)
{
    app.UseForwardedHeaders();
}

// CORS runs before the firewall so browsers can read 429/403 responses and show a useful message.
app.UseCors("Frontend");
app.UseApplicationFirewall();

await InitialiseDatabaseAsync(connectionString);
GuestDataStore.RestrictDatabaseFiles(databasePath);
var encryptedRows = await GuestDataStore.EncryptLegacyRowsAsync(connectionString, cipher);
if (encryptedRows > 0)
{
    app.Logger.LogInformation("Encrypted {Count} enquiries that were stored before encryption was enabled.", encryptedRows);
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/games", async (ScheduleService schedule, CancellationToken cancellationToken) =>
{
    var games = await schedule.GetUpcomingGamesAsync(cancellationToken);
    return games is null
        ? Results.Problem(
            "The JackJumpers schedule is temporarily unavailable. Please use the official fixture link.",
            statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.Ok(games);
});

app.MapGet("/api/enquiries", async (HttpContext context, ClientGuard guard) =>
{
    var client = SecurityPipeline.ClientKey(context);
    if (string.IsNullOrWhiteSpace(adminApiKey)
        || !SecurityPipeline.KeysMatch(context.Request.Headers["X-Admin-Key"].FirstOrDefault(), adminApiKey))
    {
        if (guard.RecordAdminFailure(client))
        {
            app.Logger.LogWarning(
                "Firewall: {Client} locked out after {Attempts} wrong manager keys.",
                client,
                security.AdminMaxFailedAttempts);
        }

        return Results.Unauthorized();
    }

    guard.RecordAdminSuccess(client);
    app.Logger.LogInformation("Manager enquiry list read by {Client}.", client);

    const string selectSql = @"
        SELECT Id, GuestName, GuestEmail, GuestNote, GameChoice, CreatedAtUtc
        FROM Enquiries
        ORDER BY Id DESC;";

    var enquiries = new List<EnquiryResponse>();
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = selectSql;
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        enquiries.Add(new EnquiryResponse(
            reader.GetInt64(0),
            cipher.Unprotect(reader.GetString(1)),
            cipher.Unprotect(reader.GetString(2)),
            reader.IsDBNull(3) ? null : cipher.Unprotect(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5)));
    }

    return Results.Ok(enquiries);
});

app.MapPost("/api/enquiries", async (
    EnquiryRequest request,
    HttpContext context,
    ClientGuard guard,
    ScheduleService schedule,
    CancellationToken cancellationToken) =>
{
    var clientKey = SecurityPipeline.ClientKey(context);

    // Honeypot: the "website" field is hidden from people, so only bots fill it in.
    // Pretend it worked so the bot has nothing to learn from, but store nothing.
    if (!string.IsNullOrEmpty(request.Website))
    {
        app.Logger.LogWarning("Firewall: honeypot enquiry discarded from {Client}.", clientKey);
        return Results.Created("/api/enquiries/0", new { id = 0, message = "Enquiry received." });
    }

    var validation = ValidateRequest(request);
    if (validation.Count == 0 && !await IsListedGameChoiceAsync(request.GameChoice, schedule, cancellationToken))
    {
        validation["gameChoice"] = new[] { "Please select a listed game or enquire about another home game." };
    }

    if (validation.Count > 0)
    {
        return Results.ValidationProblem(validation);
    }

    if (!guard.TryAcceptEnquiry(clientKey))
    {
        return Results.Problem(
            "Please wait a few seconds before sending another enquiry.",
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    var guestName = request.GuestName!.Trim();
    var guestEmail = request.GuestEmail!.Trim().ToLowerInvariant();

    const string insertSql = @"
        INSERT INTO Enquiries (GuestName, GuestEmail, GuestNote, GameChoice, CreatedAtUtc, ClientKey)
        VALUES ($name, $email, $note, $game, $createdAt, $clientKey);
        SELECT last_insert_rowid();";

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = insertSql;
    var guestNote = request.GuestNote?.Trim();
    command.Parameters.AddWithValue("$name", cipher.Protect(guestName));
    command.Parameters.AddWithValue("$email", cipher.Protect(guestEmail));
    command.Parameters.AddWithValue("$note", string.IsNullOrEmpty(guestNote) ? DBNull.Value : cipher.Protect(guestNote));
    command.Parameters.AddWithValue("$game", request.GameChoice);
    command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
    command.Parameters.AddWithValue("$clientKey", cipher.HashClient(clientKey));

    var id = Convert.ToInt64(await command.ExecuteScalarAsync());
    return Results.Created($"/api/enquiries/{id}", new { id, message = "Enquiry received." });
});

app.Run();

static Dictionary<string, string[]> ValidateRequest(EnquiryRequest request)
{
    var errors = new Dictionary<string, string[]>();
    var name = request.GuestName?.Trim() ?? string.Empty;
    var email = request.GuestEmail?.Trim() ?? string.Empty;
    var note = request.GuestNote?.Trim() ?? string.Empty;

    if (name.Length is < 2 or > 80 || SecurityPipeline.ContainsControlCharacters(name, allowLineBreaks: false))
    {
        errors["guestName"] = new[] { "Name must be between 2 and 80 characters." };
    }

    if (email.Length > 120
        || SecurityPipeline.ContainsControlCharacters(email, allowLineBreaks: false)
        || !new EmailAddressAttribute().IsValid(email))
    {
        errors["guestEmail"] = new[] { "Please provide a valid email address." };
    }

    if (note.Length > 400)
    {
        errors["guestNote"] = new[] { "Your note must be 400 characters or fewer." };
    }
    else if (SecurityPipeline.ContainsControlCharacters(note, allowLineBreaks: true))
    {
        errors["guestNote"] = new[] { "Your note contains characters we cannot accept." };
    }

    if (request.GameChoice is not ("next" or "future")
        && (request.GameChoice.Length is < 3 or > 180
            || SecurityPipeline.ContainsControlCharacters(request.GameChoice, allowLineBreaks: false)))
    {
        errors["gameChoice"] = new[] { "Please select a listed game or enquire about another home game." };
    }

    return errors;
}

// A tampered request cannot invent a fixture: the choice must match a game from the official
// list. If the schedule is unreachable we fall back to the shape checks in ValidateRequest.
static async Task<bool> IsListedGameChoiceAsync(
    string gameChoice,
    ScheduleService schedule,
    CancellationToken cancellationToken)
{
    if (gameChoice is "next" or "future")
    {
        return true;
    }

    var games = await schedule.GetUpcomingGamesAsync(cancellationToken);
    return games is null || games.Any(game => ScheduleService.ChoiceValue(game) == gameChoice);
}

static async Task InitialiseDatabaseAsync(string connectionString)
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = @"
        CREATE TABLE IF NOT EXISTS Enquiries (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            GuestName TEXT NOT NULL,
            GuestEmail TEXT NOT NULL,
            GuestNote TEXT,
            GameChoice TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            ClientKey TEXT NOT NULL
        );
        ";
    await command.ExecuteNonQueryAsync();
}

public sealed class EnquiryRequest
{
    public string? GuestName { get; init; }
    public string? GuestEmail { get; init; }
    public string? GuestNote { get; init; }
    public string GameChoice { get; init; } = string.Empty;
    public string? Website { get; init; }
}

public sealed record EnquiryResponse(
    long Id,
    string GuestName,
    string GuestEmail,
    string? GuestNote,
    string GameChoice,
    string CreatedAtUtc);

public sealed record UpcomingGame(
    string Id,
    string Opponent,
    string GameDate,
    string Tipoff,
    string DisplayDate,
    string OfficialUrl);
