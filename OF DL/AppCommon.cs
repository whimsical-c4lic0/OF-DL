using System.Runtime.InteropServices;
using Newtonsoft.Json;
using OF_DL.Entities;
using OF_DL.Helpers;
using Serilog;

namespace OF_DL;

public class AppCommon
{
    private readonly Auth _auth;
    private readonly Config _config;
    private readonly bool _useCdrmProject;

    private readonly IAPIHelper _apiHelper;
    private readonly IDBHelper _dbHelper;
    private readonly IDownloadHelper _downloadHelper;

    private Dictionary<string, int> _activeSubscriptions = new();
    private Dictionary<string, int> _expiredSubscriptions = new();

    public AppCommon()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File("logs/OFDL.txt", rollingInterval: RollingInterval.Day)
            .WriteTo.Console()
            .CreateLogger();

        VerifyOSCompatibility();
        _auth = GetAuth();
        _config = GetConfig();
        _useCdrmProject = !DetectDrmKeysPresence();
        LoadFfmpeg();

        _apiHelper = new APIHelper();
        _dbHelper = new DBHelper();
        _downloadHelper = new DownloadHelper();
    }

    private static void VerifyOSCompatibility()
    {
        var os = Environment.OSVersion;
        if (os.Platform == PlatformID.Win32NT && os.Version.Major < 10)
        {
            Log.Error("Windows version prior to 10.x: {0}", os.VersionString);
            throw new Exception("Windows version prior to 10.x");
        }
    }

    private static Auth GetAuth()
    {
        if (File.Exists("auth.json"))
        {
            Log.Debug("auth.json located successfully");
            var authJson = JsonConvert.DeserializeObject<Auth>(File.ReadAllText("auth.json"));
            if (authJson != null)
            {
                return authJson;
            }
            else
            {
                Log.Error("auth.json is invalid");
                throw new Exception("auth.json is invalid");
            }
        }
        else
        {
            Log.Error("auth.json does not exist");
            throw new Exception("auth.json does not exist");
        }
    }

    private static Config GetConfig()
    {
        if (File.Exists("config.json"))
        {
            Log.Debug("config.json located successfully");
            var configJson = JsonConvert.DeserializeObject<Config>(File.ReadAllText("config.json"));
            if (configJson != null)
            {
                return configJson;
            }
            else
            {
                Log.Error("config.json is invalid");
                throw new Exception("config.json is invalid");
            }
        }
        else
        {
            Log.Error("config.json does not exist");
            throw new Exception("config.json does not exist");
        }
    }

    private void LoadFfmpeg()
    {
        var ffmpegFound = false;
        var pathAutoDetected = false;
        if (!string.IsNullOrEmpty(_config!.FFmpegPath) && ValidateFilePath(_config.FFmpegPath))
        {
            // FFmpeg path is set in config.json and is valid
            ffmpegFound = true;
        }
        else if (!string.IsNullOrEmpty(_auth!.FFMPEG_PATH) && ValidateFilePath(_auth.FFMPEG_PATH))
        {
            // FFmpeg path is set in auth.json and is valid (config.json takes precedence and auth.json is only available for backward compatibility)
            ffmpegFound = true;
            _config.FFmpegPath = _auth.FFMPEG_PATH;
        }
        else if (string.IsNullOrEmpty(_config.FFmpegPath))
        {
            // FFmpeg path is not set in config.json, so we will try to locate it in the PATH or current directory
            var ffmpegPath = GetFullPath("ffmpeg");
            if (ffmpegPath != null)
            {
                // FFmpeg is found in the PATH or current directory
                ffmpegFound = true;
                pathAutoDetected = true;
                _config.FFmpegPath = ffmpegPath;
            }
            else
            {
                // FFmpeg is not found in the PATH or current directory, so we will try to locate the windows executable
                ffmpegPath = GetFullPath("ffmpeg.exe");
                if (ffmpegPath != null)
                {
                    // FFmpeg windows executable is found in the PATH or current directory
                    ffmpegFound = true;
                    pathAutoDetected = true;
                    _config.FFmpegPath = ffmpegPath;
                }
            }
        }

        if (ffmpegFound)
        {
            Log.Debug(
                pathAutoDetected
                    ? $"FFmpeg located successfully. Path auto-detected: {_config.FFmpegPath}"
                    : $"FFmpeg located successfully"
            );

            // Escape backslashes in the path for Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _config.FFmpegPath!.Contains(@":\") && !_config.FFmpegPath.Contains(@":\\"))
            {
                _config.FFmpegPath = _config.FFmpegPath.Replace(@"\", @"\\");
            }
        }
        else
        {
            Log.Error($"Cannot locate FFmpeg with path: {_config.FFmpegPath}");
            throw new Exception("Cannot locate FFmpeg");
        }
    }

    private static bool DetectDrmKeysPresence()
    {
        var clientIdBlobMissing = false;
        var devicePrivateKeyMissing = false;

        if (!File.Exists("cdm/devices/chrome_1610/device_client_id_blob"))
        {
            clientIdBlobMissing = true;
        }
        else
        {
            Log.Debug($"device_client_id_blob located successfully");
        }

        if (!File.Exists("cdm/devices/chrome_1610/device_private_key"))
        {
            devicePrivateKeyMissing = true;
        }
        else
        {
            Log.Debug($"device_private_key located successfully");
        }

        if (!clientIdBlobMissing && !devicePrivateKeyMissing)
        {
            return true;
        }

        Log.Information("device_client_id_blob and/or device_private_key missing, https://cdrm-project.com/ will be used instead for DRM protected videos");
        return false;
    }

    private static bool ValidateFilePath(string path)
    {
        var invalidChars = System.IO.Path.GetInvalidPathChars();
        var foundInvalidChars = path.Where(c => invalidChars.Contains(c)).ToArray();

        if (foundInvalidChars.Length != 0)
        {
            Log.Information($"Invalid characters found in path {path}:[/] {string.Join(", ", foundInvalidChars)}");
            return false;
        }

        if (File.Exists(path)) return true;

        Log.Information(
            Directory.Exists(path)
                ? $"The provided path {path} improperly points to a directory and not a file."
                : $"The provided path {path} does not exist or is not accessible."
        );

        return false;
    }

    private static string? GetFullPath(string filename)
    {
        if (File.Exists(filename))
        {
            return Path.GetFullPath(filename);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return pathEnv
            .Split(Path.PathSeparator)
            .Select(path => Path.Combine(path, filename))
            .FirstOrDefault(File.Exists);
    }

    public async Task<User> GetUser()
    {
        var user = await _apiHelper.GetUserInfo("/users/me", _auth);

        if (user is { id: not null })
        {
            Log.Debug($"Logged in successfully as {user.name} {user.username}");
            return user;
        }

        Log.Error("Auth failed, please check the values in auth.json are correct");
        throw new Exception("Failed to get user info");
    }

    private async Task<Dictionary<string, int>> GetActiveSubscriptions()
    {
        if (_activeSubscriptions.Count > 0)
        {
            return _activeSubscriptions;
        }

        _activeSubscriptions = await _apiHelper.GetActiveSubscriptions("/subscriptions/subscribes", _auth, _config.IncludeRestrictedSubscriptions);
        return _activeSubscriptions;
    }

    private async Task<Dictionary<string, int>> GetExpiredSubscriptions()
    {
        if (_expiredSubscriptions.Count > 0)
        {
            return _expiredSubscriptions;
        }

        _expiredSubscriptions = await _apiHelper.GetExpiredSubscriptions("/subscriptions/subscribes", _auth, _config.IncludeRestrictedSubscriptions);
        return _expiredSubscriptions;
    }

    public async Task<Dictionary<string, int>> GetSubscriptions()
    {
        var subscriptions = new Dictionary<string, int>();

        foreach (var (key, value) in  await GetActiveSubscriptions())
        {
            subscriptions.Add(key, value);
        }

        if (_config.IncludeExpiredSubscriptions)
        {
            foreach (var (key, value) in await GetExpiredSubscriptions())
            {
                subscriptions.Add(key, value);
            }
        }

        return subscriptions;
    }

    public async Task<Dictionary<string, int>> GetLists()
    {
        return await _apiHelper.GetLists("/lists", _auth);
    }

    public async Task CreateOrUpdateUsersDatabase()
    {
        var users = await GetSubscriptions();
        await _dbHelper.CreateUsersDB(users);
    }
}
