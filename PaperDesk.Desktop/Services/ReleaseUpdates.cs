using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PaperDesk.Desktop.Services;

internal sealed record ReleaseUpdate(string Tag, bool Installed, long Size, string Sha256)
{
    internal string AssetName => Installed ? "PaperDesk-Setup-win-x64.exe" : "PaperDesk-Portable-win-x64.zip";
    internal Uri DownloadUri => new($"https://github.com/Zkvr/PaperDesk/releases/download/{Tag}/{AssetName}");
    internal string Version => Tag[1..];
}

internal sealed class ReleaseUpdates(HttpClient http)
{
    private const string Releases = "https://api.github.com/repos/Zkvr/PaperDesk/releases";
    private const long MaxPackageBytes = 512L * 1024 * 1024;
    private static readonly Regex VersionPattern = new(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(preview|beta|rc)(?:\.(0|[1-9][0-9]*))?)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    internal static readonly ReleaseUpdates Shared = new(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }));
    internal static string CurrentVersion => typeof(ReleaseUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+', 2)[0];

    internal async Task<ReleaseUpdate?> FindAsync(string currentVersion, bool installed, CancellationToken token)
    {
        if (!TryVersion(currentVersion, out var current)) return null;
        var stable = current.Stage == 3;
        using var request = Request(new Uri(Releases + (stable ? "/latest" : "?per_page=20")));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var releases = stable
            ? new[] { await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: token).ConfigureAwait(false) }
            : await response.Content.ReadFromJsonAsync<GitHubRelease[]>(cancellationToken: token).ConfigureAwait(false) ?? [];

        ReleaseUpdate? best = null;
        foreach (var release in releases)
        {
            if (release is not { Draft: false, Tag: { } tag, Assets: { } assets } || !tag.StartsWith('v') ||
                !TryVersion(tag[1..], out var candidate) || candidate.CompareTo(current) <= 0 ||
                release.Prerelease != (candidate.Stage != 3) || (stable && release.Prerelease)) continue;
            var update = new ReleaseUpdate(tag, installed, 0, "");
            var asset = assets.FirstOrDefault(item => item.Name == update.AssetName && item.State == "uploaded" &&
                item.Size is > 0 and <= MaxPackageBytes && item.DownloadUrl == update.DownloadUri.AbsoluteUri &&
                item.Digest is { Length: 71 } digest && digest.StartsWith("sha256:", StringComparison.Ordinal) &&
                digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0);
            if (asset == null) continue;
            best = update with { Size = asset.Size, Sha256 = asset.Digest![7..] };
            current = candidate;
        }
        return best;
    }

    internal async Task<string> DownloadAsync(ReleaseUpdate update, string folder, IProgress<int>? progress, CancellationToken token)
    {
        var path = Path.Combine(folder, update.AssetName);
        var partial = path + ".part";
        using var response = await OpenDownloadAsync(update.DownloadUri, token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != update.Size)
            throw new InvalidDataException("The release download length changed.");

        await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
        await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[64 * 1024];
            long total = 0;
            var percent = -1;
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                total += count;
                if (total > update.Size) throw new InvalidDataException("The release download exceeded its expected size.");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                var next = (int)(total * 100 / update.Size);
                if (next != percent)
                {
                    percent = next;
                    progress?.Report(next);
                }
            }
            if (total != update.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The release download failed SHA-256 verification.");
        }
        if (update.Installed) RequireVersion(partial, update.Version);
        File.Move(partial, path);
        return path;
    }

    private async Task<HttpResponseMessage> OpenDownloadAsync(Uri uri, CancellationToken token)
    {
        for (var hop = 0; hop < 5; hop++)
        {
            using var request = Request(uri);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                if ((int)response.StatusCode is < 300 or >= 400) response.EnsureSuccessStatusCode();
                if (response.Headers.Location is not { } location || !Uri.TryCreate(uri, location, out var next) ||
                    next.Scheme != Uri.UriSchemeHttps || !next.IsDefaultPort || next.UserInfo.Length != 0 ||
                    next.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException("The release download redirected away from GitHub.");
                uri = next;
            }
        }
        throw new InvalidDataException("Too many release download redirects.");
    }

    internal static void RequireVersion(string exe, string version)
    {
        if (FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Trim() != version)
            throw new InvalidDataException("The executable version does not match the release tag.");
    }

    private static HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("PaperDesk");
        return request;
    }

    private static bool TryVersion(string value, out (Version Number, int Stage, int Sequence) version)
    {
        version = default;
        var match = VersionPattern.Match(value);
        if (!match.Success || match.Length != value.Length || !Version.TryParse(value.Split('-', 2)[0], out var number)) return false;
        var sequence = -1;
        if (match.Groups[5].Success && !int.TryParse(match.Groups[5].Value, out sequence)) return false;
        var stage = match.Groups[4].Value switch { "preview" => 0, "beta" => 1, "rc" => 2, _ => 3 };
        version = (number, stage, sequence);
        return true;
    }

    private sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string Tag,
        bool Draft, bool Prerelease, GitHubAsset[] Assets);
    private sealed record GitHubAsset(string Name, string State, long Size, string? Digest,
        [property: JsonPropertyName("browser_download_url")] string DownloadUrl);
}
