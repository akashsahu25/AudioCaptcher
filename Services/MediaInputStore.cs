using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace AudioStreaming.Backend.Services;

public sealed class AudioMediaOptions
{
    public string LibraryRoot { get; set; } = "";
}

public sealed record AvailableAudio(Guid MediaId, string Name);

// IDs refer only to supported files beneath the server-configured library, never browser-supplied paths.
public sealed class MediaInputStore(IWebHostEnvironment environment, IOptions<AudioMediaOptions> options)
{
    private IEnumerable<(AvailableAudio Info, string Path)> Enumerate()
    {
        var configured = options.Value.LibraryRoot;
        if (!string.IsNullOrWhiteSpace(configured) && !Path.IsPathFullyQualified(configured))
            throw new InvalidOperationException("AudioMedia:LibraryRoot must be an absolute server folder path.");
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "Media") : configured);
        if (!Directory.Exists(root)) throw new InvalidOperationException("Configure AudioMedia:LibraryRoot to the VM/server audio folder.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Audio library root must not be a symbolic link or junction.");
        var extensions = new HashSet<string>([".mp3", ".wav", ".webm", ".ogg", ".opus", ".flac", ".m4a", ".aac", ".wma", ".mp4"], StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
        { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (!extensions.Contains(Path.GetExtension(path))) continue;
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())).AsSpan(0, 16));
            yield return (new(id, Path.GetRelativePath(root, path)), path);
        }
    }

    public IReadOnlyList<AvailableAudio> List() => Enumerate().Select(item => item.Info).OrderBy(item => item.Name).ToArray();

    public string Resolve(Guid id) => Enumerate().FirstOrDefault(item => item.Info.MediaId == id).Path
        ?? throw new ArgumentException("Unknown mediaId. Select an available server audio file first.");
}
