// Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).

using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterContinents;

// The runner's layer: slice A's snapshot (BakedLayerStore) and LayerEdit behind IBakeLayerPort. Each change makes one new layer (the old
// revision + 1), makes it current and says which zones differ, for the push. Palette entries are only ever appended, so a record taken out of the
// layer keeps the index it had; a record from the undo file is matched to the layer's palette by its entry.
//
// An edit that cannot be built (the layer would pass a limit of format 1, a number the registry cannot take, a record whose bake the registry no
// longer lists) throws BakedFormatException with the reason, and nothing has changed: the edit is a new layer until it is installed.
internal sealed class LayerPort : IBakeLayerPort
{
  private static readonly ValueSet NoValues = new([]);

  private readonly Func<BakedLayer?> current;
  private readonly Action<BakedLayer?, ZoneKey[]?> install;

  // What the last estimate built, so that the run that follows does not build it twice: good while the layer it grew from is still current.
  private BakeJournalData? builtFor;
  private BakedLayer? builtFrom;
  private (BakedLayer Layer, ZoneKey[] Changed) built;

  public LayerPort() : this(() => BakedLayerStore.Current, BakedLayerStore.Set)
  {
  }

  public LayerPort(Func<BakedLayer?> current, Action<BakedLayer?, ZoneKey[]?> install)
  {
    this.current = current;
    this.install = install;
  }

  public BakedLayer? Current => current();
  public bool HasLayer => current() != null;
  public uint Revision => current()?.Revision ?? 0;
  public long Bytes => current()?.Length ?? 0;
  public int NextOperation => current()?.Registry.NextOperation ?? 1;
  public IReadOnlyList<OperationInfo> Operations => current()?.Registry.Operations ?? Array.Empty<OperationInfo>();

  public bool TryGetOperation(int number, out OperationInfo operation)
  {
    var layer = current();
    if (layer != null)
      return layer.Registry.TryGet(number, out operation);
    operation = null!;
    return false;
  }

  public long RecordsOfSource(int source) => current()?.RecordsOfSource(source) ?? 0;
  public long Records => current()?.Placements ?? 0;

  public List<ZoneKey> ZonesWithRecords()
  {
    var zones = new List<ZoneKey>();
    var layer = current();
    if (layer != null)
      foreach (var row in layer.Zones)
        if (row.Placements > 0)
          zones.Add(row.Key);
    return zones;
  }

  public List<BakeRecord> RecordsIn(IEnumerable<ZoneKey> zones, Func<ZoneRecord, bool> test)
  {
    var found = new List<BakeRecord>();
    var layer = current();
    if (layer == null)
      return found;
    foreach (var zone in zones)
    {
      if (!layer.TryGetZoneRow(zone, out var row) || row.Placements == 0)
        continue;
      var data = layer.Decode(row);
      for (int k = 0; k < data.Count; k++)
      {
        var record = data.Record(k);
        if (test(record))
          found.Add(new BakeRecord(layer.Palette[record.Palette], record, ValuesOf(layer, record)));
      }
    }
    return found;
  }

  // The values a record's piece had: its value set in its bake's registry entry (a compiler's record has none).
  private static ValueSet ValuesOf(BakedLayer layer, ZoneRecord record)
  {
    if (record.HasSource && layer.Registry.TryGet(record.Source, out var operation) && record.ValueSet < operation.ValueSets.Length)
      return operation.ValueSets[record.ValueSet];
    return NoValues;
  }

  // ------------------------------------------------------------------------------------------------ the edits

  public BakeEstimate EstimateAdd(BakeJournalData data)
  {
    var basis = current();
    var made = Guarded(() => BuildAdd(basis, data));
    builtFor = data;
    builtFrom = basis;
    built = made;
    long patch = -1;
    if (basis != null)
    {
      try
      {
        patch = BakedPatch.Make(basis, made.Layer, made.Changed).Length;
      }
      catch (BakedFormatException)
      {
        // The push sends the whole layer then; the dry run only does not know the size.
      }
    }
    return new BakeEstimate { Revision = made.Layer.Revision, Bytes = made.Layer.Length, Growth = made.Layer.Length - (basis?.Length ?? 0), PatchBytes = patch };
  }

  public LayerChange Add(BakeJournalData data)
  {
    var basis = current();
    var made = ReferenceEquals(builtFor, data) && ReferenceEquals(builtFrom, basis) ? built : Guarded(() => BuildAdd(basis, data));
    builtFor = null;
    builtFrom = null;
    built = default;
    return Install(made);
  }

  public LayerChange Remove(BakeJournalData data)
  {
    var made = Guarded(() =>
    {
      var basis = current() ?? throw new BakedFormatException("this world has no layer");
      var edit = basis.Edit();
      edit.EnsureNextOperation(data.Number);
      edit.RemoveRecords(Resolve(basis, data.Records));
      edit.AddOperation(data.ToOperation());
      return edit.Build();
    });
    return Install(made);
  }

  public LayerChange UndoBake(int number)
  {
    var made = Guarded(() =>
    {
      var basis = current() ?? throw new BakedFormatException("this world has no layer");
      var edit = basis.Edit();
      edit.RemoveSource(number);
      // The value sets go with the mark, so the records come out first.
      edit.SetOperationState(number, OperationState.Undone);
      return edit.Build();
    });
    return Install(made);
  }

  public LayerChange PutBack(BakeJournalData data)
  {
    var made = Guarded(() =>
    {
      var basis = current();
      var edit = basis != null ? basis.Edit() : LayerEdit.New();
      var registry = basis?.Registry ?? Registry.Empty;
      // A record of an in-game bake names that bake's value set in the registry: it must still hold the values the undo file has.
      foreach (var record in data.Records)
      {
        if (!record.Record.HasSource)
          continue;
        int source = record.Record.Source, index = record.Record.ValueSet;
        if (!registry.TryGet(source, out var operation) || !operation.IsBake || index >= operation.ValueSets.Length || !operation.ValueSets[index].Equals(record.Values))
          throw new BakedFormatException($"bake {source} no longer holds the values of these pieces (it was undone, or it left the registry)");
      }
      AddGrouped(edit, data.Records);
      edit.SetOperationState(data.Number, OperationState.Undone);
      return edit.Build();
    });
    return Install(made);
  }

  public LayerChange Load(BakedLayer file, OperationInfo operation, out int leftOut)
  {
    var made = BuildLoad(file, operation, out leftOut);
    return Install(made);
  }

  public BakedLayer PreviewLoad(BakedLayer file, OperationInfo operation) => BuildLoad(file, operation, out _).Layer;

  private (BakedLayer Layer, ZoneKey[] Changed) BuildLoad(BakedLayer file, OperationInfo operation, out int leftOut)
  {
    var basis = current();
    int left = 0;
    var made = Guarded(() =>
    {
      var edit = basis != null ? basis.Edit() : LayerEdit.New();
      left = edit.ReplaceSource0(file);
      edit.EnsureNextOperation(operation.Number);
      edit.AddOperation(operation);
      return edit.Build();
    });
    leftOut = left;
    return made;
  }

  // ------------------------------------------------------------------------------------------------ the pieces of an edit

  private static (BakedLayer Layer, ZoneKey[] Changed) BuildAdd(BakedLayer? basis, BakeJournalData data)
  {
    var edit = basis != null ? basis.Edit() : LayerEdit.New();
    edit.EnsureNextOperation(data.Number);
    AddGrouped(edit, data.Records);
    edit.AddOperation(data.ToOperation());
    return edit.Build();
  }

  // The records in, each with the palette index of its entry (an equal entry's, or a new one appended), grouped by how far above its y a
  // piece reaches so that a zone's y bounds grow no more than they must.
  private static void AddGrouped(LayerEdit edit, IEnumerable<BakeRecord> records)
  {
    var groups = new SortedDictionary<float, List<ZoneRecord>>();
    foreach (var record in records)
    {
      var placed = record.Record;
      placed.Palette = (ushort)edit.PaletteIndexFor(record.Entry);
      float height = (float)Math.Ceiling(record.Height);
      if (!groups.TryGetValue(height, out var list))
        groups[height] = list = [];
      list.Add(placed);
    }
    foreach (var group in groups)
      edit.AddRecords(group.Value, group.Key);
  }

  // The records as the layer holds them: the index they came out of it with when that is still the entry's, else the index of an equal entry.
  // A record whose entry the layer does not have is not there to remove.
  private static List<ZoneRecord> Resolve(BakedLayer basis, IEnumerable<BakeRecord> records)
  {
    var resolved = new List<ZoneRecord>();
    foreach (var record in records)
    {
      var placed = record.Record;
      if (placed.Palette >= basis.Palette.Count || !basis.Palette[placed.Palette].Equals(record.Entry))
      {
        int index = IndexOf(basis, record.Entry);
        if (index < 0)
          continue;
        placed.Palette = (ushort)index;
      }
      resolved.Add(placed);
    }
    return resolved;
  }

  private static int IndexOf(BakedLayer layer, PaletteEntry entry)
  {
    for (int i = 0; i < layer.Palette.Count; i++)
      if (layer.Palette[i].Equals(entry))
        return i;
    return -1;
  }

  private LayerChange Install((BakedLayer Layer, ZoneKey[] Changed) made)
  {
    install(made.Layer, made.Changed);
    return new LayerChange { Layer = made.Layer, Changed = made.Changed, Revision = made.Layer.Revision, Bytes = made.Layer.Length };
  }

  // What an edit throws for a reason that is the layer's or the registry's, as one kind of exception with the words to say.
  private static T Guarded<T>(Func<T> build)
  {
    try
    {
      return build();
    }
    catch (Exception e) when (e is InvalidOperationException or ArgumentException)
    {
      throw new BakedFormatException(e.Message);
    }
  }
}
