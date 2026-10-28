using System.Security.Cryptography;
using System.Text;

namespace Apex.Site.Api.Services;

/// <summary>
/// A stable, salted identifier for one caller within one day.
/// <para>
/// Enough to rate-limit and to tell one visitor's six clicks from six
/// visitors' one, without keeping an address. The salt rolls with the date,
/// so yesterday's identifiers cannot be re-derived even from stored rows.
/// Copied from ToxFinder's Upkeep.Query.Hosting.CallerKey.
/// </para>
/// </summary>
public static class CallerKey
{
    public static string For(HttpContext http)
    {
        var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var day = DateTime.UtcNow.ToString("yyyyMMdd");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"apex|{day}|{address}"));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
