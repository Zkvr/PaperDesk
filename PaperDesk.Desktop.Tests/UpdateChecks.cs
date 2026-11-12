using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaperDesk.Desktop.Services;

static class UpdateChecks
{
    public static int Run(string folder)
    {
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        CheckRelease(folder).GetAwaiter().GetResult();
        CheckPortable(folder);
        return 0;
    }

    private static async Task CheckRelease(string folder)
    {
        var bytes = "Synthetic release asset"u8.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        var token = CancellationToken.None;
        const string zip = "PaperDesk-Portable-win-x64.zip";
        const string setup = "PaperDesk-Setup-win-x64.exe";
        using var http = new HttpClient(new FakeHttp(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/latest")) return JsonResponse(Release("v0.9.0", prerelease: false));
            if (uri.Host == "api.github.com") return JsonResponse(new[]
            {
                Release("v1.0.0", draft: true, prerelease: false), Release("v0.8.1-beta.9"),
                Release("v0.8.1-rc", badUrl: true), Release("v0.8.1-beta.10"), Release("v0.7.0-rc"),
            });
            if (uri.Host == "github.com") return Redirect("https://release-assets.githubusercontent.com/paperdesk-fixture");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var releases = new ReleaseUpdates(http);
        var portable = await releases.FindAsync("0.8.0-beta", installed: false, token);
        Require(portable is { Tag: "v0.8.1-beta.10", AssetName: zip }, "Preview ordering, asset URL and draft handling");
        var installed = await releases.FindAsync("0.8.0", installed: true, token);
        Require(installed is { Tag: "v0.9.0", AssetName: setup }, "Stable builds select a stable installer");
        Require(await releases.FindAsync("0.9.0", installed: false, token) == null, "The current stable version must not update itself");
        var verified = await releases.DownloadAsync(portable!, folder, null, token);
        Require(File.ReadAllBytes(verified).SequenceEqual(bytes), "Verified bytes must reach the final package path");
        var invalid = Directory.CreateDirectory(Path.Combine(folder, "invalid")).FullName;
        await RejectAsync(() => releases.DownloadAsync(portable! with { Size = bytes.Length + 1 }, invalid, null, token));
        await RejectAsync(() => releases.DownloadAsync(portable! with { Sha256 = new string('0', 64) }, invalid, null, token));
        Require(!File.Exists(Path.Combine(invalid, zip)), "An invalid hash must never publish the downloaded package");

        using var foreign = new HttpClient(new FakeHttp(request =>
        {
            Require(request.RequestUri!.Host == "github.com", "Foreign redirect must be refused before contacting the host");
            return Redirect("https://example.test/paperdesk-fixture");
        }));
        await RejectAsync(() => new ReleaseUpdates(foreign).DownloadAsync(portable!, invalid, null, token));
        Console.WriteLine("PASS GitHub release selection, length and SHA-256 verification, and redirect restrictions");

        object Release(string tag, bool draft = false, bool prerelease = true, bool badUrl = false) => new
        {
            tag_name = tag, draft, prerelease,
            assets = new[] { zip, setup }.Select(name => new
            {
                name, size = bytes.Length, state = "uploaded", digest = "sha256:" + digest,
                browser_download_url = $"https://{(badUrl ? "example.test" : "github.com")}/Zkvr/PaperDesk/releases/download/{tag}/{name}"
            })
        };
    }

    private static void CheckPortable(string folder)
    {
        const string oldVersion = "0.8.0-beta";
        const string newVersion = "0.8.1-beta";
        const string app = "PaperDesk.dll";
        var target = Directory.CreateDirectory(Path.Combine(folder, "portable")).FullName;
        var package = Directory.CreateDirectory(Path.Combine(folder, "package")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(target, "data")).FullName;
        var notePath = Path.Combine(data, "notes.fixture");
        File.WriteAllText(notePath, "local notes");
        File.WriteAllText(Path.Combine(target, "unrelated.txt"), "other files");
        WritePackage(target, oldVersion);
        WritePackage(package, newVersion, "new-library.dll");
        var rolledBack = false;
        try
        {
            PortableUpdate.ApplyFiles(package, target, data, oldVersion, newVersion, Path.Combine(folder, "backup"),
                () => throw new IOException("Synthetic restart failure."));
        }
        catch (IOException) { rolledBack = true; }
        Require(rolledBack && File.ReadAllText(Path.Combine(target, app)) == oldVersion && !File.Exists(Path.Combine(target, "new-library.dll")),
            "A failed restart must restore existing files and remove the new files");
        var restarted = false;
        PortableUpdate.ApplyFiles(package, target, data, oldVersion, newVersion, Path.Combine(folder, "backup2"), () => restarted = true);
        Require(restarted && File.ReadAllText(Path.Combine(target, app)) == newVersion && File.ReadAllText(notePath) == "local notes" &&
            File.ReadAllText(Path.Combine(target, "unrelated.txt")) == "other files", "Apply only package files and restart with the existing data");

        var zip = Path.Combine(folder, "invalid.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry("data/settings.json");
        try
        {
            PortableUpdate.Extract(zip, Path.Combine(folder, "unpack"), CancellationToken.None);
            throw new InvalidOperationException("A portable package must never include profile files.");
        }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS portable update rollback, replacement, and preservation of profile and unrelated files");

        static void WritePackage(string root, string version, params string[] additional)
        {
            string[] files = ["PaperDesk.exe", app, "PaperDesk.deps.json", ..additional];
            foreach (var file in files) File.WriteAllText(Path.Combine(root, file), version);
            File.WriteAllLines(Path.Combine(root, "PaperDesk.portable.txt"), [version, ..files]);
        }
    }

    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Redirect(string address) => new(HttpStatusCode.Redirect)
        { Headers = { Location = new Uri(address) } };

    private static async Task RejectAsync(Func<Task<string>> action)
    {
        try { _ = await action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("The invalid update must be rejected.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(send(request));
    }
}
