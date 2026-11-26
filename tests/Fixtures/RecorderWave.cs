using System.Buffers.Binary;

internal static class RecorderWave
{
    internal static byte[] Create(int length, int frequencyHz = 0)
    {
        var data = new byte[length];
        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), length - 8);
        "WAVEfmt "u8.CopyTo(data.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), 8000);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), 16000);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(34), 16);
        "data"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), length - 44);
        if (frequencyHz > 0)
        {
            for (int offset = 44, sample = 0; offset + 1 < length; offset += 2, sample++)
            {
                var envelope = Math.Min(1.0, Math.Min(sample, (length - offset) / 2 - 1) / 160.0);
                var amplitude = 6000 * envelope * Math.Sin(2 * Math.PI * frequencyHz * sample / 8000);
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset), (short)amplitude);
            }
        }
        return data;
    }
}
