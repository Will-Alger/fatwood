using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ResearchDiscovery.Infrastructure.Embeddings;

/// <summary>
/// Per-vector max-abs int8 quantization for embedding vectors, plus a SIMD
/// integer dot product. At 300k-paper scale the float32 index is ~500 MB —
/// past what the API replica can hold — while int8 is ~125 MB, and ranking
/// quality is insensitive to the quantization noise (verified by the eval
/// harness). Cosine over quantized vectors: both sides are quantized with
/// their own scale s = maxAbs/127, so dot(a, b) ≈ intDot(qa, qb) · sa · sb.
/// </summary>
public static class Int8Quantization
{
    /// <summary>Quantizes a (unit) float vector to sbyte with its dequant scale.</summary>
    public static (sbyte[] Quantized, float Scale) Quantize(ReadOnlySpan<float> vector)
    {
        var maxAbs = 0f;
        foreach (var v in vector)
        {
            var abs = MathF.Abs(v);
            if (abs > maxAbs)
            {
                maxAbs = abs;
            }
        }

        var quantized = new sbyte[vector.Length];
        if (maxAbs == 0)
        {
            return (quantized, 0f);
        }

        var scale = maxAbs / 127f;
        for (var i = 0; i < vector.Length; i++)
        {
            quantized[i] = (sbyte)Math.Clamp(MathF.Round(vector[i] / scale), -127f, 127f);
        }

        return (quantized, scale);
    }

    /// <summary>
    /// Integer dot product of two equal-length sbyte spans. Products fit in
    /// Int16 (|a·b| ≤ 127² = 16129) and a 384-dim sum fits Int32 with huge
    /// margin, so the widen–multiply–widen–accumulate chain is exact.
    /// </summary>
    public static int Dot(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b)
    {
        var length = Math.Min(a.Length, b.Length);
        var i = 0;
        var sum = 0;

        if (Vector256.IsHardwareAccelerated && length >= Vector256<sbyte>.Count)
        {
            var acc = Vector256<int>.Zero;
            var lastBlock = length - Vector256<sbyte>.Count;
            for (; i <= lastBlock; i += Vector256<sbyte>.Count)
            {
                var va = Vector256.Create<sbyte>(a.Slice(i, Vector256<sbyte>.Count));
                var vb = Vector256.Create<sbyte>(b.Slice(i, Vector256<sbyte>.Count));
                var (aLo, aHi) = Vector256.Widen(va);
                var (bLo, bHi) = Vector256.Widen(vb);
                acc += WidenSum(aLo * bLo) + WidenSum(aHi * bHi);
            }

            sum = Vector256.Sum(acc);
        }
        else if (Vector128.IsHardwareAccelerated && length >= Vector128<sbyte>.Count)
        {
            var acc = Vector128<int>.Zero;
            var lastBlock = length - Vector128<sbyte>.Count;
            for (; i <= lastBlock; i += Vector128<sbyte>.Count)
            {
                var va = Vector128.Create<sbyte>(a.Slice(i, Vector128<sbyte>.Count));
                var vb = Vector128.Create<sbyte>(b.Slice(i, Vector128<sbyte>.Count));
                var (aLo, aHi) = Vector128.Widen(va);
                var (bLo, bHi) = Vector128.Widen(vb);
                acc += WidenSum128(aLo * bLo) + WidenSum128(aHi * bHi);
            }

            sum = Vector128.Sum(acc);
        }

        for (; i < length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static Vector256<int> WidenSum(Vector256<short> products)
    {
        var (lo, hi) = Vector256.Widen(products);
        return lo + hi;
    }

    private static Vector128<int> WidenSum128(Vector128<short> products)
    {
        var (lo, hi) = Vector128.Widen(products);
        return lo + hi;
    }

    /// <summary>Reinterprets stored int8 bytes as sbytes without copying.</summary>
    public static ReadOnlySpan<sbyte> AsSbytes(ReadOnlySpan<byte> bytes) =>
        MemoryMarshal.Cast<byte, sbyte>(bytes);
}
