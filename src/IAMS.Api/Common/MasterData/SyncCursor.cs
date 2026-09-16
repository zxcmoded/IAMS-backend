using System.Text;

namespace IAMS.Api.Common.MasterData;

/// <summary>
/// Opaque keyset cursor for master-data sync pagination. Encodes the last-seen row's
/// <c>SyncCursorUtc</c> (COALESCE(UpdatedAtUtc, CreatedAtUtc)) and <c>Id</c> as a base64url string of
/// <c>"{ticksUtc:D19}|{id}"</c>. The same value serves both bulk pagination and incremental "since"
/// sync — an absent/empty cursor decodes to the start-of-world sentinel <c>(DateTime.MinValue,
/// Guid.Empty)</c>, i.e. a fresh/initial full sync. The wire format is deliberately opaque so the
/// backend could later swap the timestamp basis (e.g. a monotonic revision) without a contract change.
/// </summary>
public static class SyncCursor
{
    /// <summary>
    /// Name of the stored generated keyset column (COALESCE(UpdatedAtUtc, CreatedAtUtc)) present on every
    /// hierarchy level. Single source of truth shared by the EF mapping and the listing handlers, which
    /// order/filter on it via <c>EF.Property&lt;DateTime&gt;(x, SyncCursor.ColumnName)</c>.
    /// </summary>
    public const string ColumnName = "SyncCursorUtc";

    public static string Encode(DateTime tsUtc, Guid id)
    {
        var raw = $"{tsUtc.Ticks:D19}|{id}";
        return ToBase64Url(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>
    /// Decodes a cursor. Null/empty → <c>(DateTime.MinValue, Guid.Empty)</c> returning <c>true</c>
    /// (the valid "start of the world" request). A non-empty but malformed cursor returns <c>false</c>
    /// with the same sentinel outputs, so callers can reject it as a validation error.
    /// </summary>
    public static bool TryDecode(string? cursor, out DateTime tsUtc, out Guid id)
    {
        tsUtc = DateTime.MinValue;
        id = Guid.Empty;

        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        try
        {
            var raw = Encoding.UTF8.GetString(FromBase64Url(cursor));
            var sep = raw.IndexOf('|');
            if (sep <= 0)
            {
                return false;
            }

            if (!long.TryParse(raw.AsSpan(0, sep), out var ticks) ||
                !Guid.TryParse(raw.AsSpan(sep + 1), out var parsedId) ||
                ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            {
                return false;
            }

            tsUtc = new DateTime(ticks, DateTimeKind.Utc);
            id = parsedId;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return (s.Length % 4) switch
        {
            2 => Convert.FromBase64String(s + "=="),
            3 => Convert.FromBase64String(s + "="),
            0 => Convert.FromBase64String(s),
            _ => throw new FormatException("Invalid base64url length.")
        };
    }
}
