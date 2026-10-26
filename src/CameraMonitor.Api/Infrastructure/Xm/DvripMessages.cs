namespace CameraMonitor.Api.Infrastructure.Xm;

/// <summary>DVRIP request ids; a reply id is request + 1 (1000 → 1001). Spec: third_party/python-dvr/dvrip.py.</summary>
public static class DvripMessages
{
    public const ushort Login = 1000, KeepAlive = 1006, ConfigSet = 1040, ConfigGet = 1042, PtzControl = 1400,
        PlayBack = 1420, PlayBackEof = 1423, PlayBackClaim = 1424, FileQuery = 1440, TimeSetting = 1450, TimeQuery = 1452,
        Guard = 1500, AlarmPush = 1504, Snapshot = 1560;

    public static readonly int[] OkCodes = [100, 150, 515];

    private static readonly Dictionary<int, string> Codes = new()
    {
        [100] = "OK",
        [101] = "Unknown error",
        [102] = "Unsupported version",
        [103] = "Request not permitted",
        [104] = "User already logged in",
        [105] = "User is not logged in",
        [106] = "Username or password is incorrect",
        [107] = "User does not have necessary permissions",
        [108] = "Timeout",
        [109] = "Search failed, no matching file",
        [110] = "Search succeeded, all files returned",
        [111] = "Search succeeded, some files returned",
        [112] = "User already exists",
        [113] = "User does not exist",
        [114] = "User group already exists",
        [115] = "User group does not exist",
        [117] = "Message format error",
        [118] = "PTZ protocol not set",
        [119] = "No file found",
        [120] = "Configuration enabled",
        [121] = "Digital channel not connected",
        [124] = "Algorith error",
        [150] = "Operation successful, device restart required",
        [202] = "User not logged in",
        [203] = "Password is incorrect",
        [204] = "User is invalid",
        [205] = "IP locked",
        [206] = "User is in the blacklist",
        [207] = "Blacklisted",
        [208] = "Input is invalid",
        [209] = "Duplicate index (object to add already exists)",
        [210] = "Object does not exist (query)",
        [211] = "Object does not exist",
        [212] = "Account is in use",
        [213] = "Subset out of range (permissions exceed allowed scope)",
        [214] = "Password is invalid",
        [215] = "Passwords do not match",
        [216] = "Reserved account",
        [502] = "Command is invalid",
        [503] = "Talk is already open",
        [504] = "Talk is not open",
        [511] = "Start of upgrade",
        [512] = "Upgrade was not started",
        [513] = "Upgrade data errors",
        [514] = "Upgrade error",
        [515] = "Upgrade successful",
        [521] = "Restore to default failed",
        [522] = "Device restart required",
        [523] = "Default configuration is invalid",
        [602] = "Application restart required",
        [603] = "System restart required",
        [604] = "File write error",
        [605] = "Feature not supported",
        [606] = "Verification failed",
        [607] = "Configuration does not exist",
        [608] = "Configuration parse error",
    };

    public static string Describe(int ret) => Codes.GetValueOrDefault(ret, "Unknown error");
}

public sealed class DvripException(int ret, string message) : Exception(message)
{
    public int Ret { get; } = ret;
}

/// <summary>From <c>{"AlarmInfo":{"Channel":0,"Event":"HumanDetect","Status":"Start","StartTime":"2026-10-26 14:03:10"}}</c>.</summary>
public sealed record AlarmInfo(int Channel, string Event, string Status, DateTime? StartTimeLocal);

/// <summary>One OPFileQuery item; the camera reports FileLength as a hex string in KB.</summary>
public sealed record XmFileEntry(string FileName, DateTime BeginLocal, DateTime EndLocal, long LengthKb, int Channel);
