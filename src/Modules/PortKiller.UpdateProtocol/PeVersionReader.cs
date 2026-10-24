using System.Buffers.Binary;
using System.Text;

namespace PortKiller.Updates;

/// <summary>Reads the Windows VERSIONINFO resource without using platform APIs.</summary>
internal static class PeVersionReader
{
    private const uint ResourceVersionType = 16;
    private const uint FixedFileInfoSignature = 0xFEEF04BD;

    public static string? ReadFileVersion(Stream input)
    {
        if (!input.CanSeek) throw new InvalidDataException("PE 输入流必须可定位。");
        long length = input.Length;
        if (length < 128 || length > 512L * 1024 * 1024) throw new InvalidDataException("EXE 大小不合法。");
        uint peOffset = ReadUInt32(input, 0x3c);
        if (peOffset > length - 24 || ReadUInt16(input, 0) != 0x5a4d || ReadUInt32(input, peOffset) != 0x00004550)
            throw new InvalidDataException("不是有效的 Windows PE 文件。");

        ushort sectionCount = ReadUInt16(input, peOffset + 6);
        ushort optionalSize = ReadUInt16(input, peOffset + 20);
        long optionalOffset = peOffset + 24;
        if (sectionCount is 0 or > 96 || optionalSize < 96 || optionalOffset + optionalSize > length)
            throw new InvalidDataException("PE 头不完整。");
        ushort magic = ReadUInt16(input, optionalOffset);
        int directoryOffset = magic switch { 0x10b => 96, 0x20b => 112, _ => throw new InvalidDataException("PE 类型不支持。") };
        if (optionalSize < directoryOffset + 8 * 3) throw new InvalidDataException("PE 目录不完整。");
        uint resourceRva = ReadUInt32(input, optionalOffset + directoryOffset + 8 * 2);
        uint resourceSize = ReadUInt32(input, optionalOffset + directoryOffset + 8 * 2 + 4);
        if (resourceRva == 0 || resourceSize < 16) throw new InvalidDataException("PE 缺少版本资源。");

        long sectionOffset = optionalOffset + optionalSize;
        if (sectionOffset + sectionCount * 40L > length) throw new InvalidDataException("PE 节表不完整。");
        var sections = new Section[sectionCount];
        for (int index = 0; index < sections.Length; index++)
        {
            long current = sectionOffset + index * 40L;
            uint virtualSize = ReadUInt32(input, current + 8);
            uint virtualAddress = ReadUInt32(input, current + 12);
            uint rawSize = ReadUInt32(input, current + 16);
            uint rawOffset = ReadUInt32(input, current + 20);
            if (rawOffset > length || rawSize > length - rawOffset) throw new InvalidDataException("PE 节超出文件范围。");
            sections[index] = new(virtualAddress, Math.Max(virtualSize, rawSize), rawOffset, rawSize);
        }

        long resourceOffset = MapRva(resourceRva, sections, length);
        var tree = new ResourceTree(input, resourceOffset, resourceSize);
        long dataEntry = tree.FindVersionData(0, 0);
        uint dataRva = tree.ReadUInt32(dataEntry);
        uint dataLength = tree.ReadUInt32(dataEntry + 4);
        if (dataLength == 0 || dataLength > 16 * 1024 * 1024) throw new InvalidDataException("版本资源大小不合法。");
        long dataOffset = MapRva(dataRva, sections, length);
        if (dataLength > length - dataOffset) throw new InvalidDataException("版本资源超出文件范围。");
        return ParseVersionResource(input, dataOffset, dataLength);
    }

    private static string? ParseVersionResource(Stream input, long offset, uint length)
    {
        byte[] bytes = new byte[(int)length];
        input.Position = offset;
        int read = 0;
        while (read < bytes.Length)
        {
            int count = input.Read(bytes, read, bytes.Length - read);
            if (count == 0) throw new InvalidDataException("版本资源读取不完整。");
            read += count;
        }
        return FindFixedFileVersion(bytes, 0, bytes.Length);
    }

    private static string? FindFixedFileVersion(byte[] bytes, int start, int limit)
    {
        if (start < 0 || start + 6 > limit) return null;
        ushort blockLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(start, 2));
        ushort valueLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(start + 2, 2));
        if (blockLength < 6 || start + blockLength > limit) return null;
        int cursor = start + 6;
        while (cursor + 2 <= start + blockLength)
        {
            ushort character = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor, 2));
            cursor += 2;
            if (character == 0) break;
        }
        int valueOffset = Align4(cursor);
        if (valueLength >= 52 && valueOffset + 52 <= start + blockLength
            && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(valueOffset, 4)) == FixedFileInfoSignature)
        {
            uint majorMinor = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(valueOffset + 8, 4));
            uint buildRevision = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(valueOffset + 12, 4));
            return $"{majorMinor >> 16}.{majorMinor & 0xffff}.{buildRevision >> 16}.{buildRevision & 0xffff}";
        }
        int childOffset = Align4(valueOffset + valueLength);
        int blockEnd = start + blockLength;
        while (childOffset + 6 <= blockEnd)
        {
            ushort childLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(childOffset, 2));
            if (childLength < 6 || childOffset + childLength > blockEnd) break;
            string? result = FindFixedFileVersion(bytes, childOffset, blockEnd);
            if (result != null) return result;
            childOffset += childLength;
            childOffset = Align4(childOffset);
        }
        return null;
    }

    private static int Align4(int value) => (value + 3) & ~3;

    private static long MapRva(uint rva, Section[] sections, long fileLength)
    {
        foreach (Section section in sections)
        {
            ulong end = (ulong)section.VirtualAddress + section.MappedSize;
            if (rva < section.VirtualAddress || rva >= end) continue;
            ulong offset = (ulong)section.RawOffset + rva - section.VirtualAddress;
            if (offset >= section.RawOffset && offset < (ulong)section.RawOffset + section.RawSize && offset < (ulong)fileLength)
                return (long)offset;
        }
        throw new InvalidDataException("PE RVA 不在文件节范围内。");
    }

    private static ushort ReadUInt16(Stream input, long offset)
    {
        Span<byte> bytes = stackalloc byte[2]; ReadExactly(input, offset, bytes); return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    }

    private static uint ReadUInt32(Stream input, long offset)
    {
        Span<byte> bytes = stackalloc byte[4]; ReadExactly(input, offset, bytes); return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static void ReadExactly(Stream input, long offset, Span<byte> bytes)
    {
        if (offset < 0 || offset > input.Length - bytes.Length) throw new InvalidDataException("PE 读取越界。");
        input.Position = offset;
        int read = 0;
        while (read < bytes.Length)
        {
            int count = input.Read(bytes[read..]);
            if (count == 0) throw new InvalidDataException("PE 头读取不完整。");
            read += count;
        }
    }

    private readonly record struct Section(uint VirtualAddress, uint MappedSize, uint RawOffset, uint RawSize);

    private sealed class ResourceTree(Stream input, long rootOffset, uint size)
    {
        private long End => checked(rootOffset + size);
        public uint ReadUInt32(long offset)
        {
            Span<byte> bytes = stackalloc byte[4]; Read(offset, bytes); return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
        public long FindVersionData(long relative, int level)
        {
            long directory = checked(rootOffset + relative);
            if (directory < rootOffset || directory + 16 > End) throw new InvalidDataException("PE 资源目录越界。");
            ushort named = ReadUInt16(directory + 12), ids = ReadUInt16(directory + 14);
            int count = checked(named + ids);
            long entries = directory + 16;
            if (entries + count * 8L > End) throw new InvalidDataException("PE 资源项越界。");
            for (int index = 0; index < count; index++)
            {
                long entry = entries + index * 8L;
                uint name = ReadUInt32(entry), target = ReadUInt32(entry + 4);
                uint id = name & 0x7fffffff;
                if (level == 0 && id != ResourceVersionType) continue;
                if ((target & 0x80000000) != 0)
                    return FindVersionData(target & 0x7fffffff, level + 1);
                if (level >= 2)
                {
                    long data = checked(rootOffset + (target & 0x7fffffff));
                    if (data < rootOffset || data + 16 > End) throw new InvalidDataException("PE 资源数据越界。");
                    return data;
                }
            }
            throw new InvalidDataException("PE 缺少 VERSIONINFO 资源。");
        }
        private ushort ReadUInt16(long offset)
        {
            Span<byte> bytes = stackalloc byte[2]; Read(offset, bytes); return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        }
        private void Read(long offset, Span<byte> bytes)
        {
            if (offset < rootOffset || offset > End - bytes.Length) throw new InvalidDataException("PE 资源读取越界。");
            input.Position = offset; int read = 0;
            while (read < bytes.Length)
            {
                int count = input.Read(bytes[read..]);
                if (count == 0) throw new InvalidDataException("PE 资源读取不完整.");
                read += count;
            }
        }
    }
}
