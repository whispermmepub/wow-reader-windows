using System.IO;
using System.Security.Cryptography;
using WowReader.Windows.Models;

namespace WowReader.Windows.Services;

public sealed class PdfBookService
{
    private const long MaxPdfBytes = 1_500_000_000L;
    private const int BufferSize = 128 * 1024;

    public Task<PdfBook> ImportAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => Import(sourcePath, cancellationToken), cancellationToken);

    private static PdfBook Import(string sourcePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath) || !string.Equals(Path.GetExtension(sourcePath), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a readable .pdf file.");

        AppPaths.EnsureCreated();
        var temporary = Path.Combine(AppPaths.Pdfs, Guid.NewGuid().ToString("N") + ".import.tmp");
        try
        {
            using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
            if (input.Length < 8 || input.Length > MaxPdfBytes)
                throw new InvalidDataException("This PDF is empty or exceeds the 1.5 GB safety limit.");

            var header = new byte[(int)Math.Min(1024L, input.Length)];
            var headerRead = 0;
            while (headerRead < header.Length)
            {
                var read = input.Read(header, headerRead, header.Length - headerRead);
                if (read == 0) break;
                headerRead += read;
            }
            if (headerRead < 5 || header.AsSpan(0, headerRead).IndexOf("%PDF-"u8) < 0)
                throw new InvalidDataException("The selected file does not contain a PDF header.");
            input.Position = 0;

            string id;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[BufferSize];
                long copiedBytes = 0;
                int copied;
                while ((copied = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    copiedBytes += copied;
                    if (copiedBytes > MaxPdfBytes) throw new InvalidDataException("This PDF exceeds the 1.5 GB safety limit.");
                    output.Write(buffer, 0, copied);
                    hash.AppendData(buffer, 0, copied);
                }
                if (copiedBytes < 8) throw new InvalidDataException("This PDF is empty or incomplete.");
                output.Flush(flushToDisk: true);
                cancellationToken.ThrowIfCancellationRequested();
                id = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            var localPath = Path.Combine(AppPaths.Pdfs, id + ".pdf");
            if (File.Exists(localPath))
            {
                string existingId;
                using (var existing = File.OpenRead(localPath))
                    existingId = Convert.ToHexStringLower(SHA256.HashData(existing));
                if (!string.Equals(existingId, id, StringComparison.Ordinal))
                    File.Move(temporary, localPath, overwrite: true);
            }
            else
            {
                try { File.Move(temporary, localPath); }
                catch (IOException) when (File.Exists(localPath)) { }
            }
            return new PdfBook(id, Path.GetFileNameWithoutExtension(sourcePath), localPath);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
