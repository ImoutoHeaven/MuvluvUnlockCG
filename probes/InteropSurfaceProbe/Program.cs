using System;

namespace MuvluvUnlockCG.Probes;

internal static class Program
{
    private readonly record struct RouteCase(
        bool HasCharacter,
        int CurrentAffection,
        int RequiredAffection,
        InteropSurfaceProbe.PlaybackRoute Expected);

    public static int Main()
    {
        var cases = new[]
        {
            new RouteCase(false, 0, 0, InteropSurfaceProbe.PlaybackRoute.LocalBypass),
            new RouteCase(false, 100, 10, InteropSurfaceProbe.PlaybackRoute.LocalBypass),
            new RouteCase(true, 9, 10, InteropSurfaceProbe.PlaybackRoute.LocalBypass),
            new RouteCase(true, 10, 10, InteropSurfaceProbe.PlaybackRoute.Normal),
            new RouteCase(true, 11, 10, InteropSurfaceProbe.PlaybackRoute.Normal),
        };

        foreach (var testCase in cases)
        {
            var actual = InteropSurfaceProbe.SelectPlaybackRoute(
                testCase.HasCharacter,
                testCase.CurrentAffection,
                testCase.RequiredAffection);
            if (actual != testCase.Expected)
            {
                Console.Error.WriteLine(
                    $"route mismatch: owned={testCase.HasCharacter}, " +
                    $"current={testCase.CurrentAffection}, required={testCase.RequiredAffection}, " +
                    $"expected={testCase.Expected}, actual={actual}");
                return 1;
            }
        }

        Console.WriteLine($"route-policy: {cases.Length}/{cases.Length} passed");
        return 0;
    }
}
