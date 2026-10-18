using LoanUnderwriting.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
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


/// <summary>Azure document store: a private Blob container accessed with the app's managed identity (Storage Blob Data Contributor). Blobs use generated ids.</summary>
public sealed class BlobDocumentStore : IDocumentStore
{
    private readonly BlobContainerClient _container;

    public BlobDocumentStore(string accountUrl, string container)
    {
        _container = new BlobServiceClient(new Uri(accountUrl), new DefaultAzureCredential()).GetBlobContainerClient(container);
    }

    public async Task<DocumentRef> SaveAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        await _container.GetBlobClient(NameFor(id)).UploadAsync(content, overwrite: false, cancellationToken: ct);
        return new DocumentRef(id, Path.GetFileName(fileName));
    }

    public async Task<Stream> OpenAsync(DocumentRef doc, CancellationToken ct = default) =>
        await _container.GetBlobClient(NameFor(doc.Id)).OpenReadAsync(cancellationToken: ct);

    public async Task DeleteAsync(DocumentRef doc, CancellationToken ct = default) =>
        await _container.GetBlobClient(NameFor(doc.Id)).DeleteIfExistsAsync(cancellationToken: ct);

    /// <summary>Round-trips a small blob; used by the startup self-check.</summary>
    public async Task ProbeAsync(CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        var blob = _container.GetBlobClient(".write-test");
        await blob.UploadAsync(new MemoryStream("ok"u8.ToArray()), overwrite: true, cancellationToken: ct);
        await blob.DeleteIfExistsAsync(cancellationToken: ct);
    }

    private static string NameFor(string id)
    {
        if (id.Length == 0 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid document id.");
        return id + ".pdf";
    }
}
