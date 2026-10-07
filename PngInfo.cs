// Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BetterContinents;

// What is wrong with a PNG file, in the words the log uses ("it is cut short (inside its IDAT chunk)").
internal sealed class PngInfoException(string message) : Exception(message)
{
}

// A PNG file's header and text chunks, read from its bytes without decoding the picture. ImageSharp turns every picture into the
// pixel type it is asked for (an 8-bit one into 16-bit grey, an RGB one into grey), so what a file is - its size, bit depth and
// colour type - can be known only from the file. Fine heights need it: a heightmap-fine.png must be 8-bit grey, and the heightmap it
// refines 16-bit grey, as files. Every chunk is walked and its CRC checked, as the reference (tools/fine_ref.py) does.
internal sealed class PngInfo
{
  private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
  // A text chunk that inflates to more than this is not read: a record is a line.
  private const int MaxText = 1 << 20;

  internal int Width, Height, Depth, ColourType;
  internal bool Interlaced;
  // The text chunks (tEXt, zTXt and iTXt) that could be read, in the file's order: keyword and text.
  internal readonly List<(string Keyword, string Text)> Texts = [];

  // "16-bit grey", "8-bit grey and alpha", "16-bit RGB, interlaced".
  internal string Describe() => $"{Depth}-bit {ColourName(ColourType)}" + (Interlaced ? ", interlaced" : "");

  private static string ColourName(int colourType) => colourType switch
  {
    0 => "grey",
    2 => "RGB",
    3 => "palette",
    4 => "grey and alpha",
    _ => "RGBA",
  };

  // The header and text chunks of a PNG file; throws PngInfoException when it is not one, or is damaged or cut short.
  internal static PngInfo Read(byte[] file)
  {
    if (file.Length < 8 || !file.AsSpan(0, 8).SequenceEqual(Signature))
      throw new PngInfoException("it is not a PNG file");
    var info = new PngInfo();
    bool header = false;
    int images = 0;
    long at = 8;
    while (true)
    {
      if (at + 12 > file.Length)
        throw new PngInfoException("it is cut short (no IEND)");
      uint size = Be32(file, (int)at);
      var name = new string(new[] { (char)file[at + 4], (char)file[at + 5], (char)file[at + 6], (char)file[at + 7] });
      if (at + 12 + size > file.Length)
        throw new PngInfoException($"it is cut short (inside its {name} chunk)");
      int data = (int)at + 8, length = (int)size;
      if (Crc32.Compute(file, (int)at + 4, 4 + length) != Be32(file, data + length))
        throw new PngInfoException($"its {name} chunk at byte {at} is damaged (the CRC does not match)");
      if (!header && name != "IHDR")
        throw new PngInfoException("it does not start with IHDR");
      switch (name)
      {
        case "IHDR":
          if (length != 13 || header)
            throw new PngInfoException("its IHDR is not valid");
          info.Width = (int)Math.Min(Be32(file, data), int.MaxValue);
          info.Height = (int)Math.Min(Be32(file, data + 4), int.MaxValue);
          info.Depth = file[data + 8];
          info.ColourType = file[data + 9];
          info.Interlaced = file[data + 12] == 1;
          // Compression and filter method 0 are the only ones there are; interlace method 0 or 1; the colour types PNG has.
          if (file[data + 10] != 0 || file[data + 11] != 0 || file[data + 12] > 1 || info.ColourType is not (0 or 2 or 3 or 4 or 6))
            throw new PngInfoException("its IHDR is not valid");
          header = true;
          break;
        case "IDAT":
          images++;
          break;
        case "tEXt":
        case "zTXt":
        case "iTXt":
          if (Text(name, file, data, length) is { } text)
            info.Texts.Add(text);
          break;
      }
      if (name == "IEND")
        break;
      at += 12 + size;
    }
    if (images == 0)
      throw new PngInfoException("it holds no image data");
    return info;
  }

  private static uint Be32(byte[] data, int at) => (uint)(data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3]);

  // (keyword, text) of a text chunk, or null when it cannot be read: a keyword and the text after a zero byte; the text of a zTXt chunk
  // is deflated (after a method byte and zlib's header), and an iTXt chunk's is UTF-8, deflated or not, after its flags and two
  // strings (the language and the translated keyword).
  private static (string, string)? Text(string kind, byte[] file, int at, int length)
  {
    try
    {
      int end = at + length;
      int zero = Array.IndexOf(file, (byte)0, at, length);
      if (zero < 0)
        return null;
      var keyword = Latin1(file, at, zero - at);
      int body = zero + 1;
      switch (kind)
      {
        case "tEXt":
          return (keyword, Latin1(file, body, end - body));
        case "zTXt":
          // The method byte, then zlib's two header bytes.
          return (keyword, Latin1(Inflate(file, body + 3, end - body - 3)));
        default:
          if (body + 2 > end)
            return null;
          bool compressed = file[body] != 0;
          body += 2;
          // The language tag and the translated keyword, each ended by a zero byte.
          for (int i = 0; i < 2; i++)
          {
            int next = Array.IndexOf(file, (byte)0, body, end - body);
            if (next < 0)
              return null;
            body = next + 1;
          }
          var bytes = compressed ? Inflate(file, body + 2, end - body - 2) : file.AsSpan(body, end - body).ToArray();
          return (keyword, new UTF8Encoding(false, true).GetString(bytes));
      }
    }
    catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
    {
      // A chunk that does not read is no record, as for ImageSharp.
      return null;
    }
  }

  private static string Latin1(byte[] data, int at, int length)
  {
    var chars = new char[length];
    for (int i = 0; i < length; i++)
      chars[i] = (char)data[at + i];
    return new string(chars);
  }

  private static string Latin1(byte[] data) => Latin1(data, 0, data.Length);

  private static byte[] Inflate(byte[] file, int at, int length)
  {
    if (length < 0)
      throw new InvalidDataException("a text chunk ends early");
    using var inflate = new DeflateStream(new MemoryStream(file, at, length, false), CompressionMode.Decompress);
    using var result = new MemoryStream();
    var buffer = new byte[4096];
    int n;
    while ((n = inflate.Read(buffer, 0, buffer.Length)) > 0)
    {
      result.Write(buffer, 0, n);
      if (result.Length > MaxText)
        throw new InvalidDataException("a text chunk inflates to more than a record can be");
    }
    return result.ToArray();
  }
}
