using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Cameras;

public sealed partial class CamerasOptionsValidator : IValidateOptions<CamerasOptions>
{
    public ValidateOptionsResult Validate(string? name, CamerasOptions options)
    {
        var errors = new List<string>();

        foreach (var dup in options.Items.GroupBy(c => c.Id).Where(g => g.Count() > 1))
        {
            errors.Add($"{dup.Key}: duplicate camera id");
        }

        foreach (var cam in options.Items)
        {
            ValidateCamera(options, cam, errors);
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateCamera(CamerasOptions options, CameraConfig cam, List<string> errors)
    {
        if (!IdFormat().IsMatch(cam.Id))
        {
            errors.Add($"{cam.Id}: id must match ^[a-z0-9][a-z0-9-]{{0,31}}$");
        }

        if (cam.Lenses.Count == 0)
        {
            errors.Add($"{cam.Id}: camera has no lenses");
        }

        foreach (var dup in cam.Lenses.GroupBy(l => l.Id).Where(g => g.Count() > 1))
        {
            errors.Add($"{cam.Id}: duplicate lens id '{dup.Key}'");
        }

        switch (cam.Vendor)
        {
            case "xm":
                var password = CameraPasswords.Resolve(options, cam);
                if (password is null)
                {
                    errors.Add($"{cam.Id}: password file not found: {cam.PasswordFile ?? options.DefaultPasswordFile}");
                }
                else if (password.IndexOfAny(['&', '?', '#', '/', ':', '@', ' ']) >= 0)
                {
                    errors.Add($"{cam.Id}: password contains forbidden characters (& ? # / : @ space)");
                }

                break;
            case "rtsp":
                foreach (var lens in cam.Lenses.Where(l => string.IsNullOrWhiteSpace(l.RtspUrl)))
                {
                    errors.Add($"{cam.Id}/{lens.Id}: vendor 'rtsp' requires RtspUrl");
                }

                break;
            default:
                errors.Add($"{cam.Id}: unknown vendor '{cam.Vendor}' (expected xm or rtsp)");
                break;
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex IdFormat();
}
