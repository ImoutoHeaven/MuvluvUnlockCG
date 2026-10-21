using System;
using System.Reflection;

namespace MuvluvUnlockCG.Core;

/// <summary>
/// Resolves compiler-generated lambda seams. The compiler renumbers the numeric
/// suffix whenever the enclosing method changes, while the declaring type, the
/// factory-family prefix, the parameter list, and the return type stay put. The
/// exact name is preferred; when it moved, the single member of the same factory
/// family with the identical signature is accepted. Zero or several matches fail
/// closed, so an ambiguity or a shape change is never patched by guesswork.
///
/// Pure logic: depends on <see cref="System.Reflection"/> only, never on
/// HarmonyLib or the game assemblies, so it compiles into the loader-free test
/// project.
/// </summary>
public static class GeneratedLambdaTargetResolver
{
    private const BindingFlags DeclaredInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Finds one generated lambda method declared directly on <paramref name="declaringType"/>.
    /// </summary>
    /// <param name="declaringType">Type that declares the generated lambda methods.</param>
    /// <param name="exactName">Current generated name, preferred when its signature matches.</param>
    /// <param name="familyPrefix">
    /// Name prefix shared by one factory's generated lambdas, for example
    /// <c>_GenerateMemoryCellArgs_b__</c>. A sibling factory's prefix is never considered.
    /// </param>
    /// <param name="parameterTypes">Exact parameter type list of the wanted lambda.</param>
    /// <param name="returnType">Exact return type of the wanted lambda.</param>
    /// <returns>The unique matching method, or null when none or several match.</returns>
    public static MethodInfo? Resolve(
        Type declaringType,
        string exactName,
        string familyPrefix,
        Type[] parameterTypes,
        Type returnType)
    {
        var exact = declaringType.GetMethod(
            exactName,
            DeclaredInstance,
            binder: null,
            parameterTypes,
            modifiers: null);
        if (exact is not null && HasSignature(exact, parameterTypes, returnType))
        {
            return exact;
        }

        MethodInfo? match = null;
        foreach (var candidate in declaringType.GetMethods(DeclaredInstance))
        {
            if (!candidate.Name.StartsWith(familyPrefix, StringComparison.Ordinal)
                || !HasSignature(candidate, parameterTypes, returnType))
            {
                continue;
            }

            if (match is not null)
            {
                return null;
            }

            match = candidate;
        }

        return match;
    }

    private static bool HasSignature(MethodInfo method, Type[] parameterTypes, Type returnType)
    {
        if (method.ReturnType != returnType)
        {
            return false;
        }

        var parameters = method.GetParameters();
        if (parameters.Length != parameterTypes.Length)
        {
            return false;
        }

        for (var index = 0; index < parameters.Length; index++)
        {
            if (parameters[index].ParameterType != parameterTypes[index])
            {
                return false;
            }
        }

        return true;
    }
}
