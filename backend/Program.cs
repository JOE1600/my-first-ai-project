using System.ComponentModel.DataAnnotations;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var databaseDirectory = Path.Combine(builder.Environment.ContentRootPath, "app_data");
Directory.CreateDirectory(databaseDirectory);
var connectionString = $"Data Source={Path.Combine(databaseDirectory, "boxwood.db")}";
var adminApiKey = Environment.GetEnvironmentVariable("BOXWOOD_ADMIN_API_KEY");

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod());
});

var app = builder.Build();
app.UseCors("Frontend");

await InitialiseDatabaseAsync(connectionString);

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/enquiries", async (HttpRequest request) =>
{
    if (!IsAuthorised(request, adminApiKey))
    {
        return Results.Unauthorized();
    }

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
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5)));
    }

    return Results.Ok(enquiries);
});

app.MapPost("/api/enquiries", async (EnquiryRequest request, HttpContext context) =>
{
    var validation = ValidateRequest(request);
    if (validation.Count > 0)
    {
        return Results.ValidationProblem(validation);
    }

    var guestName = request.GuestName!.Trim();
    var guestEmail = request.GuestEmail!.Trim().ToLowerInvariant();
    var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    var clientKey = forwardedFor?.Split(',').FirstOrDefault()?.Trim()
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";

    if (!SubmissionLimiter.TryAccept(clientKey))
    {
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    const string insertSql = @"
        INSERT INTO Enquiries (GuestName, GuestEmail, GuestNote, GameChoice, CreatedAtUtc, ClientKey)
        VALUES ($name, $email, $note, $game, $createdAt, $clientKey);
        SELECT last_insert_rowid();";

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = insertSql;
    command.Parameters.AddWithValue("$name", guestName);
    command.Parameters.AddWithValue("$email", guestEmail);
    command.Parameters.AddWithValue("$note", (object?)request.GuestNote?.Trim() ?? DBNull.Value);
    command.Parameters.AddWithValue("$game", request.GameChoice);
    command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
    command.Parameters.AddWithValue("$clientKey", clientKey);

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

    if (name.Length is < 2 or > 80)
    {
        errors["guestName"] = new[] { "Name must be between 2 and 80 characters." };
    }

    if (email.Length > 120 || !new EmailAddressAttribute().IsValid(email))
    {
        errors["guestEmail"] = new[] { "Please provide a valid email address." };
    }

    if (note.Length > 400)
    {
        errors["guestNote"] = new[] { "Your note must be 400 characters or fewer." };
    }

    if (request.GameChoice is not ("next" or "future"))
    {
        errors["gameChoice"] = new[] { "Please select a valid game option." };
    }

    return errors;
}

static bool IsAuthorised(HttpRequest request, string? adminApiKey)
{
    return !string.IsNullOrWhiteSpace(adminApiKey)
        && request.Headers.TryGetValue("X-Admin-Key", out var suppliedKey)
        && suppliedKey == adminApiKey;
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

static class SubmissionLimiter
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, DateTimeOffset> LastSubmissions = new();
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(15);

    public static bool TryAccept(string clientKey)
    {
        lock (Lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (LastSubmissions.TryGetValue(clientKey, out var previous) && now - previous < Cooldown)
            {
                return false;
            }

            LastSubmissions[clientKey] = now;
            return true;
        }
    }
}

public sealed class EnquiryRequest
{
    public string? GuestName { get; init; }
    public string? GuestEmail { get; init; }
    public string? GuestNote { get; init; }
    public string GameChoice { get; init; } = string.Empty;
}

public sealed record EnquiryResponse(
    long Id,
    string GuestName,
    string GuestEmail,
    string? GuestNote,
    string GameChoice,
    string CreatedAtUtc);
