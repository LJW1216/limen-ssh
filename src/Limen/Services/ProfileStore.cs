using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Limen;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FilePath { get; }
    public List<SshProfile> Profiles { get; private set; } = [];

    public ProfileStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Limen",
            "sessions.json");
    }

    public void Load()
    {
        if (!File.Exists(FilePath))
        {
            Profiles = [];
            return;
        }
        var json = File.ReadAllText(FilePath);
        Profiles = JsonSerializer.Deserialize<List<SshProfile>>(json, Options) ?? [];
        if (LinkJumpProfiles()) Save();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Profiles, Options));
        File.Move(temp, FilePath, overwrite: true);
    }

    public void AddOrUpdate(SshProfile profile)
    {
        var index = Profiles.FindIndex(p => p.Id == profile.Id);
        if (index < 0) Profiles.Add(profile);
        else Profiles[index] = profile;
        SynchronizeJumpCopies(profile);
        Save();
    }

    public void Remove(SshProfile profile)
    {
        foreach (var dependent in Profiles.Where(candidate => candidate.JumpProfileId == profile.Id))
        {
            dependent.JumpProfileId = string.Empty;
            dependent.JumpHost = null;
        }
        Profiles.RemoveAll(p => p.Id == profile.Id);
        Save();
    }

    private void SynchronizeJumpCopies(SshProfile source)
    {
        foreach (var dependent in Profiles.Where(candidate => candidate.JumpProfileId == source.Id))
            dependent.JumpHost = new JumpHostProfile
            {
                Host = source.Host,
                Port = source.Port,
                UserName = source.UserName,
                Auth = source.Auth,
                PrivateKeyPath = source.PrivateKeyPath,
                ProtectedPassword = source.ProtectedPassword,
                ProtectedPassphrase = source.ProtectedPassphrase,
                HostKeyFingerprint = source.HostKeyFingerprint
            };
    }

    public SshProfile? GetJumpProfile(SshProfile profile) =>
        profile.JumpProfileId.Length == 0
            ? null
            : Profiles.FirstOrDefault(candidate => candidate.Id == profile.JumpProfileId && candidate.Id != profile.Id);

    public JumpHostProfile? ResolveJumpHost(SshProfile profile)
    {
        var source = GetJumpProfile(profile);
        return source is null ? profile.JumpHost : new JumpHostProfile
        {
            Host = source.Host,
            Port = source.Port,
            UserName = source.UserName,
            Auth = source.Auth,
            PrivateKeyPath = source.PrivateKeyPath,
            ProtectedPassword = source.ProtectedPassword,
            ProtectedPassphrase = source.ProtectedPassphrase,
            HostKeyFingerprint = source.HostKeyFingerprint
        };
    }

    private bool LinkJumpProfiles()
    {
        var changed = false;
        foreach (var profile in Profiles.Where(profile => profile.JumpHost is not null && profile.JumpProfileId.Length == 0))
        {
            var jump = profile.JumpHost!;
            var source = Profiles.FirstOrDefault(candidate => candidate.Id != profile.Id
                && candidate.Host.Equals(jump.Host, StringComparison.OrdinalIgnoreCase)
                && candidate.Port == jump.Port
                && candidate.UserName.Equals(jump.UserName, StringComparison.Ordinal));
            if (source is null) continue;
            profile.JumpProfileId = source.Id;
            changed = true;
        }
        return changed;
    }

}
