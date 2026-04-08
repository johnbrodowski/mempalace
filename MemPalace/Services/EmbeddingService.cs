using FastBertTokenizer;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MemPalace.Core;

/// <summary>
/// Generates 384-dim sentence embeddings using the all-MiniLM-L6-v2 ONNX model.
/// Thread-safe: <see cref="InferenceSession.Run"/> is documented as thread-safe;
/// the tokenizer is stateless after loading.
/// </summary>
public sealed class EmbeddingService : IDisposable
{
    private const int MaxTokens    = 512;
    private const int EmbeddingDim = 384;

    private readonly InferenceSession _session;
    private readonly BertTokenizer    _tokenizer;

    // Output node name: some ONNX exports emit "sentence_embedding" (already pooled+normalised),
    // others emit "last_hidden_state" which we mean-pool ourselves.
    private readonly bool _hasTokenTypeIds;
    private readonly bool _hasSentenceEmbeddingOutput;

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Try to create the service from files in <paramref name="modelDir"/>.
    /// Returns <c>null</c> if the model or vocab files are not present —
    /// callers should fall back to BM25-only search in that case.
    /// </summary>
    public static async Task<EmbeddingService?> TryCreateAsync(string modelDir)
    {
        var modelPath = Path.Combine(modelDir, "model.onnx");
        var vocabPath = Path.Combine(modelDir, "vocab.txt");

        if (!File.Exists(modelPath) || !File.Exists(vocabPath))
            return null;

        try
        {
            var tokenizer = new BertTokenizer();
            await tokenizer.LoadVocabularyAsync(
                vocabPath,
                convertInputToLowercase: true);

            return new EmbeddingService(modelPath, tokenizer);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[embedding] Failed to load model: {ex.Message}");
            return null;
        }
    }

    private EmbeddingService(string modelPath, BertTokenizer tokenizer)
    {
        var opts = new SessionOptions();
        opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;

        _session   = new InferenceSession(modelPath, opts);
        _tokenizer = tokenizer;

        _hasTokenTypeIds            = _session.InputMetadata.ContainsKey("token_type_ids");
        _hasSentenceEmbeddingOutput = _session.OutputMetadata.ContainsKey("sentence_embedding");

        Console.Error.WriteLine(
            $"[embedding] Loaded all-MiniLM-L6-v2  " +
            $"inputs={string.Join(",", _session.InputMetadata.Keys)}  " +
            $"outputs={string.Join(",", _session.OutputMetadata.Keys)}");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Embed a single string and return an L2-normalised 384-dim float vector.
    /// </summary>
    public float[] Embed(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new float[EmbeddingDim];

        var (inputIds, attentionMask, tokenTypeIds) = _tokenizer.Encode(text, MaxTokens);
        return RunInference(
            [inputIds],
            [attentionMask],
            _hasTokenTypeIds ? [tokenTypeIds] : null)[0];
    }

    /// <summary>
    /// Embed multiple strings using the batch ONNX call.
    /// All sequences are padded to <c>MaxTokens</c> by the tokenizer.
    /// </summary>
    public IReadOnlyList<float[]> EmbedBatch(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();

        int batchSize = texts.Count;
        int flatLen   = batchSize * MaxTokens;

        var inputIdsMem  = new Memory<long>(new long[flatLen]);
        var attentionMem = new Memory<long>(new long[flatLen]);
        var tokenTypeMem = new Memory<long>(new long[flatLen]);

        _tokenizer.Encode(
            new ReadOnlyMemory<string>(texts.ToArray()),
            inputIdsMem,
            attentionMem,
            tokenTypeMem,
            MaxTokens);

        // Slice the flat buffers into per-item views
        var inputIdSlices  = Enumerable.Range(0, batchSize)
            .Select(i => inputIdsMem.Slice(i * MaxTokens, MaxTokens)).ToArray();
        var attnSlices = Enumerable.Range(0, batchSize)
            .Select(i => attentionMem.Slice(i * MaxTokens, MaxTokens)).ToArray();
        var typeSlices = _hasTokenTypeIds
            ? Enumerable.Range(0, batchSize)
                .Select(i => tokenTypeMem.Slice(i * MaxTokens, MaxTokens)).ToArray()
            : null;

        return RunInference(inputIdSlices, attnSlices, typeSlices);
    }

    /// <summary>
    /// Cosine similarity between two L2-normalised vectors (= dot product).
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0f;
        for (int i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot;
    }

    public void Dispose() => _session.Dispose();

    // ── Internals ─────────────────────────────────────────────────────────────

    /// <summary>Run ONNX inference on a batch; return one normalised vector per item.</summary>
    private float[][] RunInference(
        Memory<long>[]  batchIds,
        Memory<long>[]  batchMasks,
        Memory<long>[]? batchTypeIds)
    {
        int batchSize = batchIds.Length;
        int seqLen    = batchIds[0].Length;   // all slices are same length (padded)

        var inputIdsTensor  = new DenseTensor<long>(new[] { batchSize, seqLen });
        var attentionTensor = new DenseTensor<long>(new[] { batchSize, seqLen });
        DenseTensor<long>? tokenTypeTensor = batchTypeIds is not null
            ? new DenseTensor<long>(new[] { batchSize, seqLen }) : null;

        for (int b = 0; b < batchSize; b++)
        {
            var ids  = batchIds[b].Span;
            var mask = batchMasks[b].Span;
            for (int s = 0; s < seqLen; s++)
            {
                inputIdsTensor[b, s]  = ids[s];
                attentionTensor[b, s] = mask[s];
                if (tokenTypeTensor is not null)
                    tokenTypeTensor[b, s] = batchTypeIds![b].Span[s];
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids",      inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionTensor),
        };
        if (tokenTypeTensor is not null)
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeTensor));

        using var outputs = _session.Run(inputs);
        var results = new float[batchSize][];

        if (_hasSentenceEmbeddingOutput)
        {
            // Model already outputs pooled + normalised sentence_embedding [batch, dim]
            var sentEmb = outputs.First(o => o.Name == "sentence_embedding").AsTensor<float>();
            for (int b = 0; b < batchSize; b++)
            {
                var vec = new float[EmbeddingDim];
                for (int d = 0; d < EmbeddingDim; d++)
                    vec[d] = sentEmb[b, d];
                results[b] = vec;
            }
        }
        else
        {
            // last_hidden_state [batch, seq, dim] — mean-pool over non-padding tokens, then L2-normalise
            var hidden = outputs.First(o => o.Name == "last_hidden_state").AsTensor<float>();
            for (int b = 0; b < batchSize; b++)
            {
                var vec  = new float[EmbeddingDim];
                var mask = batchMasks[b].Span;

                int realLen = 0;
                for (int s = 0; s < seqLen; s++)
                    if (mask[s] != 0) realLen++;
                if (realLen == 0) realLen = 1;

                for (int s = 0; s < seqLen; s++)
                {
                    if (mask[s] == 0) continue;
                    for (int d = 0; d < EmbeddingDim; d++)
                        vec[d] += hidden[b, s, d];
                }

                for (int d = 0; d < EmbeddingDim; d++)
                    vec[d] /= realLen;

                // L2 normalise
                float norm = MathF.Sqrt(vec.Sum(x => x * x));
                if (norm > 1e-9f)
                    for (int d = 0; d < EmbeddingDim; d++)
                        vec[d] /= norm;

                results[b] = vec;
            }
        }

        return results;
    }
}
