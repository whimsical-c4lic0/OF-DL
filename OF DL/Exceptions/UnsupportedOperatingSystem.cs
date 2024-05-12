namespace OF_DL.Exceptions;

public class UnsupportedOperatingSystem(string platform, string version) : Exception($"{platform} version {version} is not supported")
{
    public string Platform { get; } = platform;
    public string Version { get; } = version;
}
