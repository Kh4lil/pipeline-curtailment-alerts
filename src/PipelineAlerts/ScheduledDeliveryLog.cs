using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PipelineAlerts;

internal sealed class ScheduledDeliveryLog : IDisposable
{
    private const int HashLength = 64;

    private readonly FileStream stream;
    private readonly HashSet<string> delivered;

    internal ScheduledDeliveryLog(string path)
    {
        path = Path.GetFullPath(path);
        if (Path.GetDirectoryName(path) is { } directory)
            Directory.CreateDirectory(directory);

        stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        try
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var rows = reader.ReadToEnd().Split('\n');
            var complete = rows[..^1];
            var tail = rows[^1];
            if (complete.Any(row => row.Length != HashLength || !row.All(Uri.IsHexDigit))
                || tail.Length > HashLength || !tail.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("Scheduled delivery state contains an invalid hash record.");
            }

            delivered = complete.ToHashSet(StringComparer.OrdinalIgnoreCase);

            // A newline commits each ASCII record. A partial final append can be retried.
            if (tail.Length > 0)
                stream.SetLength(stream.Length - tail.Length);

            stream.Position = stream.Length;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal bool IsDelivered(Notice notice) => delivered.Contains(RevisionKey(notice));

    internal void RecordDelivered(Notice notice)
    {
        var key = RevisionKey(notice);
        if (delivered.Contains(key))
            return;

        stream.Write(Encoding.ASCII.GetBytes(key + "\n"));
        stream.Flush(flushToDisk: true);
        delivered.Add(key);
    }

    public void Dispose() => stream.Dispose();

    private static string RevisionKey(Notice notice)
    {
        var revision = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Link = notice.Link.AbsoluteUri,
            PostedAt = notice.PostedAt.UtcTicks,
            Pipeline = Normalize(notice.Pipeline),
            Type = Normalize(notice.Type),
            Title = Normalize(notice.Title),
            Location = Normalize(notice.Location ?? ""),
            Body = Normalize(notice.Body)
        });
        return Convert.ToHexString(SHA256.HashData(revision));
    }

    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
