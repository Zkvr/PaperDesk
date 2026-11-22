using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaperDesk.Desktop.Services;

internal static class OfflineProfile
{
    internal static (string Name, Guid Owner) Resolve(string root, string? saved, string? selected)
    {
        if (Existing(selected) is { } profile) return profile;
        if (selected == null && saved != null)
        {
            using var document = JsonDocument.Parse(saved);
            var settings = document.RootElement;
            if (settings.TryGetProperty("last_user_id", out var user) && user.ValueKind == JsonValueKind.String &&
                user.TryGetGuid(out var id) && id != Guid.Empty)
            {
                var prefix = "local";
                if (settings.TryGetProperty("supabase_url", out var address) && address.ValueKind == JsonValueKind.String &&
                    address.GetString() is { Length: > 0 } url)
                {
                    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url.TrimEnd('/').ToLowerInvariant()));
                    prefix = Convert.ToHexString(hash)[..16];
                }
                if (Existing($"{prefix}-{id:D}") is { } legacy) return legacy;
            }
        }
        return ("local", Guid.Empty);

        (string, Guid)? Existing(string? name)
        {
            if (name == null) return null;
            var separator = name.IndexOf('-');
            if (separator < 0 || !Guid.TryParseExact(name[(separator + 1)..], "D", out var owner) || owner == Guid.Empty)
                return null;
            var prefix = name[..separator];
            if (prefix != "local" && (prefix.Length != 16 || !prefix.All(Uri.IsHexDigit))) return null;
            return File.Exists(Path.Combine(root, "profiles", name, "notes.db")) ? (name, owner) : null;
        }
    }
}
