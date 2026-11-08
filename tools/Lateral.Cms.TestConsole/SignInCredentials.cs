using System.Security.Cryptography;
using System.Text.Json;

namespace Lateral.Cms.TestConsole;

/// <summary>
/// Passwords for the website sign-in when none are configured: a random GUID each for <c>admin</c> and <c>user</c>, written once to a
/// private, git-ignored file and reused after that. The website can be put on a public address, so no password is ever a default
/// that is written down in the repository. The file is the only place they appear; they are never logged.
/// </summary>
public static class SignInCredentials
{
    public const string FileName = "console-credentials.json";

    public static string PathIn(string contentRoot) => Path.Combine(contentRoot, "App_Data", FileName);

    /// <summary>Reads the file, or makes it (mode 600 where the system has modes) with a fresh GUID per person.</summary>
    public static List<PersonOptions> LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var people = document.RootElement.GetProperty("People").EnumerateArray()
                .Select(p => new PersonOptions { Name = p.GetProperty("Name").GetString()!, Password = p.GetProperty("Password").GetString()!, Role = p.GetProperty("Role").GetString()! })
                .ToList();
            if (people.Count > 0 && people.All(p => Guid.TryParse(p.Password, out _)))
            {
                return people;
            }
        }

        List<PersonOptions> made =
        [
            new() { Name = "admin", Password = Guid.NewGuid().ToString(), Role = SignIn.AdminRole },
            new() { Name = "user", Password = Guid.NewGuid().ToString(), Role = SignIn.UserRole },
        ];
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Create it private from the start, so the passwords are never readable by another account even for a moment.
        using (var stream = CreatePrivate(path))
        {
            JsonSerializer.Serialize(stream, new { Note = "Sign-in passwords for the website (admin: everything; user: the photo library only). Delete this file to rotate them.", People = made },
                new JsonSerializerOptions { WriteIndented = true });
        }

        return made;
    }

    // UnixCreateMode is not supported on Windows, and the analyzer (an error in CI) wants the check where the call is, not a
    // conditional expression around it. Windows files take the folder's access rules, which for App_Data are the user's own.
    private static FileStream CreatePrivate(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }
}
