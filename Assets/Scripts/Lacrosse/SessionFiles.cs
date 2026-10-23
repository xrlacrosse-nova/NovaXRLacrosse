using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Where player data lives on the device and how it's read and written
/// (Plans/Current/Player_Session_History_Plan.md, "Storage"). Local only, under
/// Application.persistentDataPath (on ML2, the app's private storage):
///
///   NovaXR/
///       profiles.json                      all players + the one selected last
///       players/&lt;playerId&gt;/
///           index.json                     one summary per session
///           sessions/2026-10-21_143005_&lt;sessionId&gt;.json
///
/// Writes go to a .tmp file first and then replace the real one, so a crash mid-write can't
/// corrupt it. A file that can't be read is skipped with a warning; a missing or broken
/// index.json is rebuilt from the session files.
/// </summary>
public static class SessionFiles
{
    private const string FileTimeFormat = "yyyy-MM-dd_HHmmss";

    public static string Root => Path.Combine(Application.persistentDataPath, "NovaXR");

    public static string ProfilesPath => Path.Combine(Root, "profiles.json");

    public static string PlayerFolder(string playerId) => Path.Combine(Root, "players", playerId);

    public static string IndexPath(string playerId) => Path.Combine(PlayerFolder(playerId), "index.json");

    public static string SessionsFolder(string playerId) => Path.Combine(PlayerFolder(playerId), "sessions");

    // ── Reading and writing ───────────────────────────────────────

    /// <summary>Writes <paramref name="contents"/> to a .tmp file beside <paramref name="path"/>,
    /// then swaps it in. Creates the folder if needed.</summary>
    public static void WriteAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);

        if (File.Exists(path))
            File.Replace(tmp, path, null);
        else
            File.Move(tmp, path);
    }

    /// <summary>Reads a JsonUtility file. Returns false, with a warning if the file exists but
    /// can't be read, so one broken file never stops the rest loading.</summary>
    public static bool TryRead<T>(string path, out T value) where T : class
    {
        value = null;
        if (!File.Exists(path)) return false;

        try
        {
            value = JsonUtility.FromJson<T>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SessionFiles] Couldn't read {path} ({e.Message}) — skipped.");
            value = null;
            return false;
        }

        if (value == null)
            Debug.LogWarning($"[SessionFiles] {path} is empty — skipped.");
        return value != null;
    }

    // ── Sessions ──────────────────────────────────────────────────

    /// <summary>Writes the session file and adds its summary to the player's index.json.
    /// Returns the session file's name.</summary>
    public static string SaveSession(SessionRecord session)
    {
        // Loaded before the session file is written: if the index has to be rebuilt, it must
        // not already contain this session, or the append below would list it twice.
        SessionIndex index = LoadIndex(session.playerId);

        DateTime started = DateTime.TryParse(session.startedUtc, CultureInfo.InvariantCulture,
                                             DateTimeStyles.RoundtripKind, out DateTime parsed)
            ? parsed.ToUniversalTime()
            : DateTime.UtcNow;
        string file = $"{started.ToString(FileTimeFormat, CultureInfo.InvariantCulture)}_{session.sessionId}.json";

        WriteAtomic(Path.Combine(SessionsFolder(session.playerId), file), JsonUtility.ToJson(session));

        index.sessions.Add(Summarize(session, file));
        WriteAtomic(IndexPath(session.playerId), JsonUtility.ToJson(index, true));
        return file;
    }

    /// <summary>The player's index.json, rebuilt from the session files if it's missing or
    /// broken. Empty for a player with no sessions yet.</summary>
    public static SessionIndex LoadIndex(string playerId)
    {
        if (TryRead(IndexPath(playerId), out SessionIndex index) && index.sessions != null)
            return index;

        return RebuildIndex(playerId);
    }

    /// <summary>Summarizes every readable session file, oldest first, and writes the result as
    /// the new index.json (only if there was anything to index).</summary>
    public static SessionIndex RebuildIndex(string playerId)
    {
        var index = new SessionIndex();
        string folder = SessionsFolder(playerId);
        if (!Directory.Exists(folder))
            return index;

        foreach (string path in Directory.GetFiles(folder, "*.json"))
        {
            if (TryRead(path, out SessionRecord session) && session.shots != null)
                index.sessions.Add(Summarize(session, Path.GetFileName(path)));
        }

        // ISO round-trip UTC strings sort in time order.
        index.sessions.Sort((a, b) => string.CompareOrdinal(a.startedUtc, b.startedUtc));

        if (index.sessions.Count > 0)
        {
            WriteAtomic(IndexPath(playerId), JsonUtility.ToJson(index, true));
            Debug.Log($"[SessionFiles] Rebuilt index.json for player {playerId} ({index.sessions.Count} sessions).");
        }
        return index;
    }

    public static SessionSummary Summarize(SessionRecord session, string file)
    {
        var summary = new SessionSummary
        {
            sessionId = session.sessionId,
            file = file,
            startedUtc = session.startedUtc,
            difficulty = session.settings != null ? session.settings.difficulty : "",
        };

        foreach (ShotRecord shot in session.shots)
        {
            int q = (int)shot.resultQuadrant;
            bool saved = shot.outcome == ShotRecord.Save;

            summary.shots++;
            summary.shotsPerQuadrant[q]++;
            if (saved)
            {
                summary.saves++;
                summary.savesPerQuadrant[q]++;
            }
            else
            {
                summary.goals++;
            }
        }
        return summary;
    }
}
