// PLACEHOLDER (slice C, 2026-10-07): slice B's BakedTransfer, so that this branch builds before B merges. DELETE THIS FILE when feat/0.10.4
// holds B's BakedTransfer.cs (two definitions of the type would not compile). The two members are the contract: ClientReports is set while
// the client manager runs in a world; ReportApplied(revision) tells the server a push's colliders stand.
namespace BetterContinents;

internal static class BakedTransfer
{
  public static bool ClientReports;

  public static void ReportApplied(int revision)
  {
  }
}
