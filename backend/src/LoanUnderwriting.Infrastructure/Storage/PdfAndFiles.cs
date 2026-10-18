using LoanUnderwriting.Core;
using UglyToad.PdfPig;

namespace LoanUnderwriting.Infrastructure.Storage;

/// <summary>Reads text from digital PDFs with PdfPig (no OCR: scanned PDFs have no text layer and are rejected).</summary>
public sealed class PdfPigReader : IPdfReader
{
    public PdfContent Read(Stream pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var pages = new List<PdfPage>();
        foreach (var page in doc.GetPages())
        {
            // Rebuild lines from word positions so table rows stay on one line.
            var words = page.GetWords().OrderByDescending(w => Math.Round(w.BoundingBox.Bottom / 4)).ThenBy(w => w.BoundingBox.Left).ToList();
            var lines = new List<string>();
            double? lastKey = null;
            var current = new List<string>();
            foreach (var w in words)
            {
                var key = Math.Round(w.BoundingBox.Bottom / 4);
                if (lastKey is not null && key != lastKey) { lines.Add(string.Join(" ", current)); current.Clear(); }
                current.Add(w.Text);
                lastKey = key;
            }
            if (current.Count > 0) lines.Add(string.Join(" ", current));
            pages.Add(new PdfPage(page.Number, string.Join("\n", lines)));
        }
        return new PdfContent(pages);
    }
}

/// <summary>On-prem document store: a restricted, encrypted folder or file share. Files are stored under generated ids.</summary>
public sealed class FileDocumentStore : IDocumentStore
{
    private readonly string _root;

    public FileDocumentStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task<DocumentRef> SaveAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var fs = File.Create(PathFor(id));
        await content.CopyToAsync(fs, ct);
        return new DocumentRef(id, Path.GetFileName(fileName));
    }

    public Task<Stream> OpenAsync(DocumentRef doc, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new FileStream(PathFor(doc.Id), FileMode.Open, FileAccess.Read, FileShare.Read));

    public Task DeleteAsync(DocumentRef doc, CancellationToken ct = default)
    {
        var p = PathFor(doc.Id);
        if (File.Exists(p)) File.Delete(p);
        return Task.CompletedTask;
    }

    // The id is a generated hex string, never user input, but the path is still validated to stay inside the root.
    private string PathFor(string id)
    {
        if (id.Length == 0 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid document id.");
        return Path.Combine(_root, id + ".pdf");
    }
}
