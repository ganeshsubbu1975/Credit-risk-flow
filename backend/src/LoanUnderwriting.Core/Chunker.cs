using System.Security.Cryptography;
using System.Text;

namespace LoanUnderwriting.Core;

public sealed record TextChunk(int Page, int Index, string Text, string Hash);

/// <summary>
/// Line-aware chunker. Each non-empty line (including table rows) is kept whole; a chunk holds up to
/// <c>maxChars</c> characters and overlaps the next one by its last line so context is not cut mid-table.
/// </summary>
public static class Chunker
{
    public static IReadOnlyList<TextChunk> Split(PdfContent pdf, int maxChars = 500)
    {
        var result = new List<TextChunk>();
        var index = 0;
        foreach (var page in pdf.Pages)
        {
            var lines = page.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var buf = new List<string>();
            var fresh = 0; // lines added since the last flush (the overlap line does not count)
            var len = 0;

            void Flush()
            {
                var text = string.Join("\n", buf);
                result.Add(new TextChunk(page.Number, index++, text, Hash(text)));
            }

            foreach (var line in lines)
            {
                if (len + line.Length > maxChars && fresh > 0)
                {
                    Flush();
                    var last = buf[^1];
                    buf = new List<string> { last };
                    fresh = 0;
                    len = last.Length;
                }
                buf.Add(line);
                fresh++;
                len += line.Length + 1;
            }
            if (fresh > 0) Flush();
        }
        return result;
    }

    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
