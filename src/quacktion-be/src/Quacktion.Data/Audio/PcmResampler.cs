using System.Buffers.Binary;

namespace Quacktion.Data.Audio;

/// <summary>Streaming 24 kHz to 16 kHz PCM16, using a 63-tap windowed-sinc low-pass
/// at 7.2 kHz before rational decimation. State and fractional phase survive chunk boundaries.</summary>
public sealed class PcmResampler
{
    private const int Taps = 63;
    private readonly double[] samples = new double[Taps + 4];
    private readonly double[][] coefficients = [Kernel(0), Kernel(0.5)];
    private long inputIndex = -1;
    private long nextOutputTwice;

    public void Reset() { Array.Clear(samples); inputIndex = -1; nextOutputTwice = 0; }

    public byte[] Convert(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % 2 != 0) throw new ArgumentException("PCM16 frames must contain whole samples.", nameof(pcm));
        using var output = new MemoryStream();
        Span<byte> encoded = stackalloc byte[2];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            inputIndex++;
            samples[inputIndex % samples.Length] = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]);
            while (nextOutputTwice / 2 <= inputIndex)
            {
                var end = nextOutputTwice / 2;
                var kernel = coefficients[nextOutputTwice % 2];
                double value = 0;
                for (var tap = 0; tap < Taps; tap++)
                {
                    var index = end - tap;
                    if (index >= 0) value += samples[index % samples.Length] * kernel[tap];
                }
                BinaryPrimitives.WriteInt16LittleEndian(encoded, (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
                output.Write(encoded);
                nextOutputTwice += 3; // 1.5 source samples per output sample.
            }
        }
        return output.ToArray();
    }

    private static double[] Kernel(double fraction)
    {
        var result = new double[Taps];
        const double cutoff = 7200.0 / 24000;
        for (var i = 0; i < Taps; i++)
        {
            var x = i - (Taps - 1) / 2.0 + fraction;
            var sinc = Math.Abs(x) < 1e-10 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            result[i] = sinc * (0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (Taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (Taps - 1)));
        }
        var sum = result.Sum();
        return result.Select(x => x / sum).ToArray();
    }
}
