// Added by Wubarrk on 2026-10-04 for the unifying refactor (0.10.0).

// What a mutation run found the other checks let through. Every line of ZoneReset.cs, ZoneRegen.cs, GameUtils.ResetZones and the
// patches was broken one way at a time, and the breaks no check noticed are covered here: which of the two regenerations
// GameUtils.ResetZones starts, what PokeHeightmaps clears, the game's calls that need Unity (a zone's root, a loaded object, the
// ground's height, the grass and the minimap), a request while another runs, an error in the work, a closing world in every phase,
// who the work looks at first, the progress lines, the order of a plan whatever order it is given, where a spawner's creature may
// stand, a peer that is not ready, and every player-visible string of the regeneration.
//
// The game's Unity parts are not here, so each call that needs them is a field of the production code (ZoneRegen.StartRoutine,
// FindView, DestroyObject, GameUtils.PokeHeightmap, ...) and the tests put their own in, restoring the game's with Seams.Dispose.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BetterContinents;
using HarmonyLib;
using UnityEngine;
using BC = BetterContinents.BetterContinents;
using Budget = BetterContinents.ZoneReset.Budget;
using Edges = BetterContinents.ZoneReset.Edges;
using Kind = BetterContinents.ZoneReset.Kind;
using Plan = BetterContinents.ZoneReset.Plan;
using Progress = BetterContinents.ZoneReset.Progress;
using Rules = BetterContinents.ZoneReset.Rules;

internal static partial class Program
{
  // The fields of the production code as the game has them, taken when the program starts.
  private static Seams? Defaults;

  private static void HardenTests()
  {
    ZoneRegen.DeltaTime = () => 1f / 60f;
    DefaultSeamTests();
    ResetZonesTests();
    PokeHeightmapsTests();
    UnityCallTests();
    LoadedObjectTests();
    PieceTests();
    AliveTests();
    RequestTests();
    GoneTests();
    FocusTests();
    ProgressLineTests();
    OrderTests();
    FollowSpawnTests();
    ReadyPeerTests();
    WordsTests();
    MessageGapTests();
    PlanGapTests();
    GroundGapTests();
    WorkGapTests();
    RunGapTests();
    ErrorCountTests();
    MemoryTests();
    SaveErrorTests();
  }

  // ------------------------------------------------------------------------------------------------ helpers

  // Unity's `if (obj)` is false for an object whose native half is gone: its pointer is zero. A made-up object is dead, one that has a
  // pointer is alive. Reflection's SetValue would run UnityEngine.Object's type initializer, which reaches into the engine; IL does not.
  private static readonly Action<UnityEngine.Object, IntPtr> SetNativePointer = MakePointerSetter();

  private static Action<UnityEngine.Object, IntPtr> MakePointerSetter()
  {
    var field = typeof(UnityEngine.Object).GetField("m_CachedPtr", Any)!;
    var method = new DynamicMethod("SetNativePointer", typeof(void), [typeof(UnityEngine.Object), typeof(IntPtr)], typeof(Program).Module, true);
    var il = method.GetILGenerator();
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ldarg_1);
    il.Emit(OpCodes.Stfld, field);
    il.Emit(OpCodes.Ret);
    return (Action<UnityEngine.Object, IntPtr>)method.CreateDelegate(typeof(Action<UnityEngine.Object, IntPtr>));
  }

  private static T Alive<T>() where T : UnityEngine.Object
  {
    var made = Make<T>();
    SetNativePointer(made, (IntPtr)1);
    return made;
  }

  // Everything the production code lets the tests stand in for, and the statics a run leaves behind, put back as the game has them.
  private sealed class Seams : IDisposable
  {
    public readonly Func<IEnumerator, Coroutine> startRoutine = ZoneRegen.StartRoutine;
    public readonly Action<Coroutine> stopRoutine = ZoneRegen.StopRoutine;
    public readonly Func<double>? frameClock = ZoneRegen.FrameClock;
    public readonly Func<ZDO, ZNetView?> findView = ZoneRegen.FindView;
    public readonly Action<ZNetView> destroyView = ZoneRegen.DestroyView;
    public readonly Action<GameObject> destroyObject = ZoneRegen.DestroyObject;
    public readonly Func<float, float, float?> groundHeight = ZoneRegen.GroundHeight;
    public readonly Action<ClutterSystem> clearGrass = ZoneRegen.ClearGrass;
    public readonly Action<Minimap> redrawPins = ZoneRegen.RedrawLocationPins;
    public readonly Func<Piece, Vector3> piecePosition = ZoneRegen.PiecePosition;
    public readonly Action finish = ZoneRegen.Finish;
    public readonly Func<float> deltaTime = ZoneRegen.DeltaTime;
    public readonly Action sendQueue = ZoneRegen.SendDestroyQueue;
    public readonly Action<string> runCommand = GameUtils.RunConsoleCommand;
    public readonly Action requestRegeneration = GameUtils.RequestRegeneration;
    public readonly Action<Heightmap> pokeHeightmap = GameUtils.PokeHeightmap;
    // (The configuration is not declared yet when the defaults are taken.)
    private readonly string? command = BC.ConfigDebugResetCommand?.Value;

    public void Dispose()
    {
      ZoneRegen.StartRoutine = startRoutine;
      ZoneRegen.StopRoutine = stopRoutine;
      ZoneRegen.FrameClock = frameClock;
      ZoneRegen.FindView = findView;
      ZoneRegen.DestroyView = destroyView;
      ZoneRegen.DestroyObject = destroyObject;
      ZoneRegen.GroundHeight = groundHeight;
      ZoneRegen.ClearGrass = clearGrass;
      ZoneRegen.RedrawLocationPins = redrawPins;
      ZoneRegen.PiecePosition = piecePosition;
      ZoneRegen.Finish = finish;
      ZoneRegen.DeltaTime = deltaTime;
      ZoneRegen.SendDestroyQueue = sendQueue;
      GameUtils.RunConsoleCommand = runCommand;
      GameUtils.RequestRegeneration = requestRegeneration;
      GameUtils.PokeHeightmap = pokeHeightmap;
      if (command != null)
        BC.ConfigDebugResetCommand.Value = command;
      SetStatic(typeof(ZNet), "m_isServer", true);
      SetStatic(typeof(ZoneRegen), "job", null);
      SetStatic(typeof(ZoneRegen), "flushOwed", false);
      GetStatic<HashSet<Vector2s>>(typeof(ZoneRegen), "placedDuringRun").Clear();
      SetStatic(typeof(ClutterSystem), "m_instance", null);
      SetStatic(typeof(Minimap), "s_instance", null);
      GetStatic<List<Heightmap>>(typeof(Heightmap), "s_heightmaps").Clear();
    }
  }

  // ------------------------------------------------------------------------------------------------ the defaults of the seams

  private static bool CallsMethod(Delegate seam, Type type, string method) =>
    Callees(seam.Method).Any(m => m.DeclaringType == type && m.Name == method);

  // Whether the method makes the call with these constants pushed just before it (a call's arguments are pushed in order).
  private static bool CallsWith(Delegate seam, Type type, string method, params OpCode[] constants)
  {
    var code = PatchProcessor.GetOriginalInstructions(seam.Method).ToList();
    for (int i = constants.Length; i < code.Count; i++)
      if ((code[i].opcode == OpCodes.Call || code[i].opcode == OpCodes.Callvirt) && code[i].operand is MethodBase m && m.DeclaringType == type && m.Name == method
          && Enumerable.Range(0, constants.Length).All(k => code[i - constants.Length + k].opcode == constants[k]))
        return true;
    return false;
  }

  private static void DefaultSeamTests()
  {
    Section("the seams: what the game does by default, read from the compiled code");
    var d = Defaults!;
    C(CallsMethod(d.startRoutine, typeof(MonoBehaviour), "StartCoroutine") && CallsMethod(d.stopRoutine, typeof(MonoBehaviour), "StopCoroutine"),
      "the run is a coroutine of the plugin: started and stopped on the plugin's MonoBehaviour");
    C(d.frameClock == null, "a frame's time is taken by the system clock");
    C(CallsMethod(d.findView, typeof(ZNetScene), "FindInstance"), "a loaded object is found through the scene's instances");
    C(CallsMethod(d.destroyView, typeof(ZNetScene), "Destroy") && CallsMethod(d.destroyView, typeof(Component), "get_gameObject"), "and destroyed through the scene, which hands an owned ZDO to ZDOMan");
    C(CallsMethod(d.destroyObject, typeof(UnityEngine.Object), "Destroy"), "a zone's root is destroyed with Unity's Destroy");
    C(CallsMethod(d.groundHeight, typeof(WorldGenerator), "GetHeight"), "the ground's height is the world generator's");
    C(CallsMethod(d.clearGrass, typeof(ClutterSystem), "ClearAll"), "the grass is cleared with the grass system's ClearAll");
    C(CallsWith(d.redrawPins, typeof(Minimap), "UpdateLocationPins", OpCodes.Ldc_R4), "the minimap's location icons are updated at once: with a time step that is over the timer");
    C(d.redrawPins.Method.GetMethodBody() != null && PatchProcessor.GetOriginalInstructions(d.redrawPins.Method).Any(i => i.opcode == OpCodes.Ldc_R4 && Convert.ToSingle(i.operand) == 1000f), "(of 1000 seconds)");
    C(CallsMethod(d.piecePosition, typeof(Component), "get_transform") && CallsMethod(d.piecePosition, typeof(Transform), "get_position"), "a piece is where its transform is");
    C(d.finish.Method.Name == nameof(ZoneRegen.FinishTouches), "when the work is done the grass and the minimap are touched");
    C(CallsMethod(d.deltaTime, typeof(Time), "get_unscaledDeltaTime"), "a frame's length for the bandwidth is the real time, not the game's scaled time");
    C(CallsMethod(d.sendQueue, typeof(ZDOMan), "SendDestroyed"), "a save sends the destroy queue the way the game does every frame");
    C(CallsMethod(d.runCommand, typeof(Terminal), "TryRunCommand"), "a Debug Reset Command runs on the console");
    C(CallsMethod(d.requestRegeneration, typeof(ZoneRegen), "Request"), "an empty one starts Better Continents' regeneration");
    C(CallsWith(d.pokeHeightmap, typeof(Heightmap), "Poke", OpCodes.Ldc_I4_1, OpCodes.Ldc_I4_0), "a chunk of terrain is poked one frame later, and not for its paint alone");
  }

  // ------------------------------------------------------------------------------------------------ GameUtils

  private static void ResetZonesTests()
  {
    Section("GameUtils.ResetZones: the console command or Better Continents' own regeneration, never both and never neither");
    using var seams = new Seams();
    var events = new List<string>();
    var maps = GetStatic<List<Heightmap>>(typeof(Heightmap), "s_heightmaps");
    maps.Add(FakeHeightmap());
    GameUtils.RunConsoleCommand = command => events.Add("run " + command);
    GameUtils.RequestRegeneration = () => events.Add("regenerate");
    GameUtils.PokeHeightmap = _ => events.Add("poke");

    foreach (var empty in new[] { "", "   ", "\t" })
    {
      events.Clear();
      BC.ConfigDebugResetCommand.Value = empty;
      GameUtils.ResetZones();
      C(events.SequenceEqual(["poke", "regenerate"]), $"Debug Reset Command {(empty == "" ? "empty" : "blank")}: the loaded terrain is poked, then Better Continents regenerates the zones, and no command runs: " + string.Join(", ", events));
    }
    foreach (var command in new[] { "zones_reset start", "zones_reset start safezones=3", "x" })
    {
      events.Clear();
      BC.ConfigDebugResetCommand.Value = command;
      GameUtils.ResetZones();
      C(events.SequenceEqual(["run " + command, "poke"]), $"Debug Reset Command '{command}': that console command runs, then the terrain is poked, and Better Continents does not regenerate: " + string.Join(", ", events));
    }
    maps.Add(FakeHeightmap());
    events.Clear();
    BC.ConfigDebugResetCommand.Value = "zones_reset start";
    GameUtils.ResetZones();
    C(events.SequenceEqual(["run zones_reset start", "poke", "poke"]), "every loaded chunk of terrain is poked, once, after the command: " + string.Join(", ", events));
  }

  // A Heightmap made without Unity: the lists it keeps, one entry in each, and a build it would redo.
  private static Heightmap FakeHeightmap()
  {
    var map = Make<Heightmap>();
    foreach (var name in new[] { "m_cornerBiomeList", "m_cornerAltBiomes" })
    {
      var field = typeof(Heightmap).GetField(name, Any)!;
      var list = (IList)Activator.CreateInstance(field.FieldType)!;
      var element = field.FieldType.GetGenericArguments()[0];
      list.Add(element.IsValueType ? Activator.CreateInstance(element) : RuntimeHelpers.GetUninitializedObject(element));
      field.SetValue(map, list);
    }
    var build = typeof(Heightmap).GetField("m_buildData", Any)!;
    build.SetValue(map, RuntimeHelpers.GetUninitializedObject(build.FieldType));
    return map;
  }

  private static void PokeHeightmapsTests()
  {
    Section("PokeHeightmaps: a chunk of terrain forgets its build and the sectors and alt biomes of every earlier one");
    using var seams = new Seams();
    var maps = GetStatic<List<Heightmap>>(typeof(Heightmap), "s_heightmaps");
    var first = FakeHeightmap();
    var second = FakeHeightmap();
    maps.Add(first);
    maps.Add(second);
    var poked = new List<Heightmap>();
    GameUtils.PokeHeightmap = map => poked.Add(map);
    GameUtils.PokeHeightmaps();
    var build = typeof(Heightmap).GetField("m_buildData", Any)!;
    // (Unity's == and Equals call two made-up objects the same: both have instance id 0, so the references are compared.)
    C(poked.Count == 2 && ReferenceEquals(poked[0], first) && ReferenceEquals(poked[1], second), "each chunk is poked once, in the order the game keeps them");
    C(maps.All(map => build.GetValue(map) == null), "each forgets the build it was made from, so the poke builds it from the new settings");
    C(maps.All(map => map.m_cornerBiomeList.Count == 0), "each forgets the sectors of every earlier build (Heightmap only appends to them)");
    C(maps.All(map => map.m_cornerAltBiomes.Count == 0), "and the alt biomes of every earlier build, which the spawn system reads");
    GameUtils.PokeHeightmaps();
    C(poked.Count == 4, "(with nothing left to forget the chunks are poked again all the same)");
    maps.Clear();
    poked.Clear();
    GameUtils.PokeHeightmaps();
    C(poked.Count == 0, "no terrain loaded: nothing to poke");

    // The distant terrain TerrainLod built is poked through the same seam, after the zones' own heightmaps, and forgets its build as well; one
    // it has not built yet is left to it.
    var own = FakeHeightmap();
    var distant = FinalHeightmap(distant: true, built: true);
    var unbuilt = FinalHeightmap(distant: true, built: false);
    var instances = Heightmap.Instances;
    maps.Add(own);
    instances.Add(distant);
    instances.Add(unbuilt);
    try
    {
      poked.Clear();
      GameUtils.PokeHeightmaps();
      C(poked.Count == 2 && ReferenceEquals(poked[0], own) && ReferenceEquals(poked[1], distant), "the distant terrain is poked through the seam too, after the zones' own heightmaps, and no unbuilt one");
      C(FinalBuild(distant) == null, "and it forgets the build it was made from");
    }
    finally
    {
      instances.Clear();
    }
  }

  // ------------------------------------------------------------------------------------------------ the game's calls that need Unity

  private static void UnityCallTests()
  {
    Section("the game's calls that need Unity: a zone's root, a location's height, the grass, the minimap, a loaded object");
    using var seams = new Seams();
    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      var zone = Z(3, -2);
      var world = new ZoneRegen.GameWorld(new ZoneRegen.Job(_ => { }));

      // DestroyRoot: a loaded zone's copy goes by Unity's Destroy of its root, and the zone is no longer loaded.
      var destroyed = new List<GameObject>();
      ZoneRegen.DestroyObject = destroyed.Add;
      w.Load(zone);
      var root = Alive<GameObject>();
      w.Loaded[zone]!.GetType().GetField("m_root")!.SetValue(w.Loaded[zone], root);
      w.Load(Z(4, 4));
      world.DestroyRoot(zone);
      C(destroyed.Count == 1 && ReferenceEquals(destroyed[0], root) && !w.Loaded.Contains(zone) && w.Loaded.Contains(Z(4, 4)), "a loaded zone: its root is destroyed, once, and the zone is no longer loaded; another stays");
      world.DestroyRoot(Z(4, 4));
      C(destroyed.Count == 1 && !w.Loaded.Contains(Z(4, 4)), "a loaded zone whose root is gone already (Unity destroyed it): nothing is destroyed again, the zone is still dropped");
      world.DestroyRoot(zone);
      C(destroyed.Count == 1 && w.Loaded.Count == 0, "a zone that is not loaded: nothing happens");

      // Unplace: the location is to be placed again, at the ground's height now.
      w.PlaceLocation(zone, true, 20f, dx: 7f, dz: 3f);
      w.PlaceLocation(Z(0, 0), true, 20f);
      var asked = new List<(float, float)>();
      ZoneRegen.GroundHeight = (x, z) =>
      {
        asked.Add((x, z));
        return x * 0.5f + z * 0.25f + 100f;
      };
      var before = w.Zones.m_locationInstances[zone];
      world.Unplace(zone);
      var after = w.Zones.m_locationInstances[zone];
      C(!after.m_placed && before.m_placed && ReferenceEquals(after.m_location, before.m_location), "Unplace: the zone's location is marked not placed and is still the same location");
      C(asked.SequenceEqual([(199f, -125f)]) && after.m_position.x == 199f && after.m_position.z == -125f && after.m_position.y == 199f * 0.5f - 125f * 0.25f + 100f,
        $"... at the height of the ground where it stands (x and z asked for as they are): {after.m_position.x}, {after.m_position.y}, {after.m_position.z}");
      C(w.Zones.m_locationInstances[Z(0, 0)].m_placed && w.Zones.m_locationInstances[Z(0, 0)].m_position.y == 30f, "another zone's location is not touched");
      ZoneRegen.GroundHeight = (_, _) => null;
      w.PlaceLocation(zone, true, 20f, dx: 7f, dz: 3f);
      world.Unplace(zone);
      C(!w.Zones.m_locationInstances[zone].m_placed && w.Zones.m_locationInstances[zone].m_position.y == 30f, "with no world generator the location stays at its height");
      w.Zones.m_locationInstances.Clear();
      world.Unplace(zone);
      C(w.Zones.m_locationInstances.Count == 0, "a zone with no location gets none");

      // FinishTouches: the grass and the minimap's location icons, when they are there.
      var cleared = new List<ClutterSystem>();
      var redrawn = new List<Minimap>();
      ZoneRegen.ClearGrass = cleared.Add;
      ZoneRegen.RedrawLocationPins = redrawn.Add;
      ZoneRegen.FinishTouches();
      C(cleared.Count == 0 && redrawn.Count == 0, "no grass system and no minimap (the menu): nothing is redrawn");
      var grass = Alive<ClutterSystem>();
      var map = Alive<Minimap>();
      SetStatic(typeof(ClutterSystem), "m_instance", grass);
      ZoneRegen.FinishTouches();
      C(cleared.Count == 1 && ReferenceEquals(cleared[0], grass) && redrawn.Count == 0, "the grass system alone: it is cleared so the grass grows from the new ground");
      SetStatic(typeof(ClutterSystem), "m_instance", null);
      SetStatic(typeof(Minimap), "s_instance", map);
      cleared.Clear();
      ZoneRegen.FinishTouches();
      C(cleared.Count == 0 && redrawn.Count == 1 && ReferenceEquals(redrawn[0], map), "the minimap alone: its location icons follow the locations that are placed");
      SetStatic(typeof(ClutterSystem), "m_instance", Make<ClutterSystem>());
      SetStatic(typeof(Minimap), "s_instance", Make<Minimap>());
      cleared.Clear();
      redrawn.Clear();
      ZoneRegen.FinishTouches();
      C(cleared.Count == 0 && redrawn.Count == 0, "a grass system or a minimap that Unity has destroyed is left alone");
      SetStatic(typeof(ClutterSystem), "m_instance", grass);
      cleared.Clear();
      ZoneRegen.FinishTouches();
      C(cleared.Count == 1 && redrawn.Count == 0, "(a destroyed minimap beside a live grass system)");
      C(ZoneRegen.Finish.Method.Name == nameof(ZoneRegen.FinishTouches), "what the run does when it is done is FinishTouches");
    }
    finally
    {
      World.Close();
    }
  }

  // ------------------------------------------------------------------------------------------------ the game's side of the work

  private static void LoadedObjectTests()
  {
    Section("the game's side: an object that is loaded, and what a frame has queued");
    using var seams = new Seams();
    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      var world = new ZoneRegen.GameWorld(new ZoneRegen.Job(_ => { }));
      var zone = Z(2, 2);
      var loadedZdo = w.Add(zone, "Pine");
      var plainZdo = w.Add(zone, "Pine", dx: 3f);
      Tame(w.Add(zone, "Boar", dx: 5f));
      var listed = world.ObjectsIn(zone);
      var view = Alive<ZNetView>();
      var asked = new List<ZDOID>();
      var destroyedViews = new List<(ZNetView View, bool Owned)>();
      ZoneRegen.FindView = zdo =>
      {
        asked.Add(zdo.m_uid);
        return zdo.m_uid == loadedZdo.m_uid ? view : null;
      };
      ZoneRegen.DestroyView = v => destroyedViews.Add((v, loadedZdo.IsOwner()));
      SetStatic(typeof(ZoneRegen), "flushOwed", false);

      C(world.Destroy(listed[0], out _), "an object that is loaded is destroyed");
      C(asked.SequenceEqual([loadedZdo.m_uid]), "the scene is asked for the object's instance: the object's own");
      C(destroyedViews.Count == 1 && ReferenceEquals(destroyedViews[0].View, view) && destroyedViews[0].Owned, "the instance is destroyed through the scene, by this machine as its owner (the scene queues an owned ZDO itself)");
      C(w.DestroyQueue.Count == 0, "and the object is not also queued here: that would send it twice");
      C(GetStatic<bool>(typeof(ZoneRegen), "flushOwed"), "something was destroyed, so the game's destroy queue is owed to the clients");

      C(world.Destroy(listed[1], out _), "an object that is not loaded is destroyed");
      C(destroyedViews.Count == 1 && w.DestroyQueue.SequenceEqual([plainZdo.m_uid]), "... straight through ZDOMan, which tells every machine; no instance is destroyed");

      SetStatic(typeof(ZoneRegen), "flushOwed", false);
      C(!world.Destroy(listed[2], out _) && !GetStatic<bool>(typeof(ZoneRegen), "flushOwed"), "a tamed animal is not destroyed, and nothing is owed for it");

      // What a frame has queued is remembered for that frame only.
      var queued = GetField<HashSet<ZDOID>>(world, "queued");
      C(queued.SetEquals([loadedZdo.m_uid, plainZdo.m_uid]), "(the frame's set holds what was destroyed in it)");
      world.NewFrame();
      C(queued.Count == 0, "a new frame starts with an empty set: it does not grow with every object destroyed");
      var sent = 0;
      ZoneRegen.SendDestroyQueue = () => sent++;
      world.Destroy(listed[1], out _);
      SetStatic(typeof(ZoneRegen), "flushOwed", true);
      C(queued.Count == 1, "(an object is queued again)");
      world.Flush();
      C(sent == 1 && queued.Count == 0 && !GetStatic<bool>(typeof(ZoneRegen), "flushOwed"), "Flush sends the destroy queue, once, and starts the set afresh: the queue is not owed any more");
    }
    finally
    {
      World.Close();
    }
  }

  private static void PieceTests()
  {
    Section("a piece the player at this machine places while a run goes on");
    using var seams = new Seams();
    var placed = GetStatic<HashSet<Vector2s>>(typeof(ZoneRegen), "placedDuringRun");
    placed.Clear();
    var piece = Make<Piece>();
    var other = Make<Piece>();
    var where = new Dictionary<Piece, Vector3> { [piece] = new Vector3(3 * 64f, 10f, -2 * 64f), [other] = new Vector3(-1 * 64f, 0f, 2 * 64f) };
    ZoneRegen.PiecePosition = p => where[p];
    ZoneRegen.NotePlaced(piece);
    C(placed.Count == 0, "no run under way: the piece is not noted");
    SetStatic(typeof(ZoneRegen), "job", new ZoneRegen.Job(_ => { }));
    ZoneRegen.NotePlaced(piece);
    C(placed.SetEquals([Z(3, -2)]), "a run under way notes the zone the piece stands in");
    var postfix = typeof(ZoneRegenPatch).GetMethod("SetCreatorPostfix", BindingFlags.NonPublic | BindingFlags.Static)!;
    postfix.Invoke(null, [other]);
    C(placed.SetEquals([Z(3, -2), Z(-1, 2)]), "Piece.SetCreator's postfix notes the piece it is given");
    ZoneRegen.PiecePosition = _ => throw new InvalidOperationException("the piece has no transform");
    var log = LogHandler.During(() => postfix.Invoke(null, [piece]));
    C(placed.Count == 2 && log.Any(l => l.StartsWith("[Error]") && l.Contains("a piece placed could not be noted") && l.Contains("the piece has no transform")),
      "a piece that cannot be noted is logged and the placing goes on: " + string.Join(" | ", log));
    SetStatic(typeof(ZoneRegen), "job", null);
    var quiet = LogHandler.During(() => postfix.Invoke(null, [piece]));
    C(quiet.Count == 0 && placed.Count == 2, "(a run that is over notes nothing, and logs nothing)");
  }

  private static void AliveTests()
  {
    Section("a world that is there: every part of it, and nothing that needs it once it is gone");
    using var seams = new Seams();
    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      var world = new ZoneRegen.GameWorld(new ZoneRegen.Job(_ => { }));
      C(world.Alive, "all four parts there: the world is alive");
      foreach (var (name, type, field) in new[] { ("ZNet", typeof(ZNet), "m_instance"), ("ZoneSystem", typeof(ZoneSystem), "s_instance"), ("ZNetScene", typeof(ZNetScene), "s_instance") })
      {
        var kept = type.GetField(field, Any)!.GetValue(null);
        SetStatic(type, field, null);
        C(!world.Alive, $"without its {name} the world is gone");
        SetStatic(type, field, kept);
      }
      var man = ZDOMan.instance;
      SetStatic(typeof(ZDOMan), "s_instance", Make<ZDOMan>());
      C(!world.Alive, "with another ZDOMan (a world that was left and loaded again) it is gone");
      SetStatic(typeof(ZDOMan), "s_instance", man);
      C(world.Alive, "(and alive again with its own)");

      w.Peer(w.Add(Z(0, 0), "Player", user: 700L), 700L);
      C(ZoneRegen.ObjectsPerFrameNow() == ZoneRegen.ObjectsPerFrameWithPlayers, "(a player is connected: the frames are small)");
      World.Close();
      C(!world.Alive, "the world is closed");
      C(world.LiveProtectors().Count() == 0, "a closed world has nobody near, and asking does not throw");
      C(ZoneRegen.ObjectsPerFrameNow() == ZoneRegen.ObjectsPerFrame, "and a frame's size can still be asked for: the full size, with nobody connected");
    }
    finally
    {
      World.Close();
    }
  }

  // ------------------------------------------------------------------------------------------------ requests

  // Unity's scheduler as far as the run needs it: a coroutine runs its first frame when it is started, goes on one frame at a time, and
  // one that is stopped is dropped as it is, without its finally blocks.
  private sealed class Scheduler
  {
    public readonly List<(Coroutine Token, IEnumerator Routine)> Running = [];
    public int Started, Stopped;

    public Coroutine Start(IEnumerator routine)
    {
      Started++;
      var token = Make<Coroutine>();
      // (Coroutine's finalizer releases a handle in the engine that this one never had.)
      GC.SuppressFinalize(token);
      Running.Add((token, routine));
      if (!routine.MoveNext())
        Running.RemoveAll(r => ReferenceEquals(r.Token, token));
      return token;
    }

    public void Stop(Coroutine token)
    {
      Stopped++;
      Running.RemoveAll(r => ReferenceEquals(r.Token, token));
    }

    public bool Has(Coroutine? token) => Running.Any(r => ReferenceEquals(r.Token, token));

    public void Tick()
    {
      foreach (var running in Running.ToList())
        if (Has(running.Token) && !running.Routine.MoveNext())
          Running.RemoveAll(r => ReferenceEquals(r.Token, running.Token));
    }

    // Frames until nothing runs (or the guard says it never will).
    public int Finish(World? world = null, int most = 500)
    {
      int frames = 0;
      while (Running.Count > 0 && frames++ < most)
      {
        Tick();
        world?.Flush();
      }
      return frames;
    }
  }

  // How many times a run that ended well touched the grass and the minimap.
  private static int touches;

  // Whether this machine runs the world is a static of ZNet, true unless the game was joined; a client sets it false.
  private static World ServerWorld(bool server = true, bool betterContinents = true)
  {
    SetStatic(typeof(ZNet), "m_isServer", server);
    BC.Settings = new BC.BetterContinentsSettings { EnabledForThisWorld = betterContinents };
    return new World();
  }

  private static string UiKey => (string)typeof(ZoneRegen).GetField("UiKey", Any)!.GetRawConstantValue()!;
  private static ZoneRegen.Job? CurrentJob => GetStatic<ZoneRegen.Job?>(typeof(ZoneRegen), "job");

  private static void RequestTests()
  {
    Section("requests: refused where there is no world to regenerate, started, started over, and stopped by an error");
    using var seams = new Seams();
    var sched = new Scheduler();
    ZoneRegen.StartRoutine = sched.Start;
    ZoneRegen.StopRoutine = sched.Stop;
    ZoneRegen.FrameClock = () => 0.0;
    touches = 0;
    ZoneRegen.Finish = () => touches++;
    var said = new List<string>();

    // No world, or part of one: the same line, and nothing starts.
    ZDOExtraData.Reset();
    World.Close();
    ZoneRegen.Request(said.Add);
    C(said.SequenceEqual(["Zone regeneration: load a world first."]) && sched.Started == 0 && CurrentJob == null, "no world: it says to load one and starts nothing");
    var defaulted = LogHandler.During(() => ZoneRegen.Request());
    C(defaulted.SequenceEqual(["[Log] [BetterContinents] Zone regeneration: load a world first."]), "a request that is given nowhere to say it says it to the console, which is also the log: " + string.Join(" | ", defaulted));
    foreach (var (name, type, field) in new[] { ("ZNet", typeof(ZNet), "m_instance"), ("ZDOMan", typeof(ZDOMan), "s_instance"), ("ZoneSystem", typeof(ZoneSystem), "s_instance"), ("ZNetScene", typeof(ZNetScene), "s_instance") })
    {
      ZDOExtraData.Reset();
      var half = ServerWorld();
      try
      {
        half.Add(Z(0, 0), "Pine");
        half.Generated.Add(Z(0, 0));
        SetStatic(type, field, null);
        said.Clear();
        ZoneRegen.Request(said.Add);
        C(said.SequenceEqual(["Zone regeneration: load a world first."]) && sched.Started == 0 && CurrentJob == null, $"a world without its {name}: the same, and nothing starts");
      }
      finally
      {
        World.Close();
      }
    }
    ZDOExtraData.Reset();
    var client = ServerWorld(server: false);
    try
    {
      client.Add(Z(0, 0), "Pine");
      client.Generated.Add(Z(0, 0));
      said.Clear();
      ZoneRegen.Request(said.Add);
      C(said.SequenceEqual(["Zone regeneration: it runs on the machine that runs the world: in single player, or on the host."]) && sched.Started == 0 && CurrentJob == null && client.DestroyQueue.Count == 0,
        "a client: it says where it runs, starts nothing, and destroys nothing: " + string.Join(" | ", said));
    }
    finally
    {
      World.Close();
    }

    // A world without Better Continents is the game's own: refused, nothing starts, nothing is destroyed.
    ZDOExtraData.Reset();
    var vanilla = ServerWorld(betterContinents: false);
    try
    {
      vanilla.Add(Z(0, 0), "Pine");
      vanilla.Generated.Add(Z(0, 0));
      said.Clear();
      ZoneRegen.Request(said.Add);
      C(said.SequenceEqual(["Zone regeneration: this world does not use Better Continents."]) && sched.Started == 0 && CurrentJob == null && vanilla.DestroyQueue.Count == 0,
        "a world without Better Continents: it says so, starts nothing, and destroys nothing: " + string.Join(" | ", said));
    }
    finally
    {
      World.Close();
    }

    // A run, from the request to its end.
    ZDOExtraData.Reset();
    var w = ServerWorld();
    try
    {
      var zones = new[] { Z(0, 0), Z(10, 0), Z(20, 0) };
      foreach (var zone in zones)
      {
        for (int i = 0; i < 5; i++)
          w.Add(zone, "Pine", dx: i * 4f - 8f);
        w.Generated.Add(zone);
      }
      said.Clear();
      var lines = new List<string>();
      var log = LogHandler.During(() => ZoneRegen.Request(lines.Add));
      C(sched.Started == 1 && sched.Running.Count == 1 && CurrentJob != null && CurrentJob.Coroutine != null && sched.Has(CurrentJob.Coroutine), "a request starts one coroutine and is the current job, which knows its coroutine");
      C(lines.Count == 1 && lines[0].StartsWith("Zone regeneration: resetting 3 of 3 generated zones") && w.DestroyQueue.Count == 0 && w.Sent.Count == 0,
        "the first frame's work runs when the coroutine starts: the plan is made and said, and nothing is destroyed yet: " + string.Join(" | ", lines));
      C(UI.Exists(UiKey), "the progress is shown while it runs");
      C(log.Count == 0, "(a first request is not 'starting over')");
      GetStatic<HashSet<Vector2s>>(typeof(ZoneRegen), "placedDuringRun").Add(Z(99, 99));
      int frames = sched.Finish(w);
      C(frames > 0 && sched.Running.Count == 0 && CurrentJob == null && !UI.Exists(UiKey), $"when it is done the coroutine has ended, there is no current job, and nothing is shown ({frames} frames)");
      C(lines.Last().StartsWith("Zone regeneration finished in") && lines.Last().Contains("3 zones reset") && touches == 1 && w.Generated.Count == 0, "and it said so, once: " + lines.Last());
      C(GetStatic<HashSet<Vector2s>>(typeof(ZoneRegen), "placedDuringRun").Count == 0, "what the player placed during a run is forgotten when it ends");
    }
    finally
    {
      World.Close();
    }

    SecondRequestTests(sched);
    ErrorTests(sched);
  }

  // A world of four zones, the first of 250 objects, and a player far away so that a frame destroys 100 objects at most.
  private static (World World, ZDO Far) BigServerWorld()
  {
    ZDOExtraData.Reset();
    var w = ServerWorld();
    // The zones are done nearest that player first: (5, 0), (1, 0), the big one (0, 0), and (-1, 0) after it, which keeps (0, 0) waiting.
    var far = w.Add(Z(40, 40), "Player", user: 700L);
    far.SetOwner(700L);
    w.Peer(far, 700L);
    foreach (var zone in new[] { Z(0, 0), Z(1, 0), Z(5, 0), Z(-1, 0) })
    {
      int count = zone.Equals(Z(0, 0)) ? 250 : 5;
      for (int i = 0; i < count; i++)
        w.Add(zone, "Pine", dx: i % 20 - 10f, dz: i / 20 - 6f);
      w.Generated.Add(zone);
    }
    return (w, far);
  }

  private static void SecondRequestTests(Scheduler sched)
  {
    var (w, _) = BigServerWorld();
    try
    {
      var first = new List<string>();
      var second = new List<string>();
      ZoneRegen.Request(first.Add);
      var job1 = CurrentJob!;
      var coroutine1 = job1.Coroutine!;
      int guard = 0;
      while (guard++ < 200 && !(job1.Work is { InFlight: true } && w.LiveIn(Z(0, 0)).Count is > 0 and < 250))
      {
        sched.Tick();
        w.Flush();
      }
      C(job1.Work is { InFlight: true } && w.LiveIn(Z(0, 0)).Count is > 0 and < 250, $"(the first run is in the middle of the big zone: {w.LiveIn(Z(0, 0)).Count} of 250 objects left)");
      // A zone has generated since (the player walked): the second run will have more to do than the first would.
      for (int i = 0; i < 300; i++)
        w.Add(Z(30, 0), "Pine", dx: i % 20 - 10f, dz: i / 20 - 6f);
      w.Generated.Add(Z(30, 0));
      int firstLines = first.Count;
      int stopped = sched.Stopped;

      var log = LogHandler.During(() => ZoneRegen.Request(second.Add));
      var job2 = CurrentJob!;
      C(log.Count(l => l == "[Log] [BetterContinents] Zone regeneration: starting over.") == 1, "a second request logs that it starts over, once: " + string.Join(" | ", log));
      C(sched.Stopped == stopped + 1 && !sched.Has(coroutine1) && sched.Started == 3 && sched.Has(job2.Coroutine), "the first run's coroutine is stopped, and the second one started");
      C(!ReferenceEquals(job1, job2) && job2.Coroutine != coroutine1, "the current job is the new one");
      var bigIds = w.ByZone[Z(0, 0)].Select(z => z.m_uid).ToHashSet();
      C(w.All.Count(bigIds.Contains) == 250, "the zone that was being emptied was finished off first: all 250 of its objects are destroyed");
      var memory = GetStatic<ZoneReset.Memory>(typeof(ZoneRegen), "memory");
      // The zone waits for its neighbour (-1, 0), which has not had its turn: it is emptied and not finished, and the next run carries it.
      C(memory.Pending.Contains(Z(0, 0)) && w.Generated.Contains(Z(0, 0)), "it is remembered as emptied and not finished, still generated");
      C(UI.Exists(UiKey), "the progress is shown for the new run");
      C(second.Count == 1 && second[0].Contains("resetting") && second[0].Contains("generated zones"), "the new run has made its plan from the world as it is: " + string.Join(" | ", second));

      // The first run is not advanced again; the second one carries on to its end, with the progress shown all the while.
      var firstProgress = (job1.Progress.Done, job1.Progress.Destroyed, job1.Progress.Reset);
      bool shown = true;
      int working = 0;
      while (sched.Running.Count > 0 && working++ < 50)
      {
        sched.Tick();
        w.Flush();
        if (sched.Running.Count > 0)
          shown &= UI.Exists(UiKey);
      }
      C(working >= 3 && shown, $"the second run takes several frames ({working}), and the progress stays shown through all of them: a stopped run does not take it away");
      C(first.Count == firstLines && (job1.Progress.Done, job1.Progress.Destroyed, job1.Progress.Reset) == firstProgress, "the first run does not go on: it says nothing more and counts nothing more");
      C(!first.Any(l => l.Contains("finished")) && second.Last().StartsWith("Zone regeneration finished in") && second.Last().Contains("3 zones reset") && second.Last().Contains("305 objects removed"),
        "only the second run finishes, and says so: " + second.Last());
      C(w.Generated.Count == 0 && memory.Pending.Count == 0, "every zone is reset in the end, the one emptied by the first run included, and none is left waiting");
      C(CurrentJob == null && !UI.Exists(UiKey) && sched.Running.Count == 0, "and nothing runs and nothing is shown");
    }
    finally
    {
      World.Close();
    }
  }

  private static void ErrorTests(Scheduler sched)
  {
    // An error in the work: the zone being emptied is finished, the run ends, and the next request starts afresh.
    ZDOExtraData.Reset();
    var w = ServerWorld();
    try
    {
      for (int i = 0; i < 100; i++)
        w.Add(Z(0, 0), "Pine", dx: i % 20 - 10f, dz: i / 20 - 2f);
      w.Add(Z(10, 0), "Pine");
      w.Generated.Add(Z(0, 0));
      w.Generated.Add(Z(10, 0));
      bool broken = false;
      int destroyed = 0;
      ZoneRegen.FindView = _ =>
      {
        if (++destroyed == 5)
          broken = true;
        return null;
      };
      ZoneRegen.FrameClock = () => broken ? throw new InvalidOperationException("the clock failed") : 0.0;
      int touchedBefore = touches;
      var lines = new List<string>();
      var log = LogHandler.During(() =>
      {
        ZoneRegen.Request(lines.Add);
        sched.Finish(w);
      });
      C(lines.Last() == "Zone regeneration stopped by an error, see the log.", "an error in the work is said, in a line: " + lines.Last());
      C(log.Any(l => l.StartsWith("[Error]") && l.Contains("Zone regeneration stopped:") && l.Contains("the clock failed")), "... and the error is in the log: " + string.Join(" | ", log.Select(l => l.Length > 120 ? l[..120] : l)));
      C(w.All.Count(id => w.ByZone[Z(0, 0)].Any(z => z.m_uid == id)) == 100, "the zone that was being emptied is emptied to its last object, not left half done");
      C(w.Generated.SetEquals([Z(10, 0)]), "... and finished: the other zone had not had its turn and stays as it is");
      C(CurrentJob == null && !UI.Exists(UiKey) && sched.Running.Count == 0, "the run is over: no current job, nothing shown, no coroutine");
      C(touches == touchedBefore, "(the grass and the minimap are not redrawn for a run that failed)");

      // The next request is a fresh one.
      broken = false;
      lines.Clear();
      ZoneRegen.Request(lines.Add);
      sched.Finish(w);
      C(lines.Last().StartsWith("Zone regeneration finished in") && w.Generated.Count == 0, "after the error a new request runs to its end: " + lines.Last());
    }
    finally
    {
      World.Close();
    }

    // The zone being emptied cannot be finished when a second request stops the run: that is logged, and the new run starts all the same.
    ZDOExtraData.Reset();
    w = ServerWorld();
    try
    {
      w.Add(Z(0, 0), "Pine");
      w.Generated.Add(Z(0, 0));
      var failing = new FakeWorld();
      uint next = 1;
      failing.Fill([Z(0, 0)], 100, ref next);
      var plan = ZoneReset.MakePlan([Z(0, 0)], [], []);
      var work = new ZoneReset.Work(failing, plan, new Budget(10, 1_000_000, () => 0), new Progress(), new ZoneReset.Memory());
      var steps = work.Steps();
      steps.MoveNext();
      C(work.InFlight, "(a zone is half emptied)");
      failing.OnDestroy = _ => throw new InvalidOperationException("cannot destroy");
      var old = new ZoneRegen.Job(_ => { }) { Work = work };
      SetStatic(typeof(ZoneRegen), "job", old);
      var lines = new List<string>();
      Exception? thrown = null;
      var log = LogHandler.During(() =>
      {
        try
        {
          ZoneRegen.Request(lines.Add);
        }
        catch (Exception e)
        {
          thrown = e;
        }
      });
      C(thrown == null && log.Any(l => l.StartsWith("[Error]") && l.Contains("the zone being emptied could not be finished") && l.Contains("cannot destroy")), "a zone that cannot be finished is logged, and the request goes on: " + (thrown?.Message ?? string.Join(" | ", log)));
      C(lines.Count == 1 && lines[0].StartsWith("Zone regeneration: resetting") && CurrentJob != null && !ReferenceEquals(CurrentJob, old), "the new run has started in its place");
    }
    finally
    {
      World.Close();
    }
  }

  // ------------------------------------------------------------------------------------------------ a world that closes

  private static void GoneTests()
  {
    Section("a world that closes: inside a zone, among the borders, during the scan, and as the work ends");
    using var seams = new Seams();

    // Inside a zone that takes several frames: nothing more is destroyed once the world is gone.
    bool alive = true;
    var world = new FakeWorld { IsAlive = () => alive };
    uint next = 1;
    world.Fill([Z(0, 0)], 100, ref next);
    world.Fill([Z(5, 0)], 3, ref next);
    world.OnNewFrame = () => alive = world.Frames != 2;
    var plan = ZoneReset.MakePlan([Z(0, 0), Z(5, 0)], [], [Z(0, 0)]);
    var (_, progress, _) = Go(world, plan, new Budget(30, 1_000_000, () => 0));
    C(progress.Aborted && world.Destroyed.Count == 29 && progress.Reset == 0, $"the world closes in the frame after the first 29 objects of a zone: the work stops there ({world.Destroyed.Count} destroyed)");
    C(!world.Calls.Any(c => c.StartsWith("unplace") || c.StartsWith("ungenerate") || c.StartsWith("root")) && world.Frames == 2, "... and finishes nothing in a world that is not there");

    // Among the borders: no more of them are mended.
    alive = true;
    world = new FakeWorld { IsAlive = () => alive };
    plan = ZoneReset.MakePlan(Square(2), [Z(0, 0)], [Z(0, 0)]);
    int atFlip = -1;
    world.OnNewFrame = () =>
    {
      if (world.Frames == 4)
      {
        alive = false;
        atFlip = world.BorderCalls.Count;
      }
    };
    (_, progress, _) = Go(world, plan, new Budget(ZoneReset.BorderCost, 1_000_000, () => 0));
    C(progress.Aborted && atFlip is > 0 and < 8 && world.BorderCalls.Count == atFlip, $"the world closes among the borders: the ones mended so far stay, no more are ({atFlip} of 8 mended)");

    // During the scan, on the game's own classes: the work stops at once and says so, before it makes a plan.
    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      foreach (var zone in Square(1))
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      int clockCalls = 0;
      // The 50th look at the clock says the frame is long over; every other says no time has passed.
      ZoneRegen.FrameClock = () => ++clockCalls == 50 ? 100.0 : 0.0;
      var lines = new List<string>();
      var job = new ZoneRegen.Job(lines.Add);
      var steps = ZoneRegen.Steps(job);
      C(steps.MoveNext() && job.Progress.Total == 0 && lines.Count == 0 && clockCalls == 50, $"(the scan is spread over frames: the first ends before the plan, after {clockCalls} looks at the clock)");
      World.Close();
      int before = clockCalls;
      var log = LogHandler.During(() =>
      {
        while (steps.MoveNext())
        {
        }
      });
      C(log.Contains("[Log] [BetterContinents] Zone regeneration: stopped, the world closed.") && lines.Count == 0 && job.Progress.Total == 0 && job.Work == null,
        "a world that closes during the scan: it is logged, no plan is made, nothing is said, nothing is begun: " + string.Join(" | ", log));
      C(clockCalls - before < 5, $"and the scan does not go on through the rest of a world that is gone ({clockCalls - before} more looks at the clock)");
    }
    finally
    {
      World.Close();
    }

    // As the very last object goes: the run ends as stopped, and the grass and the minimap are not redrawn.
    ZDOExtraData.Reset();
    touches = 0;
    ZoneRegen.FrameClock = () => 0.0;
    ZoneRegen.Finish = () => touches++;
    w = new World();
    try
    {
      w.Add(Z(0, 0), "Pine");
      w.Generated.Add(Z(0, 0));
      ZoneRegen.FindView = _ =>
      {
        SetStatic(typeof(ZNet), "m_instance", null);
        return null;
      };
      var (lines, _, job) = Regenerate(w);
      C(job.Progress.Reset == 1 && w.Generated.Count == 0, "(the last zone was reset)");
      C(job.Progress.Aborted && touches == 0 && lines.Last() == "Zone regeneration stopped after 1 of 1 zone: the world closed.", "a world that closed as the work ended: it says it stopped, and redraws nothing: " + lines.Last());
    }
    finally
    {
      World.Close();
    }
  }

  // ------------------------------------------------------------------------------------------------ where the work begins

  private static void FocusTests()
  {
    Section("the work begins where the players are: the player at this machine, a connected player, one who is still joining");
    using var seams = new Seams();
    ZoneRegen.FrameClock = () => 0.0;
    var scenarios = new (string Name, Action<World> Setup, Vector2s[] Focus, (Vector2s Zone, int Radius)[] Protectors)[]
    {
      ("the player at this machine", w =>
      {
        var me = w.Add(Z(8, -8), "Player", user: 600L);
        SetField(w.Net, "m_characterID", me.m_uid);
      }, [Z(8, -8)], []),
      ("a player who is joining and has not entered the world (the zone their client says)", w =>
      {
        var joining = Make<ZNetPeer>();
        joining.m_uid = 800L;
        joining.m_refPos = new Vector3(-8 * 64f, 30f, 8 * 64f);
        GetField<List<ZNetPeer>>(w.Net, "m_peers").Add(joining);
      }, [Z(-8, 8)], [(Z(-8, 8), 1)]),
      ("a connected player (their character)", w => w.Peer(w.Add(Z(-8, -8), "Player", user: 700L), 700L), [Z(-8, -8)], [(Z(-8, -8), 1)]),
      ("the player at this machine and one who is joining", w =>
      {
        var me = w.Add(Z(8, -8), "Player", user: 600L);
        SetField(w.Net, "m_characterID", me.m_uid);
        var joining = Make<ZNetPeer>();
        joining.m_uid = 800L;
        joining.m_refPos = new Vector3(-8 * 64f, 30f, 8 * 64f);
        GetField<List<ZNetPeer>>(w.Net, "m_peers").Add(joining);
      }, [Z(8, -8), Z(-8, 8)], [(Z(-8, 8), 1)]),
    };
    foreach (var (name, setup, focus, protectors) in scenarios)
    {
      ZDOExtraData.Reset();
      var w = new World();
      try
      {
        var zoneOf = new Dictionary<ZDOID, Vector2s>();
        foreach (var zone in Square(10))
        {
          zoneOf[w.Add(zone, "Pine").m_uid] = zone;
          w.Generated.Add(zone);
        }
        setup(w);
        Regenerate(w);
        var order = w.All.Where(zoneOf.ContainsKey).Select(id => zoneOf[id]).ToList();
        var kept = Square(10).Where(z => protectors.Any(p => Math.Max(Math.Abs(z.x - p.Zone.x), Math.Abs(z.y - p.Zone.y)) <= p.Radius)).ToHashSet();
        long Distance(Vector2s z) => focus.Min(f => (long)(z.x - f.x) * (z.x - f.x) + (long)(z.y - f.y) * (z.y - f.y));
        var expected = Square(10).Where(z => !kept.Contains(z)).OrderBy(Distance).ThenBy(z => z.x).ThenBy(z => z.y).ToList();
        C(order.SequenceEqual(expected), $"{name}: the zones go nearest to them first, ties by x then y (first: {Show(order.Take(4))}; expected {Show(expected.Take(4))})");
        C(order.Count == 441 - kept.Count, $"... every zone but the {kept.Count} kept ones ({order.Count})");
      }
      finally
      {
        World.Close();
      }
    }
  }

  // ------------------------------------------------------------------------------------------------ progress

  private static void ProgressLineTests()
  {
    Section("the plan's frame and the progress lines: one for each tenth, from 20 zones, never for the whole");
    using var seams = new Seams();
    foreach (int count in new[] { 19, 20, 23, 37, 40, 100 })
    {
      ZDOExtraData.Reset();
      var w = new World();
      try
      {
        // count - 1 zones in a grid, three apart so that none is beside another, two trees each; and one beside a base (a piece two zones
        // east of it): the base's zones stay, and the ground along that zone is mended after the last reset, in frames of its own.
        for (int i = 0; i < count - 1; i++)
        {
          var zone = Z(i % 20 * 3, i / 20 * 3);
          w.Add(zone, "Pine");
          w.Add(zone, "Pine", dx: 5f);
          w.Generated.Add(zone);
        }
        w.Add(Z(-52, 0), "Pine");
        w.Add(Z(-52, 0), "Pine", dx: 5f);
        w.Generated.Add(Z(-52, 0));
        Creator(w.Add(Z(-50, 0), "piece_chest"), 42L);
        foreach (var zone in new[] { Z(-50, 0), Z(-51, -1), Z(-51, 0), Z(-51, 1) })
          w.Generated.Add(zone);

        double now = 0;
        bool ticking = false;
        // Until the plan is made no time passes; then every look at the clock says the frame is over.
        ZoneRegen.FrameClock = () => ticking ? now += 10.0 : 0.0;
        var lines = new List<string>();
        var job = new ZoneRegen.Job(lines.Add);
        var steps = ZoneRegen.Steps(job);
        bool more = steps.MoveNext();
        if (count == 20)
          C(more && lines.Count == 1 && lines[0].StartsWith("Zone regeneration: resetting 20 of 24 generated zones") && job.Work == null && job.Progress.Destroyed == 0 && w.DestroyQueue.Count == 0,
            "the first frame makes and says the plan; the work begins in the next: " + string.Join(" | ", lines));
        ticking = true;
        int frames = 1;
        bool sawWhole = false;
        while (steps.MoveNext())
        {
          frames++;
          w.Flush();
          // (A frame that ends with every zone done: the borders are mended in frames of their own.)
          sawWhole |= job.Progress.Percent == 100;
        }
        // One zone is done in each frame, two trees each. A line comes the first time the zones done are a tenth, two tenths, ... nine tenths
        // of all of them, in a run of 20 zones or more.
        var expected = new List<string>();
        if (count >= 20)
          for (int tenth = 1; tenth <= 9; tenth++)
          {
            int done = Enumerable.Range(1, count).First(k => k * 100 >= tenth * 10 * count);
            expected.Add($"Zone regeneration: {done} of {count} zones ({done * 100 / count}%), {done * 2} objects removed.");
          }
        var progress = lines.Where(l => l.Contains(" of " + count + " zones (")).ToList();
        C(progress.SequenceEqual(expected), $"{count} zones: {expected.Count} progress lines, one at each tenth: " + (progress.Count > 0 ? progress[0] + " ... " + progress[^1] : "(none)"));
        C(!lines.Any(l => l.Contains("(100%)")) && !lines.Any(l => l.Contains("(0%)")), $"{count} zones: no line for nothing done or for the whole");
        C(sawWhole && lines.Last().StartsWith("Zone regeneration finished in") && lines.Last().Contains($"{count} zones reset"),
          $"{count} zones: the last frames end with every zone done (the borders), and then comes the finish line ({frames} frames): " + lines.Last());
      }
      finally
      {
        World.Close();
      }
    }

    // A frame that ends at 99% of the zones gets its line like any other.
    ZDOExtraData.Reset();
    var nearly = new World();
    try
    {
      for (int i = 0; i < 100; i++)
      {
        var zone = Z(i % 20 * 3, i / 20 * 3);
        nearly.Add(zone, "Pine");
        nearly.Generated.Add(zone);
      }
      int work = -1;
      // Once the plan is made, 1 + 99 looks at the clock find the frame young (the frame's start, and one for each of 99 zones); the next, the 100th zone's, finds it over.
      ZoneRegen.FrameClock = () => work < 0 ? 0.0 : work++ == 100 ? 100.0 : 0.0;
      var lines = new List<string>();
      var job = new ZoneRegen.Job(lines.Add);
      var steps = ZoneRegen.Steps(job);
      steps.MoveNext();
      work = 0;
      while (steps.MoveNext())
        nearly.Flush();
      var progress = lines.Where(l => l.Contains(" of 100 zones (")).ToList();
      C(progress.SequenceEqual(["Zone regeneration: 99 of 100 zones (99%), 99 objects removed."]), "a frame that ends with 99 of 100 zones done says so, once: " + string.Join(" | ", progress));
    }
    finally
    {
      World.Close();
    }

    // The first frame has its whole time: a clock that starts late does not make it long over, so the scan and the plan are one frame.
    ZDOExtraData.Reset();
    var late = new World();
    try
    {
      foreach (var zone in Square(1))
      {
        late.Add(zone, "Pine");
        late.Generated.Add(zone);
      }
      ZoneRegen.FrameClock = () => 1_000_000.0;
      var lines = new List<string>();
      var job = new ZoneRegen.Job(lines.Add);
      var steps = ZoneRegen.Steps(job);
      C(steps.MoveNext() && job.Progress.Total == 9 && lines.Count == 1, "a clock that starts at a million milliseconds: the first frame still makes the plan");
    }
    finally
    {
      World.Close();
    }

    // The scan is spread over frames of 6 ms each, from the start of the frame it is in: the world's 262,144 sectors take about
    // 43,700 frames when a look at the clock takes a millisecond, and not one frame for each sector.
    ZDOExtraData.Reset();
    var slow = new World();
    try
    {
      slow.Add(Z(0, 0), "Pine");
      slow.Generated.Add(Z(0, 0));
      double now = 0;
      ZoneRegen.FrameClock = () => now += 1.0;
      var job = new ZoneRegen.Job(_ => { });
      var steps = ZoneRegen.Steps(job);
      int frames = 1;
      while (job.Progress.Total == 0 && steps.MoveNext())
        frames++;
      C(frames is > 40_000 and < 47_000, $"a scan that takes a millisecond a sector is spread over about 6 sectors a frame ({frames} frames)");
    }
    finally
    {
      World.Close();
    }
  }

  // ------------------------------------------------------------------------------------------------ order

  private static void OrderTests()
  {
    Section("the order of a plan: nearest first, then x, then y, whatever order the zones are given in");
    var rng = new System.Random(5);
    int differences = 0;
    for (int trial = 0; trial < 30; trial++)
    {
      var zones = Square(4).OrderBy(_ => rng.Next()).ToList();
      var focus = (trial % 3) switch
      {
        0 => new List<Vector2s> { Z(0, 0) },
        1 => new List<Vector2s> { Z(2, -1), Z(-3, 3) },
        _ => new List<Vector2s>(),
      };
      var protectors = trial % 2 == 0 ? new[] { (ZoneReset.Protector)Z(3, 3) } : [];
      var plan = ZoneReset.MakePlan(zones, protectors, focus);
      var near = focus.Count > 0 ? focus : [Z(0, 0)];
      var kept = ZoneReset.Dilate(protectors).ToHashSet();
      var expected = zones.Where(z => !kept.Contains(z)).OrderBy(z => near.Min(f => (long)(z.x - f.x) * (z.x - f.x) + (long)(z.y - f.y) * (z.y - f.y))).ThenBy(z => z.x).ThenBy(z => z.y).ToList();
      if (!plan.Reset.SequenceEqual(expected))
        differences++;
    }
    C(differences == 0, $"30 plans of 81 zones given in a random order, with one person, two, and none, with and without a base: the same order every time ({differences} differ)");
    // Two zones the same distance and the same x: the lower y first, even when it comes second.
    var two = ZoneReset.MakePlan([Z(1, 1), Z(1, -1), Z(-1, 1), Z(-1, -1)], [], [Z(0, 0)]);
    C(Show(two.Reset) == "-1,-1 -1,1 1,-1 1,1", "four zones the same distance away: by x, then by y: " + Show(two.Reset));
    two = ZoneReset.MakePlan([Z(1, -1), Z(1, 1), Z(-1, -1), Z(-1, 1)], [], [Z(0, 0)]);
    C(Show(two.Reset) == "-1,-1 -1,1 1,-1 1,1", "... however they are listed: " + Show(two.Reset));
  }

  // ------------------------------------------------------------------------------------------------ a spawner's creature

  private static void FollowSpawnTests()
  {
    Section("a spawner's creature: it goes with the spawner unless it stands where the run keeps things");
    // It stands in a zone nobody generated, near nothing: the only way to reach it is through the spawner.
    var world = new FakeWorld();
    world.Add(Z(0, 0), O(1));
    world.Add(Z(30, 30), O(10));
    world.Spawns(1, 10);
    var plan = ZoneReset.MakePlan([Z(0, 0)], [], [Z(0, 0)]);
    var (_, progress, _) = Go(world, plan, Roomy());
    C(world.Destroyed.Contains(Id(10)) && progress.Spawned == 1 && progress.SpawnedKept == 0 && progress.Destroyed == 2, "a creature in a zone that was never generated and is near nothing goes with its spawner");

    // It stands beside a zone that was left alone at its turn (a piece was placed in it after the plan): the run keeps that area whole.
    world = new FakeWorld();
    world.Add(Z(8, 0), O(2));
    world.Add(Z(20, 0), O(1));
    world.Add(Z(9, 0), O(10));
    world.Spawns(1, 10);
    world.OnList = zone =>
    {
      if (zone.Equals(Z(8, 0)) && world.Objects[zone].Count == 1)
        world.Add(zone, O(99, Kind.Piece));
    };
    plan = ZoneReset.MakePlan([Z(8, 0), Z(20, 0)], [], [Z(8, 0)]);
    (_, progress, _) = Go(world, plan, Roomy());
    C(progress.Skipped == 1 && Show(plan.Reset) == "8,0 20,0", "(the zone with the new piece is left alone at its turn, before the spawner's)");
    C(!world.Destroyed.Contains(Id(10)) && progress.SpawnedKept == 1 && progress.Spawned == 0, "a creature in the area around a zone that was left alone for what it holds stays");

    // It stands where a zone is not generated but a base is near: covered, so it stays (the plan's own covered zones).
    world = new FakeWorld();
    world.Add(Z(0, 0), O(1));
    world.Add(Z(40, 40), O(10));
    world.Spawns(1, 10);
    plan = ZoneReset.MakePlan([Z(0, 0)], [Z(41, 40)], [Z(0, 0)]);
    (_, progress, _) = Go(world, plan, Roomy());
    C(!world.Destroyed.Contains(Id(10)) && progress.SpawnedKept == 1, "a creature in a zone beside a base that is not generated stays");

    // A creature that a player made (a piece) or tamed stays wherever it stands.
    foreach (var kind in new[] { Kind.Piece, Kind.Tamed, Kind.Player, Kind.Tombstone, Kind.Ground })
    {
      world = new FakeWorld();
      world.Add(Z(0, 0), O(1));
      world.Add(Z(30, 30), O(10, kind));
      world.Spawns(1, 10);
      (_, progress, _) = Go(world, ZoneReset.MakePlan([Z(0, 0)], [], [Z(0, 0)]), Roomy());
      C(!world.Destroyed.Contains(Id(10)) && progress.SpawnedKept == 1, $"a creature that is a {kind} stays wherever it stands");
    }
  }

  // ------------------------------------------------------------------------------------------------ peers

  private static void ReadyPeerTests()
  {
    Section("a connection that has not finished joining is not a player");
    using var seams = new Seams();
    ZoneRegen.FrameClock = () => 0.0;
    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      foreach (var zone in Square(5))
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      // A connection without an id yet, with a position and a wide view; and a player whose peer has left (nobody owns it).
      var joining = Make<ZNetPeer>();
      joining.m_uid = 0L;
      joining.m_refPos = new Vector3(-3 * 64f, 30f, 3 * 64f);
      joining.m_simulationDistance = new SimulationDistance(4, 2);
      GetField<List<ZNetPeer>>(w.Net, "m_peers").Add(joining);
      var orphan = w.Add(Z(5, -5), "Player", user: 999L);
      orphan.SetOwner(999L);
      C(ZoneRegen.ObjectsPerFrameNow() == ZoneRegen.ObjectsPerFrame, "with only a connection that has not joined yet the frames are full size: nobody is connected");
      var live = new ZoneRegen.GameWorld(new ZoneRegen.Job(_ => { })).LiveProtectors().Select(p => (p.Zone, p.Radius)).Distinct().ToList();
      C(live.Count == 0, "the position of a connection that has not joined keeps nothing: " + string.Join(" ", live.Select(p => $"{p.Zone.x},{p.Zone.y}/{p.Radius}")));

      // A player who is in, with a view of 2 zones.
      var ready = w.Add(Z(0, 0), "Player", user: 700L);
      ready.SetOwner(700L);
      w.Peer(ready, 700L).m_simulationDistance = new SimulationDistance(2, 2);
      C(ZoneRegen.ObjectsPerFrameNow() == ZoneRegen.ObjectsPerFrameWithPlayers, "a player who has joined makes the frames small");
      live = new ZoneRegen.GameWorld(new ZoneRegen.Job(_ => { })).LiveProtectors().Select(p => (p.Zone, p.Radius)).Distinct().ToList();
      C(live.SequenceEqual([(Z(0, 0), 2)]), "the player who has joined keeps the zones their own view has, and the other connection's view of 4 is not theirs: " + string.Join(" ", live.Select(p => $"{p.Zone.x},{p.Zone.y}/{p.Radius}")));
      var (_, _, job) = Regenerate(w);
      var expectedKept = Square(5).Where(z => Math.Max(Math.Abs(z.x), Math.Abs(z.y)) <= 2 || (Math.Abs(z.x - 5) <= 2 && Math.Abs(z.y + 5) <= 2)).ToHashSet();
      C(w.Generated.SetEquals(expectedKept) && job.Progress.Reset == 121 - expectedKept.Count, $"the scan keeps the zones of the player who has joined, and of the orphaned one by the widest view of those who have joined (2, not 4), and none of the connection that has not (left: {w.Generated.Count})");
      C(!w.Generated.Contains(Z(-3, 3)), "(the zone that connection's position is in was reset)");
    }
    finally
    {
      World.Close();
    }
  }

  // ------------------------------------------------------------------------------------------------ words

  // Every string literal in the compiled methods of a type and the types inside it (closures, iterators), which is what the code can say.
  private static List<string> StringsIn(Type root)
  {
    var strings = new List<string>();
    var seen = new HashSet<Type>();
    var queue = new Queue<Type>();
    queue.Enqueue(root);
    while (queue.Count > 0)
    {
      var type = queue.Dequeue();
      if (!seen.Add(type))
        continue;
      foreach (var nested in type.GetNestedTypes(Any))
        queue.Enqueue(nested);
      foreach (var method in type.GetMethods(AnyDeclared).Cast<MethodBase>().Concat(type.GetConstructors(AnyDeclared)))
        strings.AddRange(StringsOf(method));
    }
    return strings;
  }

  private static List<string> StringsOf(MethodBase method) =>
    method.GetMethodBody() == null ? [] : PatchProcessor.GetOriginalInstructions(method).Where(i => i.opcode == OpCodes.Ldstr).Select(i => (string)i.operand).ToList();

  private static void WordsTests()
  {
    Section("words: nothing the regeneration can say names or replaces another mod");
    string[] banned = ["Upgrade World", "Expand World", "Jere", "replace", "obsolete", "no longer need", "needs no other", "instead of"];
    var regen = StringsIn(typeof(ZoneRegen));
    var reset = StringsIn(typeof(ZoneReset));
    var patch = StringsIn(typeof(ZoneRegenPatch));
    var migrate = new List<string>();
    foreach (var method in typeof(SettingsSchema).GetMethods(AnyDeclared).Where(m => m.Name == nameof(SettingsSchema.Migrate)))
      migrate.AddRange(StringsOf(method));
    var gameUtils = StringsOf(typeof(GameUtils).GetMethod(nameof(GameUtils.ResetZones))!).Concat(StringsOf(typeof(GameUtils).GetMethod(nameof(GameUtils.PokeHeightmaps), AnyDeclared)!)).ToList();

    // (The scan reads what it should: lines known to be in each are found.)
    C(regen.Any(t => t.StartsWith("Zone regeneration: ")) && regen.Any(t => t.Contains("it runs on the machine that runs the world")) && regen.Any(t => t.Contains("preparing to regenerate the zones")), $"the scan reads ZoneRegen's strings ({regen.Count})");
    C(reset.Any(t => t.Contains("left alone")) && reset.Any(t => t.Contains("generates again with the new settings")) && reset.Count > 15, $"and ZoneReset's ({reset.Count})");
    C(migrate.Any(t => t.Contains("Debug Reset Command") && t.Contains("zones_reset start")) && migrate.Any(t => t.Contains("Default Heightmap Amount")), $"and the migration's log lines ({migrate.Count})");
    C(gameUtils.Count == 0, "(GameUtils.ResetZones says nothing itself)");

    foreach (var (name, strings) in new[] { ("ZoneRegen", regen), ("ZoneReset", reset), ("ZoneRegenPatch", patch), ("SettingsSchema.Migrate", migrate) })
    {
      var bad = strings.Where(t => banned.Any(b => t.Contains(b, StringComparison.OrdinalIgnoreCase))).ToList();
      C(bad.Count == 0, $"{name}: no string names another mod or says anything is replaced or obsolete" + (bad.Count > 0 ? ": " + string.Join(" | ", bad) : ""));
    }
    // The text a player is shown is not pinned to a game patch or to a patch of this mod ("Valheim 1.0", "Better Continents 0.10").
    var versioned = regen.Concat(reset).Concat(patch).Where(t => Regex.IsMatch(t, @"\b\d+\.\d+\.\d+\b")).ToList();
    C(versioned.Count == 0, "no string of the regeneration pins a patch version" + (versioned.Count > 0 ? ": " + string.Join(" | ", versioned) : ""));
  }

  // ------------------------------------------------------------------------------------------------ what a mutation run found further

  private static void MessageGapTests()
  {
    Section("messages: what is counted, and the singular of the sentences about locations");
    var none = ZoneReset.FinishLine(new Progress { Total = 3, Done = 3, Reset = 0, Skipped = 3 }, 1, 0);
    C(none.Contains("0 zones reset") && !none.Contains("generates again") && none.Contains("3 zones were left alone after all"), "a run that reset nothing does not say that a reset zone generates again: " + none);
    var line = ZoneReset.ProgressLine(new Progress { Total = 10, Done = 6, Reset = 4, Skipped = 2, Destroyed = 7 });
    C(line == "Zone regeneration: 6 of 10 zones (60%), 7 objects removed.", "the progress line counts the zones done, those left alone included: " + line);
    string Locations(int skipped, int forLocations) => ZoneReset.FinishLine(new Progress { Total = 9, Done = 9, Reset = 9 - skipped, Skipped = skipped, SkippedForLocations = forLocations }, 1, 0);
    C(Locations(2, 1).Contains("1 zone was left alone after all") && Locations(2, 1).Contains(" 1 more zone was left alone to keep a location whole."), "one zone beside one for a location: " + Locations(2, 1));
    C(Locations(1, 1).Contains(" 1 zone was left alone to keep a location whole.") && !Locations(1, 1).Contains("more") && !Locations(1, 1).Contains("after all"), "one zone for a location alone: " + Locations(1, 1));
    C(Locations(5, 3).Contains("2 zones were left alone after all") && Locations(5, 3).Contains(" 3 more zones were left alone to keep a location whole."), "several beside several: " + Locations(5, 3));
    C(Locations(3, 3).Contains(" 3 zones were left alone to keep a location whole.") && !Locations(3, 3).Contains("more") && !Locations(3, 3).Contains("after all"), "several for a location alone: " + Locations(3, 3));
    C(ZoneReset.FinishLine(new Progress { Total = 1, Done = 1, Reset = 1 }, 1, 0).EndsWith("A reset zone generates again with the new settings when somebody comes near."), "one zone reset is enough for the closing sentence");
    C(ZoneRegen.ProgressText(new Progress()) == "Better Continents: preparing to regenerate the zones ..." && ZoneRegen.ProgressText(new Progress { Total = 40, Done = 10 }) == "Better Continents: regenerating the zones, 25% ...",
      "the screen says it is preparing until there is a plan, then how far it is");
    C(ZoneReset.FinishLine(new Progress { Total = 1, Done = 1, Reset = 0, Skipped = 1 }, 1, 0).Contains("was left alone after all: a player, or something a player made, was in or beside it before its turn."), "the singular of the 'after all' sentence: it, its");
    C(ZoneReset.FinishLine(new Progress { Total = 2, Done = 2, Reset = 0, Skipped = 2 }, 1, 0).Contains("were left alone after all: a player, or something a player made, was in or beside them before their turn."), "and the plural: them, their");
  }

  private static void PlanGapTests()
  {
    Section("the plan: squares of any radius, keys that differ in one coordinate, circles that just touch, a reach that is negative");
    C(ZoneReset.Dilate([Z(0, 0)], 2).Count == 25 && ZoneReset.Dilate([Z(0, 0), Z(10, 0)], 0).Count == 2 && ZoneReset.Dilate([Z(0, 0)], 3).Contains(Z(3, -3)), "a square of radius 2 is 5 x 5, of radius 0 the zone itself, of radius 3 reaches its corner");
    C(!ZoneReset.Comparer.Equals(Z(1, 2), Z(1, 3)) && !ZoneReset.Comparer.Equals(Z(1, 2), Z(5, 2)) && ZoneReset.Comparer.Equals(Z(1, 2), Z(1, 2)), "zones that share one coordinate are different zones");
    C(Sorted(ZoneReset.TouchedZones(LocIn(0, 0, 20, 0, 12f))) == "0,0 1,0" && Sorted(ZoneReset.TouchedZones(LocIn(0, 0, 20, 0, 11.9f))) == "0,0", "a circle that reaches a zone's edge exactly, 12 m from 20 m to the 32 m edge, touches it; a little less does not");
    C(Sorted(ZoneReset.TouchedZones(LocIn(0, 0, 30, 0, -5f))) == "0,0" && Sorted(ZoneReset.TouchedZones(LocIn(0, 0, 30, 0, 5f))) == "0,0 1,0", "a reach of -5 m is none, though 5 m would reach the next zone");
    C(Sorted(ZoneReset.TouchedZones(new ZoneReset.PlacedLocation(Z(7, 7), 0f, 0f, 0.5f))) == "0,0 7,7", "a pin whose home is elsewhere touches the zone it stands in, however small it is");
    C(Sorted(ZoneReset.TouchedZones(LocIn(0, 0, 31.5f, 0, 0f))) == "0,0" && Sorted(ZoneReset.TouchedZones(LocIn(0, 0, 31.5f, 0, 0.6f))) == "0,0 1,0", "a location with no reach touches its zone only, even half a metre from the next");
    C(Sorted(ZoneReset.TouchedZones(LocIn(0, 0, -20, 0, 12.5f))) == "-1,0 0,0" && Sorted(ZoneReset.TouchedZones(LocIn(0, 0, -20, 0, 11.9f))) == "0,0"
      && Sorted(ZoneReset.TouchedZones(LocIn(0, 0, 0, -20, 12.5f))) == "0,-1 0,0", "west and south: a circle that reaches 0.5 m over the edge touches the zone beyond, one that stops short does not");

    // Every zone a circle touches, and no other, for circles all over: the zones whose square is within the radius of the centre.
    var rng = new System.Random(77);
    int wrong = 0, compared = 0;
    for (int n = 0; n < 4000; n++)
    {
      float x = (float)(rng.NextDouble() * 800 - 400), z = (float)(rng.NextDouble() * 800 - 400), r = (float)(rng.NextDouble() * 300 + 0.1);
      var home = Z((int)Math.Floor((x + 32.0) / 64.0), (int)Math.Floor((z + 32.0) / 64.0));
      var expected = new HashSet<Vector2s> { home };
      bool exact = false;
      for (int zx = -16; zx <= 16; zx++)
        for (int zy = -16; zy <= 16; zy++)
        {
          double dx = Math.Max(Math.Abs(x - zx * 64.0) - 32.0, 0.0), dz = Math.Max(Math.Abs(z - zy * 64.0) - 32.0, 0.0);
          double distance = Math.Sqrt(dx * dx + dz * dz);
          // (A circle that touches a square at one point exactly is decided by rounding; those few are left out.)
          exact |= Math.Abs(distance - r) < 1e-3;
          if (distance <= r)
            expected.Add(Z(zx, zy));
        }
      if (exact)
        continue;
      compared++;
      if (!expected.SetEquals(ZoneReset.TouchedZones(new ZoneReset.PlacedLocation(home, x, z, r))))
        wrong++;
    }
    C(compared > 3900 && wrong == 0, $"4,000 circles of 0.1 to 300 m placed anywhere: each touches exactly the zones its radius reaches ({wrong} of {compared} differ)");
  }

  private static void GroundGapTests()
  {
    Section("ground: the first vertex, the smallest grids, the bytes after the paint");
    static bool Has(byte[]? data) => ZoneReset.TerrainBorder.HasEdits(data);
    C(Has(TcData((r, c) => r == 0 && c == 0)), "a compiler whose only edit is its first vertex is ground work");
    C(Has(TcData((r, c) => false, (r, c) => r == 0 && c == 0)), "... and one whose only paint is its first vertex");
    C(Has(TcData((r, c) => r == 1 && c == 1, pitch: 2)) && !Has(TcData((r, c) => true, pitch: 1)) && !Has(TcData((r, c) => true, (r, c) => true, pitch: 0)), "a grid of 2 x 2 is read, one of 1 x 1 or none is not a grid");
    C(Has(TcData((r, c) => false, (r, c) => r == 64 && c == 64)), "... and one whose only paint is its last vertex");
    var tiny = ZoneReset.TerrainBorder.Clear(TcData((r, c) => true, pitch: 2), Edges.North, out int tinyCleared);
    C(tiny != null && tinyCleared == 2 && ZoneReset.TerrainBorder.Clear(TcData((r, c) => true, pitch: 1), Edges.North, out int single) == null && single == 0, "the ground at a border of a grid of 2 x 2 is cut; of 1 x 1 it is not");
    // A grid that is not square, written well: 50 vertices, the one at row 6, column 3 edited.
    var odd = new ZPackage();
    odd.Write(1);
    odd.Write(1);
    odd.Write(Vector3.zero);
    odd.Write(1f);
    odd.Write(50);
    for (int i = 0; i < 50; i++)
    {
      odd.Write(i == 45);
      if (i == 45)
      {
        odd.Write(0.5f);
        odd.Write(0.25f);
      }
    }
    odd.Write(50);
    for (int i = 0; i < 50; i++)
      odd.Write(false);
    var oddData = Utils.Compress(odd.GetArray());
    C(!Has(oddData) && ZoneReset.TerrainBorder.Clear(oddData, Edges.North, out int oddCleared) == null && oddCleared == 0, "a grid that is not square is not read, even when it holds an edit that would be on the edge of a square one");
    C(ZoneReset.TerrainBorder.Clear(null!, Edges.North, out int none) == null && none == 0, "no data at all: nothing to clear");
    var tail = ZoneReset.TerrainBorder.Clear(TcData((r, c) => true, null, trailing: [42]), Edges.North, out _);
    C(tail != null && ReadTc(tail).Rest.SequenceEqual(new byte[] { 42 }), "a single byte after the paint is carried over");
    var noTail = ZoneReset.TerrainBorder.Clear(TcData((r, c) => true), Edges.North, out _);
    C(noTail != null && ReadTc(noTail).Rest.Length == 0, "(and none is made up)");
  }

  private static void WorkGapTests()
  {
    Section("the work: a slice of nothing, carried zones among the zones that stay, borders in a save, Execute");
    uint next = 1;
    // A frame of one object takes one object each: no frame is spent without destroying anything.
    var world = new FakeWorld();
    world.Fill([Z(0, 0)], 5, ref next);
    var (_, progress, frames) = Go(world, ZoneReset.MakePlan([Z(0, 0)], [], []), new Budget(1, 1_000_000, () => 0));
    C(progress.Destroyed == 5 && frames == 5 && world.DestroysByFrame.Count == 5 && world.DestroysByFrame.Values.All(n => n == 1), $"a frame of one object destroys one object, in every frame of the zone ({frames} frames)");

    // A carried zone is reset whoever is near it now, and whatever was left alone beside it at its turn.
    var z0 = Z(0, 0);
    var z1 = Z(1, 0);
    world = new FakeWorld();
    next = 1;
    world.Fill([z0, z1], 2, ref next);
    world.Generated.UnionWith([z0, z1]);
    var memory = new ZoneReset.Memory();
    memory.Pending.Add(z0);
    memory.Cleared.Add(z0);
    world.Destroyed.UnionWith(world.Objects[z0].Select(o => o.Id));
    world.Live.Add(new ZoneReset.Protector(z0, 1));
    var plan = ZoneReset.MakePlan(world.Generated, [], [z0], null, memory.Pending);
    (_, progress, _) = Go(world, plan, Roomy(), memory);
    C(progress.Skipped == 1 && progress.Reset == 1 && world.Calls.Contains("unplace 0,0") && !world.Calls.Contains("unplace 1,0") && memory.Pending.Count == 0,
      "a player is near an emptied zone now: it is reset all the same (it is bare), the zone beside it that nobody emptied is left alone");

    world = new FakeWorld();
    next = 1;
    world.Fill([z0, z1], 2, ref next);
    world.Generated.UnionWith([z0, z1]);
    memory = new ZoneReset.Memory();
    memory.Pending.Add(z0);
    memory.Cleared.Add(z0);
    world.Destroyed.UnionWith(world.Objects[z0].Select(o => o.Id));
    world.OnList = zone =>
    {
      if (zone.Equals(z1) && world.Objects[zone].Count == 2)
        world.Add(zone, O(88, Kind.Piece));
    };
    plan = ZoneReset.MakePlan(world.Generated, [], [z1], [LocIn(0, 0, 20, 0, 20)], memory.Pending);
    C(Show(plan.Reset) == "1,0 0,0", "(the zone that holds a piece comes first, the emptied one, which shares a location with it, after it)");
    (_, progress, _) = Go(world, plan, Roomy(), memory);
    C(progress.Skipped == 1 && progress.SkippedForLocations == 0 && progress.Reset == 1 && world.Calls.Contains("unplace 0,0") && memory.Pending.Count == 0 && world.Calls.Contains("list 0,0"),
      "a piece placed in the zone that shares a location with an emptied one: the emptied zone is not left alone with it, it has its turn and is reset (it is bare)");

    // A zone left alone takes with it the zones of its location that are in the plan, and no others.
    world = new FakeWorld();
    world.Add(z0, O(1), O(2));
    world.OnList = zone =>
    {
      if (zone.Equals(z0) && world.Objects[zone].Count == 2)
        world.Add(zone, O(99, Kind.Piece));
    };
    plan = ZoneReset.MakePlan([z0], [], [z0], [LocIn(0, 0, 20, 0, 20)]);
    (_, progress, _) = Go(world, plan, Roomy());
    C(progress.Skipped == 1 && progress.SkippedForLocations == 0 && progress.Done == 1, "the zone a location reaches into is not generated: it is not counted among the zones left alone");

    // Two locations, two groups: a zone left alone takes the zones of its own group.
    var g1 = new[] { Z(0, 0), Z(1, 0) };
    var g2 = new[] { Z(10, 0), Z(11, 0) };
    world = new FakeWorld();
    next = 1;
    world.Fill(g1.Concat(g2), 2, ref next);
    world.OnList = zone =>
    {
      if (zone.Equals(Z(10, 0)) && world.Objects[zone].Count == 2)
        world.Add(zone, O(99, Kind.Piece));
    };
    plan = ZoneReset.MakePlan(g1.Concat(g2), [], [Z(10, 0)], [LocIn(0, 0, 20, 0, 20), LocIn(10, 0, 20, 0, 20)]);
    (_, progress, _) = Go(world, plan, Roomy());
    C(progress.Skipped == 2 && progress.SkippedForLocations == 1 && progress.Reset == 2 && !world.Calls.Contains("list 11,0") && world.Calls.Contains("list 0,0") && world.Calls.Contains("list 1,0"),
      "a zone left alone at its turn takes the zones of its own location with it, and no others: the other location's zones are reset");

    // A pending zone beside a zone cleared later is not mended in a save: its ground is gone with its objects.
    var a = Z(0, 0);
    var b = Z(1, 0);
    var c = Z(0, 1);
    world = new FakeWorld();
    next = 1;
    world.Fill([a], 100, ref next);
    world.Fill([b, c], 3, ref next);
    world.Generated.UnionWith([a, b, c]);
    memory = new ZoneReset.Memory();
    plan = ZoneReset.MakePlan(world.Generated, [], [a]);
    C(Show(plan.Reset) == "0,0 0,1 1,0", "(the big zone first, then the one to its north, then the one to its east)");
    progress = new Progress();
    var work = new ZoneReset.Work(world, plan, new Budget(4, 1_000_000, () => 0), progress, memory);
    var steps = work.Steps();
    steps.MoveNext();
    work.BeforeSave();
    C(memory.Pending.SetEquals([a]) && world.BorderCalls.Count == 2 && memory.Cleared.Count == 0, "(the first save finishes the big zone's objects, mends the ground of the two zones beside it, and completes the pass)");
    steps.MoveNext();
    C(memory.Pending.SetEquals([a, c]) && !world.Calls.Contains("list 1,0") && memory.Cleared.SetEquals([c]), "(the zone to the north is emptied next; the one to the east has not had its turn)");
    work.BeforeSave();
    var second = world.BorderCalls.Skip(2).ToList();
    C(second.Count == 1 && second[0].Zone.Equals(b), "the second save mends the zone to the east, which is beside the zone just emptied, and not the big zone, which is emptied too: " + string.Join(", ", second.Select(x => $"{x.Zone.x},{x.Zone.y}")));

    // The literal numbers of the work: what a border costs and how many objects go between two looks at the clock.
    C(ZoneReset.BorderCost == 25 && ZoneReset.Work.SliceSize == 32, "mending a border costs 25 objects of a frame's budget, and the clock is looked at every 32 objects");

    // A zone beside another, in each of the eight directions: it is finished when the other's objects are gone, and not before.
    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) })
    {
      var p = Z(0, 0);
      var q = Z(dx, dy);
      var r = Z(20, 0);
      world = new FakeWorld();
      world.Add(p, O(1)).Add(q, O(2)).Add(r, O(3));
      plan = ZoneReset.MakePlan([p, q, r], [], [p]);
      Go(world, plan, Roomy());
      int unplaceP = world.Calls.IndexOf("unplace 0,0");
      int unplaceQ = world.Calls.IndexOf($"unplace {dx},{dy}");
      int destroyQ = world.Calls.IndexOf("destroy 2");
      int listR = world.Calls.IndexOf("list 20,0");
      C(destroyQ >= 0 && unplaceP > destroyQ && unplaceP < listR && unplaceQ > destroyQ && unplaceQ < listR,
        $"a zone with a neighbour at {dx},{dy}: it waits for the neighbour's objects and is finished as soon as they are gone, the neighbour too (calls: {string.Join(", ", world.Calls)})");
    }

    // Two zones apart are not beside each other: the first is finished without waiting for the second.
    foreach (var (dx, dy) in new[] { (2, 0), (0, 2), (0, -2), (-2, 0), (2, 2), (2, -1), (1, -2) })
    {
      world = new FakeWorld();
      world.Add(Z(0, 0), O(1)).Add(Z(dx, dy), O(2));
      plan = ZoneReset.MakePlan([Z(0, 0), Z(dx, dy)], [], [Z(0, 0)]);
      Go(world, plan, Roomy());
      C(world.Calls.IndexOf("unplace 0,0") is > 0 && world.Calls.IndexOf("unplace 0,0") < world.Calls.IndexOf($"list {dx},{dy}"), $"a zone with another {dx},{dy} away is finished before the other has its turn");
    }

    // A border of one vertex is a border mended.
    world = new FakeWorld();
    world.BorderVertices[Z(1, 0)] = 1;
    plan = ZoneReset.MakePlan(Square(2), [Z(0, 0)], [Z(0, 0)]);
    (_, progress, _) = Go(world, plan, Roomy());
    C(progress.BorderZones == 1 && progress.BorderVertices == 1, "a zone whose ground lost one vertex is counted among those adjusted");

    // Execute with a memory of its own.
    world = new FakeWorld();
    next = 1;
    world.Fill([z0, z1], 2, ref next);
    world.Generated.UnionWith([z0, z1]);
    memory = new ZoneReset.Memory();
    memory.Pending.Add(z0);
    world.Destroyed.UnionWith(world.Objects[z0].Select(o => o.Id));
    plan = ZoneReset.MakePlan(world.Generated, [], [z0], null, memory.Pending);
    var run = ZoneReset.Execute(world, plan, Roomy(), new Progress(), memory);
    while (run.MoveNext())
    {
    }
    C(memory.Pending.Count == 0 && memory.Cleared.Count == 0 && world.Generated.Count == 0, "Execute keeps what the runs leave in the memory it is given: the emptied zone is finished, the pass completes");
  }

  // ------------------------------------------------------------------------------------------------ whole runs, on the game's classes

  private static void RunGapTests()
  {
    Section("whole runs: locations, zones emptied before, a stale piece, a portal, a peer's own view, a connection that reports (0, 0, 0)");
    using var seams = new Seams();
    ZoneRegen.FrameClock = () => 0.0;
    C(ZoneRegen.ObjectsPerFrame == 1500 && ZoneRegen.ObjectsPerFrameWithPlayers == 100 && ZoneRegen.ObjectsPerSecondWithPlayers == 6000 && ZoneRegen.MillisecondsPerFrame == 6.0,
      "the limits are as documented: 1500 objects a frame alone, 100 and 6000 a second with players, 6 ms");

    // A location that reaches into a zone beside a base keeps its own zone whole, through the run on the game's classes.
    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      foreach (var zone in Square(5))
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      w.PlaceLocation(Z(-1, 0), true, 20f, dx: 20f);
      w.PlaceLocation(Z(-4, 0), false, 20f, dx: 20f);
      Creator(w.Add(Z(1, 0), "piece_chest"), 42L);
      var (lines, _, job) = Regenerate(w);
      var expected = Square(5).Where(z => Math.Max(Math.Abs(z.x - 1), Math.Abs(z.y)) <= 1 || z.Equals(Z(-1, 0))).ToHashSet();
      C(w.Generated.SetEquals(expected) && lines[0].Contains("1 of them to keep a location whole"), $"the zone whose location reaches the zone beside the base stays with it ({w.Generated.Count} zones left): " + lines[0]);
    }
    finally
    {
      World.Close();
    }

    // A zone an earlier run emptied is reset though a base is beside it now, and the others of the base stay.
    ZDOExtraData.Reset();
    w = new World();
    try
    {
      foreach (var zone in Square(3))
        w.Generated.Add(zone);
      foreach (var zone in Square(3).Where(z => !z.Equals(Z(1, 0))))
        w.Add(zone, "Pine");
      Creator(w.Add(Z(0, 0), "piece_chest"), 42L);
      ZoneRegen.MemoryFor(w.Zones).Pending.Add(Z(1, 0));
      var (_, _, job) = Regenerate(w);
      C(w.Generated.Count == 8 && !w.Generated.Contains(Z(1, 0)) && ZoneRegen.MemoryFor(w.Zones).Pending.Count == 0, $"the emptied zone beside the base is finished; the base's other eight zones stay ({w.Generated.Count} left)");
    }
    finally
    {
      World.Close();
    }

    // A piece noted by an earlier run does not keep anything in this one.
    ZDOExtraData.Reset();
    w = new World();
    try
    {
      foreach (var zone in Square(2))
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      GetStatic<HashSet<Vector2s>>(typeof(ZoneRegen), "placedDuringRun").Add(Z(0, 0));
      var (_, _, job) = Regenerate(w);
      C(w.Generated.Count == 0 && job.Progress.Skipped == 0, "a run starts with no piece noted: what an earlier run noted keeps nothing");
    }
    finally
    {
      GetStatic<HashSet<Vector2s>>(typeof(ZoneRegen), "placedDuringRun").Clear();
      World.Close();
    }

    // A portal is a piece: the zones around it stay, those the run would have done first included.
    ZDOExtraData.Reset();
    w = new World();
    try
    {
      foreach (var zone in Square(5))
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      w.AddPortal(Z(3, 0), 42L);
      Regenerate(w);
      C(w.Generated.SetEquals(Square(1, 3, 0).Where(z => Math.Abs(z.x) <= 5 && Math.Abs(z.y) <= 5)), $"a portal keeps the 3 x 3 zones around it, those nearer the middle of the world too ({w.Generated.Count} left)");
    }
    finally
    {
      World.Close();
    }

    // A portal placed after the scan is found at its zone's turn.
    ZDOExtraData.Reset();
    w = new World();
    try
    {
      foreach (var zone in Square(2))
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      var (_, _, job) = Regenerate(w, () => w.AddPortal(Z(0, 0), 42L));
      C(job.Progress.Skipped == 9 && w.Generated.SetEquals(Square(1)), "a portal placed after the scan: its zone is left alone at its turn, with the 8 around it");
    }
    finally
    {
      World.Close();
    }

    // Far out, where every zone shares one sector list and one portal list: a portal belongs to its own zone only.
    ZDOExtraData.Reset();
    w = new World();
    try
    {
      var portalZone = Z(312, 0);
      var otherZone = Z(-400, 5);
      foreach (var zone in new[] { portalZone, otherZone })
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      w.AddPortal(portalZone, 42L);
      Regenerate(w);
      C(w.Generated.SetEquals([portalZone]), $"a portal far out keeps its own zone and not another that shares its list ({Show(w.Generated)} left)");
    }
    finally
    {
      World.Close();
    }

    // A player's own view: the player a peer owns has that peer's, not the widest.
    ZDOExtraData.Reset();
    w = new World();
    try
    {
      foreach (var zone in Square(6))
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      var near = w.Add(Z(-5, -5), "Player", user: 700L);
      near.SetOwner(700L);
      w.Peer(near, 700L).m_simulationDistance = new SimulationDistance(1, 2);
      var wide = w.Add(Z(5, 5), "Player", user: 701L);
      wide.SetOwner(701L);
      w.Peer(wide, 701L).m_simulationDistance = new SimulationDistance(3, 2);
      var live = new ZoneRegen.GameWorld(new ZoneRegen.Job(_ => { })).LiveProtectors().Select(p => (p.Zone, p.Radius)).Distinct().OrderBy(p => p.Zone.x).ToList();
      C(live.SequenceEqual([(Z(-5, -5), 1), (Z(5, 5), 3)]), "each player has their own peer's view: " + string.Join(" ", live.Select(p => $"{p.Zone.x},{p.Zone.y}/{p.Radius}")));
      Regenerate(w);
      var expectedKept = Square(6).Where(z => Math.Max(Math.Abs(z.x + 5), Math.Abs(z.y + 5)) <= 1 || Math.Max(Math.Abs(z.x - 5), Math.Abs(z.y - 5)) <= 3).ToHashSet();
      C(w.Generated.SetEquals(expectedKept), $"... and the scan keeps a 3 x 3 for the one and a 7 x 7 for the other ({w.Generated.Count} left, {expectedKept.Count} expected)");
    }
    finally
    {
      World.Close();
    }

    // A connection that is in with a character reports (0, 0, 0): that is the world's centre, a place they are at.
    ZDOExtraData.Reset();
    w = new World();
    try
    {
      foreach (var zone in Square(3))
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      var centre = w.Add(Z(2, 2), "Player", user: 700L);
      var peer = w.Peer(centre, 700L);
      peer.m_refPos = Vector3.zero;
      var asked = new ZoneRegen.GameWorld(new ZoneRegen.Job(_ => { })).LiveProtectors().Select(p => p.Zone).Distinct().ToList();
      C(asked.Contains(Z(0, 0)) && asked.Contains(Z(2, 2)), "a connected player whose client says (0, 0, 0) is at the world's centre as well as where their character is: " + Show(asked));
      var nobody = Make<ZNetPeer>();
      nobody.m_uid = 801L;
      GetField<List<ZNetPeer>>(w.Net, "m_peers").Add(nobody);
      var again = new ZoneRegen.GameWorld(new ZoneRegen.Job(_ => { })).LiveProtectors().Select(p => p.Zone).Distinct().Count();
      C(again == asked.Count, "(one with no character who reports (0, 0, 0) is nowhere)");
    }
    finally
    {
      World.Close();
    }

    // An object the game has released (its prefab is -1) protects nothing and is not found.
    ZDOExtraData.Reset();
    w = new World();
    try
    {
      foreach (var zone in Square(2))
      {
        w.Add(zone, "Pine");
        w.Generated.Add(zone);
      }
      var stale = Creator(w.Add(Z(0, 0), "piece_chest"), 42L);
      FPrefab.SetValue(stale, -1);
      var game = new ZoneRegen.GameWorld(new ZoneRegen.Job(_ => { }));
      C(game.Find(stale.m_uid) == null && game.ObjectsIn(Z(0, 0)).All(o => o.Id != stale.m_uid), "a released object is not found and not listed");
      var (_, _, job) = Regenerate(w);
      C(w.Generated.Count == 0 && job.Progress.Skipped == 0, "a released object that still holds the data of a piece keeps no zone");
    }
    finally
    {
      World.Close();
    }

    // The fraction of a frame's objects carried over is forgotten when nobody is connected.
    ZDOExtraData.Reset();
    w = new World();
    try
    {
      ZoneRegen.DeltaTime = () => 1f / 144f;
      SetStatic(typeof(ZoneRegen), "frameFraction", 0.0);
      var remote = w.Add(Z(0, 0), "Player", user: 700L);
      w.Peer(remote, 700L);
      int first = ZoneRegen.ObjectsPerFrameNow();
      var peers = GetField<List<ZNetPeer>>(w.Net, "m_peers");
      var kept = peers.ToList();
      peers.Clear();
      C(ZoneRegen.ObjectsPerFrameNow() == ZoneRegen.ObjectsPerFrame, "(the player leaves: the frames are full size)");
      peers.AddRange(kept);
      C(first == 42 && ZoneRegen.ObjectsPerFrameNow() == 42, $"a player who comes back starts from nothing carried over ({first})");
    }
    finally
    {
      World.Close();
    }
  }

  private static void ErrorCountTests()
  {
    Section("errors: an object that cannot be destroyed, ground that cannot be mended: counted, and the first three logged");
    using var seams = new Seams();
    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      var job = new ZoneRegen.Job(_ => { });
      var world = new ZoneRegen.GameWorld(job);
      var zone = Z(6, 6);
      for (int i = 0; i < 5; i++)
        w.Add(zone, "Pine", dx: i);
      var listed = world.ObjectsIn(zone);
      ZoneRegen.FindView = _ => throw new InvalidOperationException("no scene");
      bool any = false;
      var log = LogHandler.During(() =>
      {
        foreach (var o in listed)
          any |= world.Destroy(o, out _);
      });
      C(!any && job.Errors == 5 && log.Count(l => l.StartsWith("[Error]") && l.Contains("an object could not be destroyed") && l.Contains("no scene")) == 3,
        $"five objects that cannot be destroyed: {job.Errors} errors counted, the first three logged ({log.Count} lines)");
      C(w.DestroyQueue.Count == 0, "and none is queued");

      // Ground that cannot be mended: the zone's sector list is not there.
      var compiler = w.Add(zone, "_TerrainCompiler");
      ZDOExtraData.Set(compiler.m_uid, ZDOVars.s_TCData, TcData((r, c) => true));
      var sectors = GetField<List<ZDO>[]>(w.Man, "m_objectsBySector");
      SetField(w.Man, "m_objectsBySector", null);
      job.Errors = 0;
      log = LogHandler.During(() =>
      {
        for (int i = 0; i < 4; i++)
          world.AdjustBorder(zone, Edges.North);
      });
      SetField(w.Man, "m_objectsBySector", sectors);
      C(job.Errors == 4 && log.Count(l => l.StartsWith("[Error]") && l.Contains("the ground at the edge of zone")) == 3, $"four zones whose ground cannot be mended: {job.Errors} errors counted, the first three logged ({log.Count} lines)");
    }
    finally
    {
      World.Close();
    }
  }

  private static void MemoryTests()
  {
    Section("what the runs leave for the next: for that world only");
    using var seams = new Seams();
    ZDOExtraData.Reset();
    var a = new World();
    var memoryA = ZoneRegen.MemoryFor(a.Zones);
    memoryA.Pending.Add(Z(3, 3));
    C(ReferenceEquals(ZoneRegen.MemoryFor(a.Zones), memoryA) && memoryA.Pending.Contains(Z(3, 3)), "the same world gets the same memory");
    var b = new World();
    var memoryB = ZoneRegen.MemoryFor(b.Zones);
    C(!ReferenceEquals(memoryA, memoryB) && memoryB.Pending.Count == 0, "another world starts with nothing");
    C(ZoneRegen.MemoryFor(a.Zones).Pending.Count == 0, "the world that was left does not get its memory back");

    // A save of another world is not edited: only the world the memory is for.
    memoryB = ZoneRegen.MemoryFor(b.Zones);
    memoryB.Pending.Add(Z(3, 3));
    a.Generated.Add(Z(3, 3));
    a.Generated.Add(Z(4, 4));
    b.Generated.Add(Z(3, 3));
    var harmony = new Harmony("zone-tests.memory");
    try
    {
      PatchPrepareSave(harmony);
      a.Zones.PrepareSave();
      b.Zones.PrepareSave();
      C(GetField<HashSet<Vector2s>>(a.Zones, "m_tempGeneratedZonesSaveClone").SetEquals([Z(3, 3), Z(4, 4)]), "the save of a world that is not the one the memory is for keeps its zones");
      C(GetField<HashSet<Vector2s>>(b.Zones, "m_tempGeneratedZonesSaveClone").Count == 0, "the save of the world it is for leaves out the zone that was emptied and not finished");
    }
    finally
    {
      harmony.UnpatchAll("zone-tests.memory");
      World.Close();
    }
  }

  private static void SaveErrorTests()
  {
    Section("a save in the middle of a run: an error is logged and the save goes on");
    using var seams = new Seams();
    ZDOExtraData.Reset();
    var w = new World();
    try
    {
      // A zone half emptied by a work whose world cannot destroy any more.
      var failing = new FakeWorld();
      uint next = 1;
      failing.Fill([Z(0, 0)], 100, ref next);
      var work = new ZoneReset.Work(failing, ZoneReset.MakePlan([Z(0, 0)], [], []), new Budget(10, 1_000_000, () => 0), new Progress(), new ZoneReset.Memory());
      var steps = work.Steps();
      steps.MoveNext();
      failing.OnDestroy = _ => throw new InvalidOperationException("cannot destroy");
      SetStatic(typeof(ZoneRegen), "job", new ZoneRegen.Job(_ => { }) { Work = work });
      Exception? thrown = null;
      var log = LogHandler.During(() =>
      {
        try
        {
          ZoneRegen.BeforeSave();
        }
        catch (Exception e)
        {
          thrown = e;
        }
      });
      C(thrown == null && log.Any(l => l.StartsWith("[Error]") && l.Contains("the world could not be made ready to save") && l.Contains("cannot destroy")), "ZNet.SaveWorld's first step that fails is logged and the save goes on: " + (thrown?.Message ?? string.Join(" | ", log)));
      SetStatic(typeof(ZoneRegen), "job", null);

      // The zones that are emptied and not finished cannot be left out of the copy.
      ZoneRegen.MemoryFor(w.Zones).Pending.Add(Z(1, 1));
      thrown = null;
      log = LogHandler.During(() =>
      {
        try
        {
          ZoneRegen.HidePendingFromSave(w.Zones);
        }
        catch (Exception e)
        {
          thrown = e;
        }
      });
      C(thrown == null && log.Any(l => l.StartsWith("[Error]") && l.Contains("could not be left out of the save")), "ZoneSystem.PrepareSave's postfix that fails is logged and the save goes on: " + (thrown?.Message ?? string.Join(" | ", log)));

      // A save that works says nothing, and has the zone emptied and not finished as the game would write a zone that is not generated.
      w.Generated.Add(Z(1, 1));
      w.PlaceLocation(Z(1, 1), true, 0f);
      w.PlaceLocation(Z(2, 2), true, 0f);
      var harmony = new Harmony("zone-tests.saveerrors");
      try
      {
        PatchPrepareSave(harmony);
        log = LogHandler.During(() => w.Zones.PrepareSave());
        var locations = GetField<List<ZoneSystem.LocationInstance>>(w.Zones, "m_tempLocationsSaveClone");
        C(!log.Any(l => l.StartsWith("[Error]")) && locations.Count == 2 && locations.Count(l => l.m_placed) == 1 && !GetField<HashSet<Vector2s>>(w.Zones, "m_tempGeneratedZonesSaveClone").Contains(Z(1, 1)),
          "a save that works logs no error and leaves out exactly the zone that is waiting: " + string.Join(" | ", log));
      }
      finally
      {
        harmony.UnpatchAll("zone-tests.saveerrors");
      }
    }
    finally
    {
      World.Close();
    }
  }
}
