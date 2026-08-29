using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MuvluvUnlockCG.Core;

public readonly record struct LocalSceneFrame(
    int Order,
    long SceneId,
    long? BranchId,
    long? SelectedBranchId,
    string ConfigurationJson);

/// <summary>
/// Maps the five fields proven equal between G4 muvluvFrame commands and the
/// original SceneFrameMaster cache. Renderer-only G4 fields stay ignored.
/// </summary>
public static class G4SceneFrameDocument
{
    public static bool TryParse(long expectedSceneId, string json, out LocalSceneFrame[] frames)
    {
        frames = Array.Empty<LocalSceneFrame>();
        if (expectedSceneId <= 0 || string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasExactProperties(root, "assets", "commands", "id", "notes", "preloaded", "title")
                || !root.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || !long.TryParse(
                    id.GetString(),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var documentSceneId)
                || documentSceneId != expectedSceneId
                || id.GetString() != expectedSceneId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || !root.TryGetProperty("commands", out var commands)
                || commands.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var result = new List<LocalSceneFrame>();
            int? previousOrder = null;
            foreach (var command in commands.EnumerateArray())
            {
                if (command.ValueKind != JsonValueKind.Object
                    || !command.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || type.GetString() != "muvluvFrame")
                {
                    continue;
                }

                if (!command.TryGetProperty("frame", out var frame)
                    || frame.ValueKind != JsonValueKind.Object
                    || !HasExactProperties(
                        frame,
                        "background",
                        "branchId",
                        "characters",
                        "configuration",
                        "needsHideText",
                        "order",
                        "sceneId",
                        "selectedBranchId")
                    || !frame.TryGetProperty("order", out var orderElement)
                    || !orderElement.TryGetInt32(out var order)
                    || previousOrder.HasValue && order <= previousOrder.Value
                    || !frame.TryGetProperty("sceneId", out var sceneElement)
                    || !sceneElement.TryGetInt64(out var sceneId)
                    || sceneId != expectedSceneId
                    || !frame.TryGetProperty("branchId", out var branchElement)
                    || !TryReadNullableLong(branchElement, out var branchId)
                    || !frame.TryGetProperty("selectedBranchId", out var selectedElement)
                    || !TryReadNullableLong(selectedElement, out var selectedBranchId)
                    || !frame.TryGetProperty("configuration", out var configuration)
                    || configuration.ValueKind != JsonValueKind.Object
                    || !frame.TryGetProperty("background", out var background)
                    || background.ValueKind != JsonValueKind.Object
                    || !frame.TryGetProperty("characters", out var characters)
                    || characters.ValueKind != JsonValueKind.Array
                    || !frame.TryGetProperty("needsHideText", out var needsHideText)
                    || needsHideText.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return false;
                }

                result.Add(new LocalSceneFrame(
                    order,
                    sceneId,
                    branchId,
                    selectedBranchId,
                    configuration.GetRawText()));
                previousOrder = order;
            }

            if (result.Count == 0)
            {
                return false;
            }

            frames = result.ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasExactProperties(JsonElement value, params string[] expected)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var propertyCount = 0;
        foreach (var property in value.EnumerateObject())
        {
            propertyCount++;
            names.Add(property.Name);
        }

        if (propertyCount != expected.Length || names.Count != expected.Length)
        {
            return false;
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (!names.Contains(expected[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadNullableLong(JsonElement value, out long? result)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            result = null;
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            result = number;
            return true;
        }

        result = null;
        return false;
    }
}
