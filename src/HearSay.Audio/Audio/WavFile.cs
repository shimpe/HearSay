using System.Buffers.Binary;
using System.Text;

namespace HearSay.Audio;

/// <summary>How a multi-channel WAV file is reduced to the mono signal the analysis works on.</summary>
public enum WavChannelMode
{
    /// <summary>Average all channels.</summary>
    Downmix,

    /// <summary>Use a single channel, selected by index.</summary>
    SingleChannel,
}

/// <summary>
/// Minimal RIFF/WAVE reader and writer. Supports uncompressed integer PCM (8, 16, 24, 32 bit) and
/// 32/64-bit IEEE float, including the WAVE_FORMAT_EXTENSIBLE wrapper for those formats.
/// The analysis core works on sample arrays; this helper only exists for convenience.
/// </summary>
public static class WavFile
{
    /// <summary>Reads a WAV file into a mono <see cref="AudioNote"/>.</summary>
    /// <param name="path">Path of the WAV file.</param>
    /// <param name="channelMode">How to reduce multiple channels to mono.</param>
    /// <param name="channelIndex">Channel used when <paramref name="channelMode"/> is <see cref="WavChannelMode.SingleChannel"/>.</param>
    /// <param name="expectedFundamentalHz">Fundamental frequency to attach to the note, when known.</param>
    public static AudioNote Read(
        string path,
        WavChannelMode channelMode = WavChannelMode.Downmix,
        int channelIndex = 0,
        double? expectedFundamentalHz = null)
    {
        using FileStream stream = File.OpenRead(path);
        return Read(stream, channelMode, channelIndex, expectedFundamentalHz);
    }

    /// <summary>Reads WAV data from a stream into a mono <see cref="AudioNote"/>.</summary>
    /// <param name="stream">Stream positioned at the RIFF header.</param>
    /// <param name="channelMode">How to reduce multiple channels to mono.</param>
    /// <param name="channelIndex">Channel used when <paramref name="channelMode"/> is <see cref="WavChannelMode.SingleChannel"/>.</param>
    /// <param name="expectedFundamentalHz">Fundamental frequency to attach to the note, when known.</param>
    /// <exception cref="InvalidDataException">The stream is not a supported WAV file.</exception>
    public static AudioNote Read(
        Stream stream,
        WavChannelMode channelMode = WavChannelMode.Downmix,
        int channelIndex = 0,
        double? expectedFundamentalHz = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        if (ReadTag(reader) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.ReadUInt32();
        if (ReadTag(reader) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        int formatTag = 0, channels = 0, sampleRate = 0, bitsPerSample = 0;
        bool haveFormat = false;
        byte[]? data = null;

        while (data is null)
        {
            string tag;
            try
            {
                tag = ReadTag(reader);
            }
            catch (EndOfStreamException)
            {
                break;
            }

            uint size = reader.ReadUInt32();
            if (tag == "fmt ")
            {
                byte[] fmt = reader.ReadBytes((int)size);
                if (fmt.Length < 16)
                {
                    throw new InvalidDataException("Truncated fmt chunk.");
                }

                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                if (formatTag == 0xFFFE && fmt.Length >= 26)
                {
                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
                }

                haveFormat = true;
                if ((size & 1) == 1)
                {
                    SkipBytes(reader, 1);
                }
            }
            else if (tag == "data")
            {
                long remaining = stream.CanSeek ? stream.Length - stream.Position : size;
                data = reader.ReadBytes((int)Math.Min(size, remaining));
            }
            else
            {
                SkipBytes(reader, size + (size & 1));
            }
        }

        if (!haveFormat || data is null)
        {
            throw new InvalidDataException("WAV file lacks a fmt or data chunk.");
        }

        if (channels < 1 || sampleRate < 1)
        {
            throw new InvalidDataException("WAV file has an invalid channel count or sample rate.");
        }

        if (channelMode == WavChannelMode.SingleChannel && (channelIndex < 0 || channelIndex >= channels))
        {
            throw new ArgumentOutOfRangeException(nameof(channelIndex), $"The file has {channels} channel(s).");
        }

        int bytesPerSample = bitsPerSample / 8;
        bool isFloat = formatTag == 3;
        bool supportedPcm = formatTag == 1 && bytesPerSample is >= 1 and <= 4;
        bool supportedFloat = isFloat && bytesPerSample is 4 or 8;
        if (!supportedPcm && !supportedFloat)
        {
            throw new InvalidDataException($"Unsupported WAV encoding (format tag {formatTag}, {bitsPerSample} bit).");
        }

        int frameCount = data.Length / (bytesPerSample * channels);
        var samples = new float[frameCount];
        ReadOnlySpan<byte> bytes = data;
        for (int frame = 0; frame < frameCount; frame++)
        {
            int frameOffset = frame * channels * bytesPerSample;
            if (channelMode == WavChannelMode.SingleChannel)
            {
                samples[frame] = Decode(bytes.Slice(frameOffset + (channelIndex * bytesPerSample), bytesPerSample), isFloat);
            }
            else
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                {
                    sum += Decode(bytes.Slice(frameOffset + (c * bytesPerSample), bytesPerSample), isFloat);
                }

                samples[frame] = sum / channels;
            }
        }

        return new AudioNote(samples, sampleRate, expectedFundamentalHz);
    }

    /// <summary>Writes mono samples as a 16-bit PCM WAV file. Samples are clipped to -1..+1.</summary>
    /// <param name="path">Destination path.</param>
    /// <param name="samples">Mono samples.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    public static void Write(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        using FileStream stream = File.Create(path);
        Write(stream, samples, sampleRate);
    }

    /// <summary>Writes mono samples as 16-bit PCM WAV data. Samples are clipped to -1..+1.</summary>
    /// <param name="stream">Destination stream.</param>
    /// <param name="samples">Mono samples.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    public static void Write(Stream stream, ReadOnlySpan<float> samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        int dataBytes = samples.Length * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        foreach (float sample in samples)
        {
            float clipped = float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0f;
            writer.Write((short)MathF.Round(clipped * short.MaxValue));
        }
    }

    private static float Decode(ReadOnlySpan<byte> sample, bool isFloat)
    {
        if (isFloat)
        {
            return sample.Length == 4
                ? BinaryPrimitives.ReadSingleLittleEndian(sample)
                : (float)BinaryPrimitives.ReadDoubleLittleEndian(sample);
        }

        return sample.Length switch
        {
            1 => (sample[0] - 128) / 128f,
            2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
            3 => (sample[0] | (sample[1] << 8) | ((sbyte)sample[2] << 16)) / 8388608f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f,
        };
    }

    private static string ReadTag(BinaryReader reader)
    {
        byte[] tag = reader.ReadBytes(4);
        if (tag.Length < 4)
        {
            throw new EndOfStreamException();
        }

        return Encoding.ASCII.GetString(tag);
    }

    private static void SkipBytes(BinaryReader reader, long count)
    {
        Stream stream = reader.BaseStream;
        if (stream.CanSeek)
        {
            stream.Seek(Math.Min(count, stream.Length - stream.Position), SeekOrigin.Current);
        }
        else
        {
            reader.ReadBytes((int)count);
        }
    }
}
