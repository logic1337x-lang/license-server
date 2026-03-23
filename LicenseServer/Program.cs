using System.Text.Json;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var configuredDbPath = Environment.GetEnvironmentVariable("DB_PATH");
var dbPath = string.IsNullOrWhiteSpace(configuredDbPath)
    ? Path.Combine(app.Environment.ContentRootPath, "data", "licenses.json")
    : configuredDbPath.Trim();
var dbDir = Path.GetDirectoryName(dbPath);
if (!string.IsNullOrWhiteSpace(dbDir))
{
    Directory.CreateDirectory(dbDir);
}

var db = LoadDb(dbPath);

app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.MapGet("/dll-version", () =>
{
    var versionPath = Path.Combine(app.Environment.ContentRootPath, "assets", "dll.version");
    if (File.Exists(versionPath))
    {
        var versionFromFile = File.ReadAllText(versionPath).Trim();
        if (!string.IsNullOrWhiteSpace(versionFromFile))
        {
            return Results.Ok(new { version = versionFromFile });
        }
    }

    var versionFromEnv = Environment.GetEnvironmentVariable("DLL_VERSION");
    var fallback = string.IsNullOrWhiteSpace(versionFromEnv) ? "1.0.0" : versionFromEnv.Trim();
    return Results.Ok(new { version = fallback });
});
app.MapGet("/admin-check", () =>
{
    var adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY") ?? "";
    return Results.Ok(new { hasAdminKey = !string.IsNullOrWhiteSpace(adminKey), length = adminKey.Length });
});

app.MapPost("/activate", async (HttpRequest request) =>
{
    var payload = await JsonSerializer.DeserializeAsync<ActivateRequest>(request.Body, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (payload is null || string.IsNullOrWhiteSpace(payload.LicenseKey) || string.IsNullOrWhiteSpace(payload.DeviceId))
    {
        return Results.BadRequest(new { error = "licenseKey and deviceId are required" });
    }

    var licenseKey = payload.LicenseKey.Trim();
    var deviceId = payload.DeviceId.Trim();
    var hwid = payload.Hwid?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(hwid))
    {
        return Results.BadRequest(new { error = "hwid is required" });
    }

    if (!db.Licenses.TryGetValue(licenseKey, out var record))
    {
        record = new LicenseRecord
        {
            DeviceId = deviceId,
            Hwid = hwid,
            Token = Guid.NewGuid().ToString("N"),
            ActivatedAtUtc = DateTime.UtcNow,
            DurationDays = 0,
            ExpiresAtUtc = null,
            IsBanned = false,
            FailedAttempts = 0,
            OtherDeviceAttempts = 0,
            LastOtherDeviceId = ""
        };
        db.Licenses[licenseKey] = record;
        SaveDb(dbPath, db);
        return Results.Ok(new ActivateResponse(record.Token, "activated"));
    }

    if (record.IsBanned)
    {
        return Results.StatusCode(403);
    }
    if (IsExpired(record))
    {
        return Results.StatusCode(403);
    }

    var isUnbound = string.IsNullOrWhiteSpace(record.DeviceId)
        && string.IsNullOrWhiteSpace(record.Token);
    if (isUnbound)
    {
        record.DeviceId = deviceId;
        record.Hwid = hwid;
        record.Token = Guid.NewGuid().ToString("N");
        record.ActivatedAtUtc = DateTime.UtcNow;
        if (record.DurationDays > 0)
        {
            record.ExpiresAtUtc = record.ActivatedAtUtc.AddDays(record.DurationDays);
        }
        SaveDb(dbPath, db);
        return Results.Ok(new ActivateResponse(record.Token, "activated"));
    }

    if (!string.Equals(record.DeviceId, deviceId, StringComparison.Ordinal))
    {
        RegisterFailure(db, record, deviceId);
        SaveDb(dbPath, db);
        return Results.StatusCode(403);
    }

    if (string.IsNullOrWhiteSpace(record.Hwid))
    {
        record.Hwid = hwid;
        SaveDb(dbPath, db);
    }
    else if (!string.Equals(record.Hwid, hwid, StringComparison.Ordinal))
    {
        RegisterFailure(db, record, deviceId);
        SaveDb(dbPath, db);
        return Results.StatusCode(403);
    }

    return Results.Ok(new ActivateResponse(record.Token, "ok"));
});

app.MapPost("/check", async (HttpRequest request) =>
{
    var payload = await JsonSerializer.DeserializeAsync<CheckRequest>(request.Body, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (payload is null || string.IsNullOrWhiteSpace(payload.LicenseKey) || string.IsNullOrWhiteSpace(payload.DeviceId) || string.IsNullOrWhiteSpace(payload.Token))
    {
        return Results.BadRequest(new { error = "licenseKey, deviceId and token are required" });
    }
    if (string.IsNullOrWhiteSpace(payload.Hwid))
    {
        return Results.BadRequest(new { error = "hwid is required" });
    }

    if (!db.Licenses.TryGetValue(payload.LicenseKey.Trim(), out var record))
    {
        return Results.StatusCode(403);
    }

    if (record.IsBanned)
    {
        return Results.StatusCode(403);
    }
    if (IsExpired(record))
    {
        return Results.StatusCode(403);
    }

    var ok = string.Equals(record.DeviceId, payload.DeviceId.Trim(), StringComparison.Ordinal)
        && string.Equals(record.Token, payload.Token.Trim(), StringComparison.Ordinal)
        && string.Equals(record.Hwid, payload.Hwid.Trim(), StringComparison.Ordinal);

    if (!ok)
    {
        RegisterFailure(db, record, payload.DeviceId.Trim());
        SaveDb(dbPath, db);
        return Results.StatusCode(403);
    }

    return Results.Ok(new
    {
        ok = true,
        durationDays = record.DurationDays,
        expiresAtUtc = record.ExpiresAtUtc,
        remainingSeconds = GetRemainingSeconds(record)
    });
});

app.MapPost("/download", async (HttpRequest request) =>
{
    var payload = await JsonSerializer.DeserializeAsync<CheckRequest>(request.Body, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (payload is null || string.IsNullOrWhiteSpace(payload.LicenseKey) || string.IsNullOrWhiteSpace(payload.DeviceId) || string.IsNullOrWhiteSpace(payload.Token))
    {
        return Results.BadRequest(new { error = "licenseKey, deviceId and token are required" });
    }

    if (!db.Licenses.TryGetValue(payload.LicenseKey.Trim(), out var record))
    {
        return Results.StatusCode(403);
    }

    if (record.IsBanned)
    {
        return Results.StatusCode(403);
    }
    if (IsExpired(record))
    {
        return Results.StatusCode(403);
    }

    var ok = string.Equals(record.DeviceId, payload.DeviceId.Trim(), StringComparison.Ordinal)
        && string.Equals(record.Token, payload.Token.Trim(), StringComparison.Ordinal)
        && string.Equals(record.Hwid, payload.Hwid?.Trim(), StringComparison.Ordinal);

    if (!ok)
    {
        RegisterFailure(db, record, payload.DeviceId.Trim());
        SaveDb(dbPath, db);
        return Results.StatusCode(403);
    }

    var dllPath = Environment.GetEnvironmentVariable("DLL_PATH");
    if (string.IsNullOrWhiteSpace(dllPath))
    {
        dllPath = ResolveDllPath(app.Environment.ContentRootPath, record.Variant);
    }

    if (!File.Exists(dllPath))
    {
        return Results.NotFound(new { error = "DLL not found on server" });
    }

    var fileName = Path.GetFileName(dllPath);
    return Results.File(dllPath, "application/octet-stream", fileName);
});

app.MapPost("/reset", async (HttpRequest request) =>
{
    var adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY");
    if (string.IsNullOrWhiteSpace(adminKey))
    {
        return Results.StatusCode(404);
    }

    var payload = await JsonSerializer.DeserializeAsync<ResetRequest>(request.Body, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (payload is null || string.IsNullOrWhiteSpace(payload.AdminKey) || string.IsNullOrWhiteSpace(payload.LicenseKey))
    {
        return Results.BadRequest(new { error = "adminKey and licenseKey are required" });
    }

    if (!string.Equals(payload.AdminKey, adminKey, StringComparison.Ordinal))
    {
        return Results.StatusCode(403);
    }

    if (db.Licenses.Remove(payload.LicenseKey.Trim()))
    {
        SaveDb(dbPath, db);
    }

    return Results.Ok(new { ok = true });
});

app.MapPost("/ban", async (HttpRequest request) =>
{
    var adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY");
    if (string.IsNullOrWhiteSpace(adminKey))
    {
        return Results.StatusCode(404);
    }

    var payload = await JsonSerializer.DeserializeAsync<BanRequest>(request.Body, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (payload is null || string.IsNullOrWhiteSpace(payload.AdminKey) || string.IsNullOrWhiteSpace(payload.LicenseKey))
    {
        return Results.BadRequest(new { error = "adminKey and licenseKey are required" });
    }

    if (!string.Equals(payload.AdminKey, adminKey, StringComparison.Ordinal))
    {
        return Results.StatusCode(403);
    }

    var key = payload.LicenseKey.Trim();
    if (!db.Licenses.TryGetValue(key, out var record))
    {
        record = new LicenseRecord
        {
            DeviceId = "",
            Token = "",
            ActivatedAtUtc = DateTime.UtcNow,
            IsBanned = true,
            FailedAttempts = 0,
            OtherDeviceAttempts = 0,
            LastOtherDeviceId = ""
        };
        db.Licenses[key] = record;
    }
    else
    {
        record.IsBanned = true;
    }

    SaveDb(dbPath, db);
    return Results.Ok(new { ok = true });
});

app.MapPost("/unban", async (HttpRequest request) =>
{
    var adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY");
    if (string.IsNullOrWhiteSpace(adminKey))
    {
        return Results.StatusCode(404);
    }

    var payload = await JsonSerializer.DeserializeAsync<BanRequest>(request.Body, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (payload is null || string.IsNullOrWhiteSpace(payload.AdminKey) || string.IsNullOrWhiteSpace(payload.LicenseKey))
    {
        return Results.BadRequest(new { error = "adminKey and licenseKey are required" });
    }

    if (!string.Equals(payload.AdminKey, adminKey, StringComparison.Ordinal))
    {
        return Results.StatusCode(403);
    }

    var key = payload.LicenseKey.Trim();
    if (db.Licenses.TryGetValue(key, out var record))
    {
        record.IsBanned = false;
        record.FailedAttempts = 0;
        record.OtherDeviceAttempts = 0;
        record.LastOtherDeviceId = "";
        SaveDb(dbPath, db);
    }

    return Results.Ok(new { ok = true });
});

app.MapPost("/list", async (HttpRequest request) =>
{
    var adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY");
    if (string.IsNullOrWhiteSpace(adminKey))
    {
        return Results.StatusCode(404);
    }

    var payload = await JsonSerializer.DeserializeAsync<AdminRequest>(request.Body, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (payload is null || string.IsNullOrWhiteSpace(payload.AdminKey))
    {
        return Results.BadRequest(new { error = "adminKey is required" });
    }

    if (!string.Equals(payload.AdminKey, adminKey, StringComparison.Ordinal))
    {
        return Results.StatusCode(403);
    }

    var items = db.Licenses.Select(kvp => new
    {
        licenseKey = kvp.Key,
        deviceId = kvp.Value.DeviceId,
        hwid = kvp.Value.Hwid,
        token = kvp.Value.Token,
        activatedAtUtc = kvp.Value.ActivatedAtUtc,
        durationDays = kvp.Value.DurationDays,
        expiresAtUtc = kvp.Value.ExpiresAtUtc,
        isExpired = IsExpired(kvp.Value),
        variant = kvp.Value.Variant,
        isBanned = kvp.Value.IsBanned,
        failedAttempts = kvp.Value.FailedAttempts,
        otherDeviceAttempts = kvp.Value.OtherDeviceAttempts,
        lastOtherDeviceId = kvp.Value.LastOtherDeviceId
    });

    return Results.Ok(new { items });
});

app.MapPost("/set-duration", async (HttpRequest request) =>
{
    var adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY");
    if (string.IsNullOrWhiteSpace(adminKey))
    {
        return Results.StatusCode(404);
    }

    var payload = await JsonSerializer.DeserializeAsync<SetDurationRequest>(request.Body, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (payload is null || string.IsNullOrWhiteSpace(payload.AdminKey) || string.IsNullOrWhiteSpace(payload.LicenseKey))
    {
        return Results.BadRequest(new { error = "adminKey and licenseKey are required" });
    }

    if (!string.Equals(payload.AdminKey, adminKey, StringComparison.Ordinal))
    {
        return Results.StatusCode(403);
    }

    var key = payload.LicenseKey.Trim();
    if (!db.Licenses.TryGetValue(key, out var record))
    {
        // Pre-provision non-activated license with expiry policy.
        record = new LicenseRecord
        {
            DeviceId = "",
            Hwid = "",
            Token = "",
            ActivatedAtUtc = default,
            IsBanned = false,
            FailedAttempts = 0,
            OtherDeviceAttempts = 0,
            LastOtherDeviceId = ""
        };
        db.Licenses[key] = record;
    }

    var days = payload.Days;
    if (days < 0)
    {
        return Results.BadRequest(new { error = "days must be >= 0" });
    }

    record.DurationDays = days;
    if (days == 0)
    {
        record.ExpiresAtUtc = null;
    }
    else
    {
        if (record.ActivatedAtUtc == default)
        {
            // Not activated yet: expiry will be set on first activation.
            record.ExpiresAtUtc = null;
        }
        else
        {
            record.ExpiresAtUtc = record.ActivatedAtUtc.AddDays(days);
        }
    }

    SaveDb(dbPath, db);
    return Results.Ok(new { ok = true, durationDays = record.DurationDays, expiresAtUtc = record.ExpiresAtUtc });
});

app.MapPost("/set-expiry-minutes", async (HttpRequest request) =>
{
    var adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY");
    if (string.IsNullOrWhiteSpace(adminKey))
    {
        return Results.StatusCode(404);
    }

    var payload = await JsonSerializer.DeserializeAsync<SetExpiryMinutesRequest>(request.Body, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (payload is null || string.IsNullOrWhiteSpace(payload.AdminKey) || string.IsNullOrWhiteSpace(payload.LicenseKey))
    {
        return Results.BadRequest(new { error = "adminKey and licenseKey are required" });
    }

    if (!string.Equals(payload.AdminKey, adminKey, StringComparison.Ordinal))
    {
        return Results.StatusCode(403);
    }

    var minutes = payload.Minutes;
    if (minutes < 0)
    {
        return Results.BadRequest(new { error = "minutes must be >= 0" });
    }

    var key = payload.LicenseKey.Trim();
    if (!db.Licenses.TryGetValue(key, out var record))
    {
        return Results.NotFound(new { error = "license not found" });
    }

    if (minutes == 0)
    {
        record.ExpiresAtUtc = DateTime.UtcNow;
    }
    else
    {
        record.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(minutes);
    }

    SaveDb(dbPath, db);
    return Results.Ok(new
    {
        ok = true,
        expiresAtUtc = record.ExpiresAtUtc,
        remainingSeconds = GetRemainingSeconds(record)
    });
});

app.MapPost("/set-variant", async (HttpRequest request) =>
{
    var adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY");
    if (string.IsNullOrWhiteSpace(adminKey))
    {
        return Results.StatusCode(404);
    }

    var payload = await JsonSerializer.DeserializeAsync<SetVariantRequest>(request.Body, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (payload is null || string.IsNullOrWhiteSpace(payload.AdminKey) || string.IsNullOrWhiteSpace(payload.LicenseKey))
    {
        return Results.BadRequest(new { error = "adminKey and licenseKey are required" });
    }

    if (!string.Equals(payload.AdminKey, adminKey, StringComparison.Ordinal))
    {
        return Results.StatusCode(403);
    }

    var variant = NormalizeVariant(payload.Variant);
    if (string.IsNullOrWhiteSpace(variant))
    {
        return Results.BadRequest(new { error = "variant must be one of: bezgrab, sgrab, default" });
    }

    var key = payload.LicenseKey.Trim();
    if (!db.Licenses.TryGetValue(key, out var record))
    {
        record = new LicenseRecord
        {
            DeviceId = "",
            Hwid = "",
            Token = "",
            ActivatedAtUtc = default,
            DurationDays = 0,
            ExpiresAtUtc = null,
            Variant = variant,
            IsBanned = false,
            FailedAttempts = 0,
            OtherDeviceAttempts = 0,
            LastOtherDeviceId = ""
        };
        db.Licenses[key] = record;
    }
    else
    {
        record.Variant = variant;
    }

    SaveDb(dbPath, db);
    return Results.Ok(new { ok = true, variant = record.Variant });
});

app.Run();

static void RegisterFailure(LicenseDb db, LicenseRecord record, string deviceId)
{
    record.FailedAttempts++;
    if (!string.IsNullOrWhiteSpace(deviceId) && !string.Equals(record.DeviceId, deviceId, StringComparison.Ordinal))
    {
        record.OtherDeviceAttempts++;
        record.LastOtherDeviceId = deviceId;
    }
    if (record.FailedAttempts >= 10)
    {
        record.IsBanned = true;
    }
}

static bool IsExpired(LicenseRecord record)
{
    if (record.ExpiresAtUtc is null)
    {
        return false;
    }

    return DateTime.UtcNow > record.ExpiresAtUtc.Value;
}

static int GetRemainingSeconds(LicenseRecord record)
{
    if (record.ExpiresAtUtc is null)
    {
        return -1;
    }

    var sec = (int)Math.Floor((record.ExpiresAtUtc.Value - DateTime.UtcNow).TotalSeconds);
    return sec < 0 ? 0 : sec;
}

static string NormalizeVariant(string? raw)
{
    var v = (raw ?? "").Trim().ToLowerInvariant();
    if (v == "bezgrab" || v == "sgrab" || v == "default")
    {
        return v;
    }
    return "";
}

static string ResolveDllPath(string contentRootPath, string? variant)
{
    var assets = Path.Combine(contentRootPath, "assets");
    var v = NormalizeVariant(variant);
    string preferred = v switch
    {
        "bezgrab" => Path.Combine(assets, "Protected_bez.dll"),
        "sgrab" => Path.Combine(assets, "Protected_s.dll"),
        _ => Path.Combine(assets, "Protected.dll")
    };

    if (File.Exists(preferred))
    {
        return preferred;
    }

    return Path.Combine(assets, "Protected.dll");
}

static LicenseDb LoadDb(string path)
{
    var pgConnectionString = GetPostgresConnectionString();
    if (!string.IsNullOrWhiteSpace(pgConnectionString))
    {
        var dbFromPg = LoadDbFromPostgres(pgConnectionString);
        if (dbFromPg is not null)
        {
            return dbFromPg;
        }
    }

    if (!File.Exists(path))
    {
        return new LicenseDb();
    }

    var json = File.ReadAllText(path);
    var db = JsonSerializer.Deserialize<LicenseDb>(json);
    return db ?? new LicenseDb();
}

static void SaveDb(string path, LicenseDb db)
{
    var pgConnectionString = GetPostgresConnectionString();
    if (!string.IsNullOrWhiteSpace(pgConnectionString))
    {
        SaveDbToPostgres(pgConnectionString, db);
        return;
    }

    var json = JsonSerializer.Serialize(db, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(path, json);
}

static LicenseDb? LoadDbFromPostgres(string connectionString)
{
    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();

        EnsurePostgresSchema(conn);

        using var cmd = new NpgsqlCommand("SELECT data::text FROM app_state WHERE id = 1", conn);
        var raw = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new LicenseDb();
        }

        return JsonSerializer.Deserialize<LicenseDb>(raw) ?? new LicenseDb();
    }
    catch
    {
        return new LicenseDb();
    }
}

static void SaveDbToPostgres(string connectionString, LicenseDb db)
{
    using var conn = new NpgsqlConnection(connectionString);
    conn.Open();

    EnsurePostgresSchema(conn);

    var json = JsonSerializer.Serialize(db, new JsonSerializerOptions { WriteIndented = true });
    const string sql = @"INSERT INTO app_state (id, data, updated_at)
VALUES (1, @data::jsonb, NOW())
ON CONFLICT (id)
DO UPDATE SET data = EXCLUDED.data, updated_at = NOW();";

    using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("data", json);
    cmd.ExecuteNonQuery();
}

static void EnsurePostgresSchema(NpgsqlConnection conn)
{
    const string sql = @"CREATE TABLE IF NOT EXISTS app_state (
    id INT PRIMARY KEY,
    data JSONB NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);";

    using var cmd = new NpgsqlCommand(sql, conn);
    cmd.ExecuteNonQuery();
}

static string GetPostgresConnectionString()
{
    var raw = Environment.GetEnvironmentVariable("DATABASE_URL");
    if (string.IsNullOrWhiteSpace(raw))
    {
        return "";
    }

    if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
        || raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var database = uri.AbsolutePath.TrimStart('/');

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = username,
            Password = password,
            Database = database
        };

        return builder.ConnectionString;
    }

    return raw;
}

record ActivateRequest(string LicenseKey, string DeviceId, string? Hwid);
record ActivateResponse(string Token, string Status);
record CheckRequest(string LicenseKey, string DeviceId, string Token, string? Hwid);
record ResetRequest(string AdminKey, string LicenseKey);
record AdminRequest(string AdminKey);
record BanRequest(string AdminKey, string LicenseKey);
record SetDurationRequest(string AdminKey, string LicenseKey, int Days);
record SetExpiryMinutesRequest(string AdminKey, string LicenseKey, int Minutes);
record SetVariantRequest(string AdminKey, string LicenseKey, string Variant);

class LicenseDb
{
    public Dictionary<string, LicenseRecord> Licenses { get; set; } = new();
}

class LicenseRecord
{
    public string DeviceId { get; set; } = "";
    public string Hwid { get; set; } = "";
    public string Token { get; set; } = "";
    public DateTime ActivatedAtUtc { get; set; }
    public int DurationDays { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public string Variant { get; set; } = "default";
    public bool IsBanned { get; set; }
    public int FailedAttempts { get; set; }
    public int OtherDeviceAttempts { get; set; }
    public string LastOtherDeviceId { get; set; } = "";
}


