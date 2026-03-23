using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var dataDir = Path.Combine(app.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "licenses.json");

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
        dllPath = Path.Combine(app.Environment.ContentRootPath, "assets", "Protected.dll");
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

static LicenseDb LoadDb(string path)
{
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
    var json = JsonSerializer.Serialize(db, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(path, json);
}

record ActivateRequest(string LicenseKey, string DeviceId, string? Hwid);
record ActivateResponse(string Token, string Status);
record CheckRequest(string LicenseKey, string DeviceId, string Token, string? Hwid);
record ResetRequest(string AdminKey, string LicenseKey);
record AdminRequest(string AdminKey);
record BanRequest(string AdminKey, string LicenseKey);
record SetDurationRequest(string AdminKey, string LicenseKey, int Days);

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
    public bool IsBanned { get; set; }
    public int FailedAttempts { get; set; }
    public int OtherDeviceAttempts { get; set; }
    public string LastOtherDeviceId { get; set; } = "";
}
