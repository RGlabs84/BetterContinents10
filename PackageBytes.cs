// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.IO;
using System.Security.Cryptography;

namespace BetterContinents;

// The bytes of a settings package (ZPackage: a MemoryStream under a reader and a writer) without the copies its own methods
// make. ZPackage.GetArray copies the whole package (and so did GenerateHash, and new ZPackage(bytes), and every read of a
// file into one): a world of 16384 px maps is hundreds of megabytes, and the package was held two to four times over while
// it was saved, hashed and sent. Everything here works on the package's own buffer.
internal static class PackageBytes
{
  // The most a package may hold: a MemoryStream is limited to 2.1 GB, and one map's bytes (a 16384 px picture is up to 540 MB
  // as a PNG or bytes) can be written after the last check. A world whose maps do not fit is refused with a message (Guard)
  // rather than failing in the stream.
  internal const int MaxLength = 1_500_000_000;

  // What Guard allows: MaxLength, lower for the tests.
  internal static int Limit = MaxLength;

  // The package's buffer, and how many bytes of it are the package (the buffer is larger, to grow into). No copy.
  internal static byte[] Buffer(ZPackage package, out int length)
  {
    package.Flush();
    length = (int)package.m_stream.Length;
    return package.m_stream.GetBuffer();
  }

  // SHA-512 of the package's bytes (ZPackage.GenerateHash, without its copy).
  internal static byte[] Hash(ZPackage package)
  {
    var buffer = Buffer(package, out int length);
    return Hash(buffer, length);
  }

  internal static byte[] Hash(byte[] data, int length)
  {
    using var sha = SHA512.Create();
    return sha.ComputeHash(data, 0, length);
  }

  // A package of count bytes read from the stream, straight into the package's buffer (new ZPackage(reader.ReadBytes(count))
  // read them twice over).
  internal static ZPackage Read(Stream from, int count)
  {
    if (count < 0)
      throw new InvalidDataException($"a settings package of {count} bytes");
    var package = new ZPackage();
    var stream = package.m_stream;
    stream.SetLength(count);
    var buffer = stream.GetBuffer();
    int read = 0, n;
    while (read < count && (n = from.Read(buffer, read, count - read)) > 0)
      read += n;
    if (read != count)
      throw new EndOfStreamException($"a settings package of {count} bytes ends after {read}");
    stream.Position = 0;
    return package;
  }

  internal static ZPackage Read(BinaryReader reader, int count) => Read(reader.BaseStream, count);

  // A block written into the package as pkg.Write(bytes) would write it (its length as an int, then its bytes), but straight
  // from `write`, with no array of the block made first: the length is written after the block, over a placeholder.
  internal static void WriteBlock(ZPackage package, Action<BinaryWriter> write)
  {
    package.Flush();
    var stream = package.m_stream;
    long lengthAt = stream.Position;
    stream.Write(new byte[4], 0, 4);
    using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
    {
      write(writer);
      writer.Flush();
    }
    long end = stream.Position;
    stream.Position = lengthAt;
    stream.Write(BitConverter.GetBytes(checked((int)(end - lengthAt - 4))), 0, 4);
    stream.Position = end;
  }

  // The block that package.ReadByteArray() would read, as a stream over the package's own buffer: no copy of it. The package
  // goes on after the block.
  internal static Stream ReadBlock(ZPackage package)
  {
    var stream = package.m_stream;
    int length = package.ReadInt();
    long at = stream.Position;
    if (length < 0 || at + length > stream.Length)
      throw new EndOfStreamException($"a block of {length} bytes ends beyond its package");
    stream.Position = at + length;
    return new MemoryStream(stream.GetBuffer(), (int)at, length, writable: false);
  }

  // Room for the package's bytes before they are written, so the stream does not grow by doubling (which holds up to twice
  // the bytes, and with them the 2 GB limit twice as soon).
  internal static void Reserve(ZPackage package, long bytes)
  {
    if (bytes > package.m_stream.Capacity && bytes < MaxLength)
      package.m_stream.Capacity = (int)bytes;
  }

  // Stops a package that cannot take `upcoming` more bytes (what a map is about to write) under the limit, by name, before the
  // stream fails without saying anything of the sort.
  internal static void Guard(ZPackage package, string what, long upcoming)
  {
    package.Flush();
    long length = package.m_stream.Length + upcoming;
    if (length > Limit)
      throw new WorldTooLargeException($"the world's settings come to {Size(length)} with {what}, over the {Size(Limit)} a settings file or a transfer to a player can hold. " +
        "Compact Maps, or smaller or fewer maps, keep them far smaller.");
  }

  private static string Size(long bytes) => bytes >= 1_000_000 ? $"{bytes / 1_000_000:N0} MB" : $"{bytes / 1000:N0} KB";
}

// The settings are too large for a package.
internal sealed class WorldTooLargeException(string message) : Exception(message)
{
}
