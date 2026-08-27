using System;
using System.Diagnostics;
using NoFrills.MemoryProfiler.Core;
using NUnit.Framework;

namespace NoFrills.MemoryProfiler.Tests;

/// <summary>
/// Says which process holds the heap the collection tests dump.
///
/// Finding it by name was wrong twice over: `dotnet run` starts the target as a child of itself,
/// so the process to dump is not the one that was started, and searching the machine for a process
/// whose name contains Sleeper finds whichever one is running, including the one another test
/// started when two test assemblies run at once. The target announces its own identifier, so each
/// test dumps the process it asked for.
/// </summary>
public static class TestTarget
{
    private const string Ready = "ready ";

    /// <summary>Reads the line the target prints when it is up, and returns the process it names.</summary>
    public static LiveProcess Announced(Process started)
    {
        var line = started.StandardOutput.ReadLine();

        Assert.That(line, Does.StartWith(Ready), "the target process did not start");

        var identifier = int.Parse(line![Ready.Length..]);

        return new LiveProcess(identifier, Process.GetProcessById(identifier).ProcessName);
    }
}
