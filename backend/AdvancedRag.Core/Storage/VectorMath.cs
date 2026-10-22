namespace AdvancedRag.Core.Storage;

/// <summary>
/// An embedding places a text as a point in space; texts with similar meaning point in similar directions.
/// Cosine similarity measures the angle: cos(a, b) = (a · b) / (|a| × |b|). When both vectors are scaled to length 1
/// ("normalized") it is just the dot product, so pipelines normalize once, before storing.
/// </summary>
public static class VectorMath
{
    public static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) throw new ArgumentException($"Vector lengths differ ({a.Length} vs {b.Length}).");

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    /// <summary>Returns a copy scaled to length 1 (the zero vector is returned unchanged).</summary>
    public static float[] Normalize(ReadOnlySpan<float> vector)
    {
        double sum = 0;
        foreach (var v in vector) sum += v * v;
        var length = Math.Sqrt(sum);

        var result = vector.ToArray();
        if (length == 0) return result;
        for (var i = 0; i < result.Length; i++) result[i] = (float)(result[i] / length);
        return result;
    }
}
