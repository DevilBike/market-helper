using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MarketHelper.Organizing;

// A plain-text record of the last few Sort runs: every visit, move and round,
// with times, so slow or wasted steps can be spotted and the next version tuned
public sealed class SortLog
{
    private const int RunsKept = 50;
    private const string RunMarker = "=== ";

    private readonly string path;
    private readonly List<string> current = new();
    private DateTime started;

    public SortLog(string directory) => path = Path.Combine(directory, "sort-log.txt");

    public string FilePath => path;
    public bool IsOpen => current.Count > 0;

    public void Begin(string title)
    {
        if (IsOpen)
            End("Interrupted");
        started = DateTime.Now;
        current.Add($"{RunMarker}{title} {started:yyyy-MM-dd HH:mm:ss}");
    }

    // Lines outside a run (like the game's item sort) are written on their own
    public void Note(string line)
    {
        if (IsOpen)
        {
            Add(line);
            return;
        }

        Begin(line);
        End(null);
    }

    public void Add(string line)
    {
        if (IsOpen)
            current.Add($"+{(DateTime.Now - started).TotalSeconds,6:0.0}s {line}");
    }

    public void End(string? summary)
    {
        if (!IsOpen)
            return;
        if (summary != null)
            Add(summary);

        try
        {
            var runs = SplitRuns(File.Exists(path) ? File.ReadAllText(path) : string.Empty);
            runs.Add(string.Join(Environment.NewLine, current));
            File.WriteAllText(path, string.Join(Environment.NewLine + Environment.NewLine, runs.TakeLast(RunsKept)) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Couldn't save the sort log");
        }

        current.Clear();
    }

    public string Read()
    {
        try
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            if (IsOpen)
                text += string.Join(Environment.NewLine, current);
            return text;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Couldn't read the sort log");
            return string.Empty;
        }
    }

    // Saved runs, newest first, as their title line and full text
    public List<(string Title, string Text)> Runs()
    {
        try
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            return SplitRuns(text)
                .Select(run => (Title: run.Split('\n')[0].TrimEnd('\r').Replace(RunMarker, string.Empty), Text: run))
                .Reverse()
                .ToList();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Couldn't read the sort log");
            return new List<(string, string)>();
        }
    }

    private static List<string> SplitRuns(string text)
    {
        var runs = new List<string>();
        var run = new StringBuilder();
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith(RunMarker) && run.Length > 0)
            {
                runs.Add(run.ToString().TrimEnd());
                run.Clear();
            }

            if (line.Length > 0 || run.Length > 0)
                run.AppendLine(line);
        }

        if (run.Length > 0)
            runs.Add(run.ToString().TrimEnd());
        return runs;
    }
}
