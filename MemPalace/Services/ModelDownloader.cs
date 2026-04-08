namespace MemPalace.Core;

/// <summary>
/// Downloads the all-MiniLM-L6-v2 ONNX model files from HuggingFace on first use.
/// Files are cached in <see cref="AppConfig.EmbeddingModelDir"/> and never re-downloaded.
/// </summary>
public static class ModelDownloader
{
    private const string BaseUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/";

    private static readonly (string Remote, string Local)[] RequiredFiles =
    [
        ("onnx/model.onnx", "model.onnx"),
        ("vocab.txt",       "vocab.txt"),
    ];

    /// <summary>
    /// Ensures all required model files exist in <paramref name="modelDir"/>.
    /// Downloads any that are missing and prints progress to stderr.
    /// Returns the path to <c>model.onnx</c>.
    /// </summary>
    public static async Task<string> EnsureModelAsync(
        string modelDir,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(modelDir);

        bool anyMissing = RequiredFiles.Any(f => !File.Exists(Path.Combine(modelDir, f.Local)));
        if (anyMissing)
        {
            Console.Error.WriteLine("[embedding] Downloading all-MiniLM-L6-v2 model from HuggingFace…");
            Console.Error.WriteLine($"[embedding] Destination: {modelDir}");
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        foreach (var (remote, local) in RequiredFiles)
        {
            var dest = Path.Combine(modelDir, local);
            if (File.Exists(dest))
                continue;

            var url = BaseUrl + remote;
            Console.Error.Write($"[embedding]   Downloading {local}… ");

            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            var tmp   = dest + ".tmp";

            await using (var src  = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst  = File.OpenWrite(tmp))
            {
                var buf       = new byte[81_920];
                long received = 0;
                int  read;

                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    received += read;

                    if (total.HasValue)
                    {
                        int pct = (int)(received * 100L / total.Value);
                        Console.Error.Write($"\r[embedding]   Downloading {local}… {pct}%   ");
                    }
                }
            }

            File.Move(tmp, dest, overwrite: true);
            Console.Error.WriteLine($"\r[embedding]   {local} — done ({new FileInfo(dest).Length / 1024 / 1024} MB)   ");
        }

        return Path.Combine(modelDir, "model.onnx");
    }

    /// <summary>Returns true when all required model files already exist locally.</summary>
    public static bool IsAvailable(string modelDir) =>
        RequiredFiles.All(f => File.Exists(Path.Combine(modelDir, f.Local)));
}
