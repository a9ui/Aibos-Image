using System.IO;
using System.Text.Json;

namespace PhotoViewer.Wpf;

internal static class SingleInstanceCoordinatorSmokeRunner
{
    internal static int Run(string resultPath)
    {
        object result;
        int exitCode;
        try
        {
            string identity = Guid.NewGuid().ToString("N");
            using var activated = new ManualResetEventSlim(false);
            int activationCount = 0;
            bool secondaryWasPrimary;
            bool signalAccepted;
            bool reacquired;

            using (var primary = SingleInstanceCoordinator.CreateForSmoke(identity))
            {
                primary.StartListening(() =>
                {
                    Interlocked.Increment(ref activationCount);
                    activated.Set();
                });
                using (var secondary = SingleInstanceCoordinator.CreateForSmoke(identity))
                {
                    secondaryWasPrimary = secondary.IsPrimary;
                    signalAccepted = secondary.SignalPrimary();
                }
                if (!activated.Wait(TimeSpan.FromSeconds(2)))
                    throw new TimeoutException(
                        "The primary instance did not receive activation.");
            }

            using (var replacement = SingleInstanceCoordinator.CreateForSmoke(identity))
                reacquired = replacement.IsPrimary;

            bool primaryExclusive = !secondaryWasPrimary;
            bool exactlyOneActivation = activationCount == 1;
            bool ok = primaryExclusive
                && signalAccepted
                && exactlyOneActivation
                && reacquired;
            result = new
            {
                ok,
                primaryExclusive,
                signalAccepted,
                exactlyOneActivation,
                reacquired,
            };
            exitCode = ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            result = new { ok = false, error = ex.GetType().Name };
            exitCode = 1;
        }

        try
        {
            string fullPath = Path.GetFullPath(resultPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(
                fullPath,
                JsonSerializer.Serialize(result),
                new System.Text.UTF8Encoding(false));
        }
        catch
        {
            return 1;
        }
        return exitCode;
    }
}
