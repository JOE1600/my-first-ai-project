using System.Data.Common;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

public sealed class DataOptions
{
    public int RetentionDays { get; init; } = 180;
    public int BackupIntervalHours { get; init; } = 24;
    public int BackupsToKeep { get; init; } = 14;
}

/// <summary>
/// Encrypts guest names, emails and notes before they reach SQLite, so a copied database file or
/// backup is unreadable without BOXWOOD_DATA_KEY. Values written before encryption was enabled are
/// read as-is and re-encrypted at startup.
///
/// Format v2 is AES-256-CBC with an HMAC-SHA256 tag (encrypt-then-MAC). It is used instead of
/// AES-GCM because .NET 6 on macOS has no AES-GCM; v2 works on every platform and on .NET 6 and 8.
/// v1 (AES-256-GCM) values are still read wherever the platform supports GCM.
/// </summary>
public sealed class GuestDataCipher
{
    private const string PrefixV1 = "enc:v1:";
    private const string PrefixV2 = "enc:v2:";
    private const int GcmNonceSize = 12;
    private const int GcmTagSize = 16;
    private const int IvSize = 16;
    private const int MacSize = 32;
    private readonly byte[]? gcmKey;
    private readonly byte[]? encryptionKey;
    private readonly byte[]? macKey;
    private readonly byte[] clientHashKey;

    private GuestDataCipher(byte[]? gcmKey, byte[]? encryptionKey, byte[]? macKey, byte[] clientHashKey)
    {
        this.gcmKey = gcmKey;
        this.encryptionKey = encryptionKey;
        this.macKey = macKey;
        this.clientHashKey = clientHashKey;
    }

    public bool IsEnabled => encryptionKey is not null;

    /// <param name="base64Key">A base64 string holding exactly 32 random bytes, or null to disable encryption.</param>
    public static GuestDataCipher FromBase64Key(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            // No key: store plaintext and hash client addresses with a per-process key (Development only).
            return new GuestDataCipher(null, null, null, RandomNumberGenerator.GetBytes(32));
        }

        byte[] master;
        try
        {
            master = Convert.FromBase64String(base64Key.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("BOXWOOD_DATA_KEY must be base64. Generate one with: openssl rand -base64 32");
        }

        if (master.Length != 32)
        {
            throw new InvalidOperationException("BOXWOOD_DATA_KEY must decode to exactly 32 bytes. Generate one with: openssl rand -base64 32");
        }

        // Separate subkeys, so no key is ever used for two jobs.
        return new GuestDataCipher(
            SubKey(master, "boxwood/guest-data/v1"),
            SubKey(master, "boxwood/guest-data-enc/v2"),
            SubKey(master, "boxwood/guest-data-mac/v2"),
            SubKey(master, "boxwood/client-key/v1"));
    }

    private static byte[] SubKey(byte[] master, string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.UTF8.GetBytes(purpose));

    public static bool IsEncrypted(string value) =>
        value.StartsWith(PrefixV2, StringComparison.Ordinal) || value.StartsWith(PrefixV1, StringComparison.Ordinal);

    public string Protect(string value)
    {
        if (encryptionKey is null || macKey is null || IsEncrypted(value))
        {
            return value;
        }

        using var aes = Aes.Create();
        aes.Key = encryptionKey;
        var iv = RandomNumberGenerator.GetBytes(IvSize);
        var ciphertext = aes.EncryptCbc(Encoding.UTF8.GetBytes(value), iv);

        var payload = new byte[IvSize + ciphertext.Length + MacSize];
        iv.CopyTo(payload, 0);
        ciphertext.CopyTo(payload, IvSize);
        HMACSHA256.HashData(macKey, payload.AsSpan(0, IvSize + ciphertext.Length), payload.AsSpan(IvSize + ciphertext.Length));
        return PrefixV2 + Convert.ToBase64String(payload);
    }

    public string Unprotect(string value)
    {
        if (!IsEncrypted(value))
        {
            return value;
        }

        if (encryptionKey is null || macKey is null || gcmKey is null)
        {
            throw new InvalidOperationException("The database holds encrypted guest data but BOXWOOD_DATA_KEY is not set.");
        }

        return value.StartsWith(PrefixV2, StringComparison.Ordinal)
            ? UnprotectV2(Convert.FromBase64String(value[PrefixV2.Length..]))
            : UnprotectV1(Convert.FromBase64String(value[PrefixV1.Length..]));
    }

    private string UnprotectV2(byte[] payload)
    {
        if (payload.Length < IvSize + 16 + MacSize)
        {
            throw new CryptographicException("Encrypted guest data is truncated.");
        }

        // Check the tag before decrypting anything, in constant time.
        var signed = payload.AsSpan(0, payload.Length - MacSize);
        var expected = HMACSHA256.HashData(macKey!, signed);
        if (!CryptographicOperations.FixedTimeEquals(expected, payload.AsSpan(payload.Length - MacSize)))
        {
            throw new CryptographicException("Encrypted guest data failed its integrity check.");
        }

        using var aes = Aes.Create();
        aes.Key = encryptionKey!;
        return Encoding.UTF8.GetString(aes.DecryptCbc(signed[IvSize..], signed[..IvSize]));
    }

    private string UnprotectV1(byte[] payload)
    {
        if (!AesGcm.IsSupported)
        {
            throw new PlatformNotSupportedException("This value was encrypted with AES-GCM, which this platform cannot decrypt.");
        }

        var plaintext = new byte[payload.Length - GcmNonceSize - GcmTagSize];
#if NET8_0_OR_GREATER
        using var aes = new AesGcm(gcmKey!, GcmTagSize);
#else
        using var aes = new AesGcm(gcmKey!);
#endif
        aes.Decrypt(
            payload.AsSpan(0, GcmNonceSize),
            payload.AsSpan(GcmNonceSize + GcmTagSize),
            payload.AsSpan(GcmNonceSize, GcmTagSize),
            plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>A keyed hash of the client address: enough to spot repeat senders, useless for finding them.</summary>
    public string HashClient(string clientAddress) =>
        Convert.ToHexString(HMACSHA256.HashData(clientHashKey, Encoding.UTF8.GetBytes(clientAddress)))[..32].ToLowerInvariant();
}

public static class GuestDataStore
{
    /// <summary>Owner-only access (700 for folders, 600 for files). No-op on Windows, which uses ACLs.</summary>
    public static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows() || !(File.Exists(path) || Directory.Exists(path)))
        {
            return;
        }

#if NET7_0_OR_GREATER
        var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(path, Directory.Exists(path) ? ownerOnly | UnixFileMode.UserExecute : ownerOnly);
#else
        // .NET 6 has no File.SetUnixFileMode, so call chmod directly. Octal 700 = 448, 600 = 384.
        if (chmod(path, Directory.Exists(path) ? 448 : 384) != 0)
        {
            throw new IOException($"Could not restrict permissions on {path} (errno {Marshal.GetLastWin32Error()}).");
        }
#endif
    }

#if !NET7_0_OR_GREATER
    [DllImport("libc", SetLastError = true)]
    private static extern int chmod(string path, int mode);
#endif

    public static void RestrictDatabaseFiles(string databasePath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            RestrictToOwner(databasePath + suffix);
        }
    }

    /// <summary>Encrypts any rows stored before encryption was switched on. Returns the number updated.</summary>
    public static async Task<int> EncryptLegacyRowsAsync(EnquiryDatabase database, GuestDataCipher cipher)
    {
        if (!cipher.IsEnabled)
        {
            return 0;
        }

        await using var connection = await database.OpenAsync();
        var legacy = new List<(long Id, string Name, string Email, string? Note, string ClientKey)>();
        await using (var select = EnquiryDatabase.Command(connection, @"
                SELECT Id, GuestName, GuestEmail, GuestNote, ClientKey
                FROM Enquiries
                WHERE GuestName NOT LIKE 'enc:v_:%' OR GuestEmail NOT LIKE 'enc:v_:%'
                   OR (GuestNote IS NOT NULL AND GuestNote NOT LIKE 'enc:v_:%');"))
        {
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                legacy.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
            }
        }

        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var row in legacy)
        {
            await using var update = EnquiryDatabase.Command(connection, @"
                UPDATE Enquiries
                SET GuestName = @name, GuestEmail = @email, GuestNote = @note, ClientKey = @client
                WHERE Id = @id;",
                ("@name", cipher.Protect(row.Name)),
                ("@email", cipher.Protect(row.Email)),
                ("@note", row.Note is null ? null : cipher.Protect(row.Note)),
                // Old rows stored raw IP addresses; replace them with the keyed hash.
                ("@client", row.ClientKey.Length == 32 && row.ClientKey.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
                    ? row.ClientKey
                    : cipher.HashClient(row.ClientKey)),
                ("@id", row.Id));
            update.Transaction = transaction;
            await update.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        if (legacy.Count > 0)
        {
            // Rewrite the file so the old plaintext copies do not survive in free pages.
            await database.CompactAsync(connection);
        }

        return legacy.Count;
    }
}

/// <summary>
/// Daily housekeeping: deletes enquiries older than the retention period, then (for SQLite) takes an
/// online backup of the database and keeps only the newest few. Hosted PostgreSQL keeps its own backups.
/// </summary>
public sealed class GuestDataMaintenance : BackgroundService
{
    private readonly EnquiryDatabase database;
    private readonly string databaseDirectory;
    private readonly DataOptions options;
    private readonly ILogger<GuestDataMaintenance> logger;

    public GuestDataMaintenance(
        EnquiryDatabase database,
        string databaseDirectory,
        DataOptions options,
        ILogger<GuestDataMaintenance> logger)
    {
        this.database = database;
        this.databaseDirectory = databaseDirectory;
        this.options = options;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, options.BackupIntervalHours)));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Guest data maintenance failed; it will retry next cycle.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.RetentionDays).ToString("O");
        await using (var connection = await database.OpenAsync(cancellationToken))
        {
            await using var delete = EnquiryDatabase.Command(
                connection, "DELETE FROM Enquiries WHERE CreatedAtUtc < @cutoff;", ("@cutoff", cutoff));
            var removed = await delete.ExecuteNonQueryAsync(cancellationToken);
            if (removed > 0)
            {
                // Rebuild the file so deleted rows do not linger in free pages.
                await database.CompactAsync(connection, cancellationToken);
                logger.LogInformation(
                    "Retention: deleted {Count} enquiries older than {Days} days.", removed, options.RetentionDays);
            }
        }

        if (database.IsPostgres)
        {
            return;
        }

        var backupDirectory = Path.Combine(databaseDirectory, "backups");
        Directory.CreateDirectory(backupDirectory);
        GuestDataStore.RestrictToOwner(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, $"boxwood-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
        await using (var source = new SqliteConnection(database.SqliteConnectionString))
        await using (var destination = new SqliteConnection($"Data Source={backupPath};Pooling=False"))
        {
            await source.OpenAsync(cancellationToken);
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
        }

        GuestDataStore.RestrictToOwner(backupPath);
        foreach (var old in Directory.GetFiles(backupDirectory, "boxwood-*.db")
                     .OrderByDescending(path => path, StringComparer.Ordinal)
                     .Skip(Math.Max(1, options.BackupsToKeep)))
        {
            File.Delete(old);
        }

        logger.LogInformation("Backup written to {Path}.", Path.GetFileName(backupPath));
    }
}
