namespace CameraMonitor.Api.Features.Cameras;

public static class CameraPasswords
{
    /// <summary>
    /// Camera password, then camera file, then default password, then default file, else "".
    /// Returns null when a referenced password file is missing (the validator reports it).
    /// </summary>
    public static string? Resolve(CamerasOptions opts, CameraConfig cam)
    {
        if (cam.Password is not null)
        {
            return cam.Password;
        }

        if (cam.PasswordFile is not null)
        {
            return ReadFile(cam.PasswordFile);
        }

        if (opts.DefaultPassword is not null)
        {
            return opts.DefaultPassword;
        }

        return opts.DefaultPasswordFile is not null ? ReadFile(opts.DefaultPasswordFile) : "";
    }

    private static string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;
}
