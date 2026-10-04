// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).

// A world export's heightmap.png records the Heightmap Amount and Sea Level Adjustment its heights are encoded for
// (HeightmapRecord, a PNG text chunk that image editors show). A heightmap without one has none; the pixels are the same.

using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using BetterContinents;

namespace ExportTest;

internal static class RecordTest
{
  static void C(bool ok, string what) => Program.C(ok, what);

  public static void Run(string work)
  {
    System.Console.WriteLine("== the heightmap's record of the settings it is encoded for");
    var px = Enumerable.Range(0, 16).Select(i => new L16((ushort)(i * 4000))).ToArray();
    var plainPath = Path.Combine(work, "plain-heightmap.png");
    var recordedPath = Path.Combine(work, "recorded-heightmap.png");
    WorldExportPng.SaveL16(plainPath, px, 4);
    WorldExportPng.SaveHeightmap(recordedPath, px, 4, new HeightmapRecord(1.2345678f, 0.123456789f));
    var plain = ImageMapFloat.Create(File.ReadAllBytes(plainPath), false)!;
    var recorded = ImageMapFloat.Create(File.ReadAllBytes(recordedPath), false)!;
    C(plain.Record == null, "a heightmap written without a record has none");
    C(recorded.Record is { } r && r.Amount == 1.2345678f && r.SeaLevel == 0.123456789f,
      $"a recorded one gives its Heightmap Amount and Sea Level Adjustment back, float for float ({recorded.Record?.Amount}, {recorded.Record?.SeaLevel})");
    C(Enumerable.Range(0, 16).All(i => plain.GetValue(i % 4 / 3f, i / 4 / 3f) == recorded.GetValue(i % 4 / 3f, i / 4 / 3f)),
      "the pixels read the same with the record as without");
    using (var image = Image.Load(File.ReadAllBytes(recordedPath)))
    {
      var text = image.Metadata.GetPngMetadata().TextData.SingleOrDefault(t => t.Keyword == HeightmapRecord.Keyword);
      C(text.Value == "Heightmap Amount = 1.2345678; Sea Level Adjustment = 0.12345679",
        $"it is a PNG text chunk anyone can read: {HeightmapRecord.Keyword}: {text.Value}");
    }
    var record = recorded.Record!;
    C(record.Matches(1.2345678f, 0.123456789f) && !record.Matches(1f, 0.123456789f) && !record.Matches(1.2345678f, 0.5f),
      "a world reads it as made only at both values");
    C(HeightmapRecord.From([new PngTextData("Software", "Heightmap Amount = 2; Sea Level Adjustment = 0.5", "", "")]) == null
      && HeightmapRecord.From([new PngTextData(HeightmapRecord.Keyword, "Heightmap Amount = two; Sea Level Adjustment = 0.5", "", "")]) == null
      && HeightmapRecord.From([new PngTextData(HeightmapRecord.Keyword, "Heightmap Amount = 2", "", "")]) == null,
      "another program's text, an unreadable number or a missing value is no record");
  }
}
