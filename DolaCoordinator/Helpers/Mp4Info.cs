using System;
using System.IO;

namespace DolaCoordinator.Helpers;

/// <summary>Đọc thời lượng THỰC TẾ của file MP4 từ hộp "mvhd" (không cần ffmpeg).</summary>
public static class Mp4Info
{
    /// <summary>Thời lượng (giây) của file MP4; null nếu không đọc được.</summary>
    public static double? GetDurationSeconds(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            return FindDuration(br, 0, fs.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    // Duyệt các hộp (box) trong [start, end): moov chứa mvhd; các hộp khác thì nhảy qua
    private static double? FindDuration(BinaryReader br, long start, long end)
    {
        var pos = start;
        while (pos + 8 <= end)
        {
            br.BaseStream.Position = pos;
            long size = ReadUInt32(br);
            var type = new string(br.ReadChars(4));
            long header = 8;
            if (size == 1) { size = (long)ReadUInt64(br); header = 16; }   // kích thước 64-bit
            else if (size == 0) size = end - pos;                           // hộp cuối: tới hết file
            if (size < header) return null;

            if (type == "moov")
            {
                var inner = FindDuration(br, pos + header, Math.Min(end, pos + size));
                if (inner != null) return inner;
            }
            else if (type == "mvhd")
            {
                br.BaseStream.Position = pos + header;
                var version = br.ReadByte();
                br.ReadBytes(3); // flags
                double timescale, duration;
                if (version == 1)
                {
                    br.ReadBytes(16); // creation + modification (64-bit mỗi cái)
                    timescale = ReadUInt32(br);
                    duration = ReadUInt64(br);
                }
                else
                {
                    br.ReadBytes(8);  // creation + modification (32-bit mỗi cái)
                    timescale = ReadUInt32(br);
                    duration = ReadUInt32(br);
                }
                return timescale > 0 ? duration / timescale : null;
            }
            pos += size;
        }
        return null;
    }

    private static uint ReadUInt32(BinaryReader br)
    {
        var b = br.ReadBytes(4);
        if (b.Length < 4) throw new EndOfStreamException();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static ulong ReadUInt64(BinaryReader br) => ((ulong)ReadUInt32(br) << 32) | ReadUInt32(br);
}
