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

    /// <summary>The player's index.json, rebuilt from the session files if it's missing, broken,
    /// or lists a different number of sessions than there are files (e.g. a session file was
    /// written but the index write after it failed). Empty for a player with no sessions yet.</summary>
    public static SessionIndex LoadIndex(string playerId)
    {
        if (TryRead(IndexPath(playerId), out SessionIndex index) && index.sessions != null)
        {
            int files = CountSessionFiles(playerId);
            if (files == index.sessions.Count)
                return index;

            Debug.LogWarning($"[SessionFiles] index.json for player {playerId} lists {index.sessions.Count} " +
                             $"sessions but there are {files} session files — rebuilding it.");
        }

        return RebuildIndex(playerId);
    }

    /// <summary>Summarizes every readable session file, oldest first, and writes the result as
    /// the new index.json (if there's anything to index, or an old index.json to replace).
    /// A session file that can't be read is renamed to .json.corrupt, so it stops counting as a
    /// session and the next load doesn't rebuild again. Nothing is deleted.</summary>
    public static SessionIndex RebuildIndex(string playerId)
    {
        var index = new SessionIndex();
        string folder = SessionsFolder(playerId);
        if (Directory.Exists(folder))
        {
            foreach (string path in Directory.GetFiles(folder, "*.json"))
            {
                if (TryRead(path, out SessionRecord session) && session.shots != null)
                    index.sessions.Add(Summarize(session, Path.GetFileName(path)));
                else
                    SetAside(path);
            }
        }

        // ISO round-trip UTC strings sort in time order.
        index.sessions.Sort((a, b) => string.CompareOrdinal(a.startedUtc, b.startedUtc));

        // An existing index.json is replaced even with an empty list, or one that lists sessions
        // whose files are gone would be rebuilt on every load.
        if (index.sessions.Count > 0 || File.Exists(IndexPath(playerId)))
        {
            WriteAtomic(IndexPath(playerId), JsonUtility.ToJson(index, true));
            Debug.Log($"[SessionFiles] Rebuilt index.json for player {playerId} ({index.sessions.Count} sessions).");
        }
        return index;
    }

    /// <summary>Session files on disk for the player. .tmp and .corrupt files don't match.</summary>
    private static int CountSessionFiles(string playerId)
    {
        string folder = SessionsFolder(playerId);
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json").Length : 0;
    }

    /// <summary>Renames an unreadable session file to .json.corrupt (same idea as
    /// profiles.json.corrupt). If the rename fails it's left alone: the worst case is that the
    /// index is rebuilt on every load, with no data lost.</summary>
    private static void SetAside(string path)
    {
        string corrupt = path + ".corrupt";
        try
        {
            File.Move(path, corrupt);
            Debug.LogWarning($"[SessionFiles] Session file {Path.GetFileName(path)} can't be read — " +
                             $"renamed to {Path.GetFileName(corrupt)} and left out of history.");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SessionFiles] Session file {Path.GetFileName(path)} can't be read, and " +
                             $"setting it aside failed ({e.Message}) — left as is.");
        }
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
