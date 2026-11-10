using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace FloatDesk.Windows.Network;

public static class SteamDnsMessage
{
    public static byte[] CreateQuery(string host, ushort type)
    {
        if (type is not (1 or 28)) throw new ArgumentOutOfRangeException(nameof(type));
        using var buffer = new MemoryStream();
        buffer.Write(new byte[] { 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (var label in host.Split('.'))
        {
            if (label.Length is < 1 or > 63 || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Invalid DNS name.", nameof(host));
            buffer.WriteByte((byte)label.Length); buffer.Write(Encoding.ASCII.GetBytes(label));
        }
        buffer.WriteByte(0); buffer.WriteByte(0); buffer.WriteByte((byte)type); buffer.WriteByte(0); buffer.WriteByte(1);
        return buffer.ToArray();
    }

    public static IPAddress[] ParseAddresses(byte[] response, byte[] query)
    {
        if (query.Length < 17 || response.Length < query.Length || response.Length > 65535 ||
            response[0] != query[0] || response[1] != query[1] || (response[2] & 0x82) != 0x80 ||
            (response[3] & 0x0f) != 0 || response[4] != 0 || response[5] != 1 ||
            !response.AsSpan(12, query.Length - 12).SequenceEqual(query.AsSpan(12))) return [];
        var count = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6));
        if (count > 128) return [];
        var position = query.Length;
        var result = new List<IPAddress>();
        for (var index = 0; index < count; index++)
        {
            if (!SkipName(response, ref position) || position + 10 > response.Length) return [];
            var type = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(position));
            var recordClass = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(position + 2));
            var length = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(position + 8));
            position += 10;
            if (position + length > response.Length) return [];
            if (recordClass == 1 && ((type == 1 && length == 4) || (type == 28 && length == 16)))
                result.Add(new IPAddress(response.AsSpan(position, length)));
            position += length;
        }
        return result.ToArray();
    }

    private static bool SkipName(byte[] bytes, ref int position)
    {
        for (var labels = 0; labels < 128 && position < bytes.Length; labels++)
        {
            var length = bytes[position++];
            if (length == 0) return true;
            if ((length & 0xc0) == 0xc0) { if (position >= bytes.Length) return false; position++; return true; }
            if (length > 63 || position + length > bytes.Length) return false;
            position += length;
        }
        return false;
    }
}
