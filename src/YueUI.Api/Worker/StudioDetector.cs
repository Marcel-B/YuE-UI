using System.Diagnostics;

namespace YueUI.Api.Worker;

public interface IStudioDetector
{
    bool IsRunning();
}

/// <summary>Looks for the YuE Studio app's process (its executable is <c>YuE Studio.app/Contents/MacOS/YuE Studio</c>).</summary>
public sealed class StudioDetector : IStudioDetector
{
    public bool IsRunning()
    {
        var processes = Process.GetProcessesByName("YuE Studio");
        foreach (var process in processes)
        {
            process.Dispose();
        }
        return processes.Length > 0;
    }
}
