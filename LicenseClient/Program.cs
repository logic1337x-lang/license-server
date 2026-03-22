using System.Net.Http.Json;
using System.Text.Json;

var resetMode = args.Any(a => a.Equals("--reset", StringComparison.OrdinalIgnoreCase));

var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
var appDir = Path.Combine(appData, "MiniLicenseClient");
Directory.CreateDirectory(appDir);

var deviceIdPath = Path.Combine(appDir, "device_id.txt");
var tokenPath = Path.Combine(appDir, "license_token.txt");
var licenseKeyPath = Path.Combine(appDir, "license_key.txt");

var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
var config = LoadConfig(configPath);

var serverUrl = args.FirstOrDefault(a => !a.StartsWith("--"))
    ?? config.ServerUrl
    ?? Prompt("Server URL", "http://localhost:5000");

var deviceId = File.Exists(deviceIdPath)
    ? File.ReadAllText(deviceIdPath).Trim()
    : Guid.NewGuid().ToString("N");

if (string.IsNullOrWhiteSpace(deviceId))
{
    deviceId = Guid.NewGuid().ToString("N");
}

File.WriteAllText(deviceIdPath, deviceId);

var licenseKey = args.FirstOrDefault(a => !a.StartsWith("--") && a != serverUrl)
    ?? (File.Exists(licenseKeyPath) ? File.ReadAllText(licenseKeyPath).Trim() : "")
    ?? config.LicenseKey;

if (string.IsNullOrWhiteSpace(licenseKey))
{
    licenseKey = Prompt("License key", "TEST-KEY-001");
}

File.WriteAllText(licenseKeyPath, licenseKey);

using var http = new HttpClient();
http.Timeout = TimeSpan.FromSeconds(10);

if (resetMode)
{
    var adminKey = Prompt("Admin key", "CHANGE-ME");
    await ResetAsync(http, serverUrl, licenseKey, adminKey);
    return;
}

if (File.Exists(tokenPath))
{
    var token = File.ReadAllText(tokenPath).Trim();
    if (!string.IsNullOrWhiteSpace(token))
    {
        var ok = await CheckTokenAsync(http, serverUrl, licenseKey, deviceId, token);
        if (ok)
        {
            Console.WriteLine("Token valid. Access granted.");
            Console.WriteLine($"DeviceId: {deviceId}");
            return;
        }
    }
}

await ActivateAsync(http, serverUrl, licenseKey, deviceId, tokenPath);

static ClientConfig LoadConfig(string path)
{
    try
    {
        if (!File.Exists(path))
        {
            return new ClientConfig();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ClientConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new ClientConfig();
    }
    catch
    {
        return new ClientConfig();
    }
}

static async Task ActivateAsync(HttpClient http, string serverUrl, string licenseKey, string deviceId, string tokenPath)
{
    try
    {
        var response = await http.PostAsJsonAsync($"{serverUrl.TrimEnd('/')}/activate", new
        {
            licenseKey,
            deviceId
        });

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Activation failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            return;
        }

        var json = await response.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<ActivateResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (data is null || string.IsNullOrWhiteSpace(data.Token))
        {
            Console.WriteLine("Activation failed: invalid response");
            return;
        }

        File.WriteAllText(tokenPath, data.Token);
        Console.WriteLine("Activated OK");
        Console.WriteLine($"Token saved to: {tokenPath}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
}

static async Task<bool> CheckTokenAsync(HttpClient http, string serverUrl, string licenseKey, string deviceId, string token)
{
    try
    {
        var response = await http.PostAsJsonAsync($"{serverUrl.TrimEnd('/')}/check", new
        {
            licenseKey,
            deviceId,
            token
        });

        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var json = await response.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<CheckResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return data?.Ok == true;
    }
    catch
    {
        return false;
    }
}

static async Task ResetAsync(HttpClient http, string serverUrl, string licenseKey, string adminKey)
{
    try
    {
        var response = await http.PostAsJsonAsync($"{serverUrl.TrimEnd('/')}/reset", new
        {
            licenseKey,
            adminKey
        });

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Reset failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            return;
        }

        Console.WriteLine("Reset OK");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
}

static string Prompt(string label, string fallback)
{
    Console.Write($"{label} [{fallback}]: ");
    var input = Console.ReadLine();
    return string.IsNullOrWhiteSpace(input) ? fallback : input.Trim();
}

record ActivateResponse(string Token, string Status);
record CheckResponse(bool Ok);

class ClientConfig
{
    public string? ServerUrl { get; set; }
    public string? LicenseKey { get; set; }
}
