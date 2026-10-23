using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace MarketHelper.Listing;

public enum StepResult
{
    Done,
    Wait,
    Fail,
}

// Runs a queue of small steps on the game's main thread, one per frame at most,
// with a random pause between steps so input arrives at a human pace.
public sealed class StepRunner : IDisposable
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(8);

    private readonly LinkedList<(string Name, Func<StepResult> Run)> steps = new();
    private readonly Random random = new();
    private readonly Func<(int Min, int Max)> delayRange;

    private DateTime resumeAt = DateTime.MinValue;
    private DateTime stepStarted = DateTime.MinValue;
    private bool stepActive;

    public bool IsRunning => steps.Count > 0;
    public string? CurrentStep => steps.First?.Value.Name;

    // Called with the failed step's name; the queue is cleared after it
    public event Action<string>? Failed;

    public StepRunner(Func<(int Min, int Max)> delayRange)
    {
        this.delayRange = delayRange;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    public void Add(string name, Func<StepResult> run) => steps.AddLast((name, run));

    public void Add(string name, Action run) => Add(name, () =>
    {
        run();
        return StepResult.Done;
    });

    // Removes queued steps after the current one until one matches
    public void SkipUntilNext(Func<string, bool> isNext)
    {
        var node = steps.First?.Next;
        while (node != null && !isNext(node.Value.Name))
        {
            var next = node.Next;
            steps.Remove(node);
            node = next;
        }
    }

    public void Pause(int minMs, int maxMs) => resumeAt = DateTime.UtcNow.AddMilliseconds(random.Next(minMs, maxMs + 1));

    public void Stop()
    {
        steps.Clear();
        stepActive = false;
    }

    private void OnUpdate(IFramework framework)
    {
        if (steps.First is not { } node || DateTime.UtcNow < resumeAt)
            return;

        if (!stepActive)
        {
            stepActive = true;
            stepStarted = DateTime.UtcNow;
        }

        StepResult result;
        try
        {
            result = node.Value.Run();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Listing step \"{node.Value.Name}\" threw");
            result = StepResult.Fail;
        }

        if (result == StepResult.Wait && DateTime.UtcNow - stepStarted > StepTimeout)
        {
            Plugin.Log.Warning($"Listing step \"{node.Value.Name}\" timed out");
            result = StepResult.Fail;
        }

        switch (result)
        {
            case StepResult.Done:
                // The node may already be gone if the step called Stop()
                if (node.List != null)
                    steps.Remove(node);
                stepActive = false;
                var (min, max) = delayRange();
                Pause(min, max);
                break;
            case StepResult.Fail:
                var name = node.Value.Name;
                Stop();
                Failed?.Invoke(name);
                break;
        }
    }
}
