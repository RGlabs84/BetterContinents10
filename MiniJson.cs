// Added by Wubarrk on 2026-10-26 for the editor pack (0.10.7).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BetterContinents;

// A small JSON reader for manifest.json and editor.json (the game ships no serializer the plugin can rely on; WorldExportJson
// writes). Objects keep their key order, so a manifest can be read, changed and written again. Values are string, double, bool,
// null, MiniJson.Obj and List<object?>. Anything it cannot read is a FormatException with the position.
internal static class MiniJson
{
  internal sealed class Obj : List<KeyValuePair<string, object?>>
  {
    public bool TryGet(string key, out object? value)
    {
      foreach (var kv in this)
        if (kv.Key == key)
        {
          value = kv.Value;
          return true;
        }
      value = null;
      return false;
    }

    public object? this[string key] => TryGet(key, out var v) ? v : null;

    public Obj? Child(string key) => this[key] as Obj;

    public string? Str(string key) => this[key] as string;

    public double? Num(string key) => this[key] is double d ? d : null;

    public bool? Bool(string key) => this[key] is bool b ? b : null;

    public void Set(string key, object? value)
    {
      for (int i = 0; i < Count; i++)
        if (this[i].Key == key)
        {
          this[i] = new KeyValuePair<string, object?>(key, value);
          return;
        }
      Add(new KeyValuePair<string, object?>(key, value));
    }

    public void Remove(string key) => RemoveAll(kv => kv.Key == key);

    /// <summary>Puts a key right after another (or at the end when that one is not there); replaces the key if it is already there.</summary>
    public void SetAfter(string after, string key, object? value)
    {
      Remove(key);
      int at = FindIndex(kv => kv.Key == after);
      Insert(at < 0 ? Count : at + 1, new KeyValuePair<string, object?>(key, value));
    }
  }

  public static object? Parse(string text)
  {
    int at = 0;
    var value = Value(text, ref at, 0);
    Skip(text, ref at);
    if (at != text.Length)
      throw new FormatException($"unexpected text at {at}");
    return value;
  }

  /// <summary>The JSON object in a file's text, or null when it is not one.</summary>
  public static Obj? ParseObject(string text)
  {
    try
    {
      return Parse(text) as Obj;
    }
    catch (FormatException)
    {
      return null;
    }
  }

  /// <summary>An object tree in the shape WorldExportJson writes.</summary>
  public static object? ToWritable(object? value)
  {
    switch (value)
    {
      case Obj obj:
      {
        var o = new WorldExportJson.Obj();
        foreach (var kv in obj)
          o.Add(kv.Key, ToWritable(kv.Value));
        return o;
      }
      case List<object?> list:
        return list.ConvertAll(ToWritable);
      case double d when d == Math.Floor(d) && Math.Abs(d) < 9e15:
        return (long)d;
      default:
        return value;
    }
  }

  private static void Skip(string s, ref int at)
  {
    while (at < s.Length && (s[at] == ' ' || s[at] == '\n' || s[at] == '\r' || s[at] == '\t'))
      at++;
  }

  private static object? Value(string s, ref int at, int depth)
  {
    if (depth > 64)
      throw new FormatException("too deep");
    Skip(s, ref at);
    if (at >= s.Length)
      throw new FormatException("the text ends early");
    char c = s[at];
    if (c == '{')
    {
      at++;
      var o = new Obj();
      Skip(s, ref at);
      if (at < s.Length && s[at] == '}')
      {
        at++;
        return o;
      }
      while (true)
      {
        Skip(s, ref at);
        if (at >= s.Length || s[at] != '"')
          throw new FormatException($"a key was expected at {at}");
        var key = Str(s, ref at);
        Skip(s, ref at);
        if (at >= s.Length || s[at] != ':')
          throw new FormatException($"':' was expected at {at}");
        at++;
        o.Add(new KeyValuePair<string, object?>(key, Value(s, ref at, depth + 1)));
        Skip(s, ref at);
        if (at < s.Length && s[at] == ',')
        {
          at++;
          continue;
        }
        if (at < s.Length && s[at] == '}')
        {
          at++;
          return o;
        }
        throw new FormatException($"',' or '}}' was expected at {at}");
      }
    }
    if (c == '[')
    {
      at++;
      var list = new List<object?>();
      Skip(s, ref at);
      if (at < s.Length && s[at] == ']')
      {
        at++;
        return list;
      }
      while (true)
      {
        list.Add(Value(s, ref at, depth + 1));
        Skip(s, ref at);
        if (at < s.Length && s[at] == ',')
        {
          at++;
          continue;
        }
        if (at < s.Length && s[at] == ']')
        {
          at++;
          return list;
        }
        throw new FormatException($"',' or ']' was expected at {at}");
      }
    }
    if (c == '"')
      return Str(s, ref at);
    if (string.CompareOrdinal(s, at, "true", 0, 4) == 0) { at += 4; return true; }
    if (string.CompareOrdinal(s, at, "false", 0, 5) == 0) { at += 5; return false; }
    if (string.CompareOrdinal(s, at, "null", 0, 4) == 0) { at += 4; return null; }
    int start = at;
    while (at < s.Length && (char.IsDigit(s[at]) || s[at] == '-' || s[at] == '+' || s[at] == '.' || s[at] == 'e' || s[at] == 'E'))
      at++;
    if (at == start || !double.TryParse(s.Substring(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
      throw new FormatException($"a value was expected at {start}");
    return number;
  }

  private static string Str(string s, ref int at)
  {
    at++;
    var sb = new StringBuilder();
    while (at < s.Length)
    {
      char c = s[at++];
      if (c == '"')
        return sb.ToString();
      if (c != '\\')
      {
        sb.Append(c);
        continue;
      }
      if (at >= s.Length)
        break;
      char e = s[at++];
      switch (e)
      {
        case 'n': sb.Append('\n'); break;
        case 'r': sb.Append('\r'); break;
        case 't': sb.Append('\t'); break;
        case 'b': sb.Append('\b'); break;
        case 'f': sb.Append('\f'); break;
        case 'u':
          if (at + 4 > s.Length || !int.TryParse(s.Substring(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            throw new FormatException($"a bad \\u escape at {at}");
          sb.Append((char)code);
          at += 4;
          break;
        default: sb.Append(e); break;
      }
    }
    throw new FormatException("the text ends inside a string");
  }
}
