using System;
using System.Collections.Generic;
using System.Reflection;

namespace MuvluvUnlockCG.Core;

/// <summary>
/// Pre-install resolution of every declared Harmony target. A game update that moves a generated
/// seam is reported per site and aborts plugin loading before Harmony installs anything, so a
/// partially patched plugin cannot reach the runtime.
///
/// Pure logic: depends on <see cref="System.Reflection"/> only, never on HarmonyLib or the game
/// assemblies, so it compiles into the loader-free test project.
/// </summary>
public static class PatchPreflightPolicy
{
    public const string MissingTarget = "missing-target";
    public const string TargetError = "target-error";

    /// <summary>Raised when at least one declared target did not resolve.</summary>
    public sealed class PatchPreflightException : InvalidOperationException
    {
        public PatchPreflightException(IReadOnlyList<string> failures)
            : base(Format(failures))
        {
            Failures = failures;
        }

        public IReadOnlyList<string> Failures { get; }

        private static string Format(IReadOnlyList<string> failures) =>
            $"Harmony patch target precheck failed ({failures.Count} sites): "
            + string.Join(" | ", failures);
    }

    /// <summary>
    /// Reports every declared target that did not resolve. A null target and a factory exception are
    /// both failures. An empty inventory is also a failure, because a registration that lost its
    /// entries must not be mistaken for a clean check.
    /// </summary>
    public static IReadOnlyList<string> Check(
        IEnumerable<(string Label, Func<MethodBase?> Resolve)> targets)
    {
        var failures = new List<string>();
        var seen = 0;

        foreach (var (label, resolve) in targets)
        {
            seen++;
            MethodBase? target;
            try
            {
                target = resolve();
            }
            catch (Exception exception)
            {
                failures.Add(label + " => " + TargetError + ":" + Describe(exception));
                continue;
            }

            if (target is null)
            {
                failures.Add(label + " => " + MissingTarget);
            }
        }

        if (seen == 0)
        {
            failures.Add("<inventory> => " + MissingTarget + ":empty-target-set");
        }

        return failures;
    }

    public static string Describe(Exception exception) =>
        exception is TargetInvocationException { InnerException: { } inner }
            ? inner.GetType().Name + ":" + inner.Message
            : exception.GetType().Name + ":" + exception.Message;
}
