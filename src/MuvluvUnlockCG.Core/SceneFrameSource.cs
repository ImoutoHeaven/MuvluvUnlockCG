using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MuvluvUnlockCG.Core;

public enum SceneFrameSourceKind
{
    None,
    Remote,
    Local,
}

public enum SceneFrameSourceStatus
{
    RemoteUnconfigured,
    ManifestUnavailable,
    ManifestIncompatible,
    IdAbsent,
    RemoteFetchInvalid,
    RemoteFetchUnavailable,
    RemoteValid,
    LocalValid,
    LocalMissing,
}

/// <summary>
/// A source result contains the final source/status plus the remote and local
/// observations that explain a fallback. No URI or response bytes are exposed.
/// </summary>
public readonly record struct SceneFrameResolution(
    long SceneId,
    SceneFrameSourceKind Source,
    SceneFrameSourceStatus Status,
    SceneFrameSourceStatus RemoteStatus,
    SceneFrameSourceStatus LocalStatus,
    LocalSceneFrame[] Frames)
{
    public bool IsAvailable => Source != SceneFrameSourceKind.None && Frames is { Length: > 0 };
}

/// <summary>
/// Reads the immutable G4 static protocol and the plugin-local fallback.
/// Remote access is deliberately synchronous because Character/Memory/Main
/// must finish their bounded preparation before native entry creates a session.
/// </summary>
public sealed class SceneFrameSource
{
    public const string DefaultRemoteBaseUrl =
        "https://raw.githubusercontent.com/ImoutoHeaven/MuvluvSceneFrame/main/";
    public const int MaxManifestBytes = 8 * 1024 * 1024;
    public const int MaxSceneBytes = 8 * 1024 * 1024;

    private readonly object _gate = new object();
    private readonly string _localRoot;
    private readonly Uri? _remoteBase;
    private readonly HttpClient _http;
    private readonly Dictionary<long, ManifestScene> _manifest = new();
    private readonly Dictionary<long, LocalSceneFrame[]> _remoteFrames = new();
    private readonly Dictionary<long, LocalSceneFrame[]> _localFrames = new();
    private bool _manifestAttempted;
    private SceneFrameSourceStatus _manifestStatus;

    public SceneFrameSource(
        string? remoteBaseUrl,
        string? localRoot,
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null)
    {
        _localRoot = localRoot ?? string.Empty;
        if (TryNormalizeRemoteBaseUrl(remoteBaseUrl, out var normalized))
        {
            _remoteBase = normalized;
        }

        if (handler is null)
        {
            handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip
                    | DecompressionMethods.Deflate
                    | DecompressionMethods.Brotli,
            };
        }

        _http = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };
        _manifestStatus = _remoteBase is null
            ? SceneFrameSourceStatus.RemoteUnconfigured
            : SceneFrameSourceStatus.ManifestUnavailable;
    }

    public bool RemoteConfigured => _remoteBase is not null;

    public SceneFrameSourceStatus ManifestStatus
    {
        get
        {
            lock (_gate)
            {
                EnsureManifestLocked();
                return _manifestStatus;
            }
        }
    }

    public SceneFrameResolution Resolve(long sceneId)
    {
        if (sceneId <= 0)
        {
            return new SceneFrameResolution(
                sceneId,
                SceneFrameSourceKind.None,
                SceneFrameSourceStatus.LocalMissing,
                _remoteBase is null
                    ? SceneFrameSourceStatus.RemoteUnconfigured
                    : SceneFrameSourceStatus.IdAbsent,
                SceneFrameSourceStatus.LocalMissing,
                Array.Empty<LocalSceneFrame>());
        }

        // ponytail: one global source lock keeps manifest/cache/file transitions
        // atomic; use per-scene locks only if measured concurrent loading matters.
        lock (_gate)
        {
            if (_remoteFrames.TryGetValue(sceneId, out var cachedRemote))
            {
                return Available(
                    sceneId,
                    SceneFrameSourceKind.Remote,
                    SceneFrameSourceStatus.RemoteValid,
                    SceneFrameSourceStatus.RemoteValid,
                    cachedRemote);
            }

            var remoteStatus = SceneFrameSourceStatus.RemoteUnconfigured;
            if (_remoteBase is not null)
            {
                EnsureManifestLocked();
                if (_manifestStatus == SceneFrameSourceStatus.RemoteValid)
                {
                    if (TryLoadRemoteLocked(sceneId, out cachedRemote, out remoteStatus))
                    {
                        return Available(
                            sceneId,
                            SceneFrameSourceKind.Remote,
                            SceneFrameSourceStatus.RemoteValid,
                            remoteStatus,
                            cachedRemote!);
                    }
                }
                else
                {
                    remoteStatus = _manifestStatus;
                }
                if (cachedRemote is not null)
                {
                    return Available(
                        sceneId,
                        SceneFrameSourceKind.Remote,
                        SceneFrameSourceStatus.RemoteValid,
                        remoteStatus,
                        cachedRemote);
                }
            }

            if (_localFrames.TryGetValue(sceneId, out var cachedLocal))
            {
                return Available(
                    sceneId,
                    SceneFrameSourceKind.Local,
                    SceneFrameSourceStatus.LocalValid,
                    remoteStatus,
                    cachedLocal);
            }

            var localStatus = TryLoadLocalLocked(sceneId, out cachedLocal)
                ? SceneFrameSourceStatus.LocalValid
                : SceneFrameSourceStatus.LocalMissing;
            if (cachedLocal is not null)
            {
                return Available(
                    sceneId,
                    SceneFrameSourceKind.Local,
                    SceneFrameSourceStatus.LocalValid,
                    remoteStatus,
                    cachedLocal);
            }

            return new SceneFrameResolution(
                sceneId,
                SceneFrameSourceKind.None,
                localStatus,
                remoteStatus,
                localStatus,
                Array.Empty<LocalSceneFrame>());
        }
    }

    public bool TryPrepare(long sceneId, out SceneFrameResolution resolution)
    {
        resolution = Resolve(sceneId);
        return resolution.IsAvailable;
    }

    public bool TryPrepareAll(
        IReadOnlyList<long> sceneIds,
        out SceneFrameResolution[] resolutions)
    {
        if (sceneIds is null || sceneIds.Count == 0)
        {
            resolutions = Array.Empty<SceneFrameResolution>();
            return false;
        }

        resolutions = new SceneFrameResolution[sceneIds.Count];
        var allAvailable = true;
        for (var index = 0; index < sceneIds.Count; index++)
        {
            resolutions[index] = Resolve(sceneIds[index]);
            allAvailable &= resolutions[index].IsAvailable;
        }

        return allAvailable;
    }

    /// <summary>
    /// Main catalog may use remote manifest membership as a lightweight
    /// availability hint; entry preparation still validates every body.
    /// </summary>
    public bool HasPotentialScene(long sceneId)
    {
        if (sceneId <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_remoteBase is not null)
            {
                EnsureManifestLocked();
                if (_manifestStatus == SceneFrameSourceStatus.RemoteValid
                    && _manifest.ContainsKey(sceneId))
                {
                    return true;
                }
            }

            if (_localFrames.ContainsKey(sceneId))
            {
                return true;
            }

            return TryLoadLocalLocked(sceneId, out _);
        }
    }

    public static bool TryNormalizeRemoteBaseUrl(string? value, out Uri? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        var path = parsed.AbsolutePath.TrimEnd('/') + "/";
        var builder = new UriBuilder(parsed)
        {
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        normalized = builder.Uri;
        return true;
    }

    private static SceneFrameResolution Available(
        long sceneId,
        SceneFrameSourceKind source,
        SceneFrameSourceStatus status,
        SceneFrameSourceStatus remoteStatus,
        LocalSceneFrame[] frames)
    {
        return new SceneFrameResolution(
            sceneId,
            source,
            status,
            remoteStatus,
            source == SceneFrameSourceKind.Local
                ? SceneFrameSourceStatus.LocalValid
                : SceneFrameSourceStatus.LocalMissing,
            frames);
    }

    private bool TryLoadRemoteLocked(
        long sceneId,
        out LocalSceneFrame[]? frames,
        out SceneFrameSourceStatus status)
    {
        frames = null;
        status = SceneFrameSourceStatus.IdAbsent;
        if (!_manifest.TryGetValue(sceneId, out var manifestScene))
        {
            return false;
        }

        var uri = BuildCanonicalSceneUri(sceneId);
        if (uri is null)
        {
            status = SceneFrameSourceStatus.RemoteFetchInvalid;
            return false;
        }

        var fetch = FetchBytes(uri, MaxSceneBytes);
        if (fetch.Status != SceneFrameSourceStatus.RemoteValid)
        {
            status = fetch.Status;
            return false;
        }

        if (fetch.Bytes.Length != manifestScene.ByteCount
            || !string.Equals(
                Convert.ToHexString(SHA256.HashData(fetch.Bytes)),
                manifestScene.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            status = SceneFrameSourceStatus.RemoteFetchInvalid;
            return false;
        }

        if (!TryParseBytes(sceneId, fetch.Bytes, out var parsedFrames))
        {
            status = SceneFrameSourceStatus.RemoteFetchInvalid;
            return false;
        }

        frames = parsedFrames;
        _remoteFrames[sceneId] = parsedFrames!;
        status = SceneFrameSourceStatus.RemoteValid;
        return true;
    }

    private bool TryLoadLocalLocked(long sceneId, out LocalSceneFrame[]? frames)
    {
        frames = null;
        if (string.IsNullOrWhiteSpace(_localRoot))
        {
            return false;
        }

        try
        {
            var path = LocalScenePath(sceneId);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxSceneBytes)
            {
                return false;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaxSceneBytes || !TryParseBytes(sceneId, bytes, out var parsedFrames))
            {
                frames = null;
                return false;
            }

            frames = parsedFrames;
            _localFrames[sceneId] = parsedFrames!;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void EnsureManifestLocked()
    {
        if (_manifestAttempted)
        {
            return;
        }

        _manifestAttempted = true;
        if (_remoteBase is null)
        {
            _manifestStatus = SceneFrameSourceStatus.RemoteUnconfigured;
            return;
        }

        var uri = BuildCanonicalManifestUri();
        if (uri is null)
        {
            _manifestStatus = SceneFrameSourceStatus.ManifestIncompatible;
            return;
        }

        var fetch = FetchBytes(uri, MaxManifestBytes);
        if (fetch.Status != SceneFrameSourceStatus.RemoteValid)
        {
            _manifestStatus = SceneFrameSourceStatus.ManifestUnavailable;
            return;
        }

        if (!TryParseManifest(fetch.Bytes, out var parsed))
        {
            _manifestStatus = SceneFrameSourceStatus.ManifestIncompatible;
            return;
        }

        _manifest.Clear();
        foreach (var scene in parsed)
        {
            _manifest.Add(scene.SceneId, scene);
        }

        _manifestStatus = SceneFrameSourceStatus.RemoteValid;
    }

    private FetchResult FetchBytes(Uri uri, int maxBytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var timeout = new System.Threading.CancellationTokenSource(_http.Timeout);
        try
        {
            using var response = _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).GetAwaiter().GetResult();
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
            {
                return new FetchResult(Array.Empty<byte>(), SceneFrameSourceStatus.RemoteFetchUnavailable);
            }

            var declaredLength = response.Content?.Headers.ContentLength;
            if (response.Content is null
                || declaredLength.HasValue && declaredLength.Value > maxBytes)
            {
                return new FetchResult(Array.Empty<byte>(), SceneFrameSourceStatus.RemoteFetchInvalid);
            }

            using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = stream.ReadAsync(
                    buffer.AsMemory(),
                    timeout.Token).GetAwaiter().GetResult();
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > maxBytes)
                {
                    return new FetchResult(Array.Empty<byte>(), SceneFrameSourceStatus.RemoteFetchInvalid);
                }

                output.Write(buffer, 0, read);
            }

            return new FetchResult(output.ToArray(), SceneFrameSourceStatus.RemoteValid);
        }
        catch (OperationCanceledException)
        {
            return new FetchResult(Array.Empty<byte>(), SceneFrameSourceStatus.RemoteFetchUnavailable);
        }
        catch (HttpRequestException)
        {
            return new FetchResult(Array.Empty<byte>(), SceneFrameSourceStatus.RemoteFetchUnavailable);
        }
        catch (IOException)
        {
            return new FetchResult(Array.Empty<byte>(), SceneFrameSourceStatus.RemoteFetchUnavailable);
        }
    }

    private static bool TryParseManifest(byte[] bytes, out List<ManifestScene> scenes)
    {
        scenes = new List<ManifestScene>();
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasUniqueProperties(root)
                || !root.TryGetProperty("format", out var format)
                || format.ValueKind != JsonValueKind.String
                || format.GetString() != "muvluv-g4-scene-export-v1"
                || !root.TryGetProperty("scenes", out var sceneArray)
                || sceneArray.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var ids = new HashSet<long>();
            foreach (var item in sceneArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !HasUniqueProperties(item)
                    || !item.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || !TryParseCanonicalId(id.GetString(), out var sceneId)
                    || !item.TryGetProperty("bytes", out var byteCount)
                    || !byteCount.TryGetInt32(out var bytesValue)
                    || bytesValue <= 0
                    || bytesValue > MaxSceneBytes
                    || !item.TryGetProperty("sha256", out var sha256)
                    || sha256.ValueKind != JsonValueKind.String
                    || !IsSha256(sha256.GetString())
                    || !ids.Add(sceneId))
                {
                    scenes.Clear();
                    return false;
                }

                scenes.Add(new ManifestScene(sceneId, bytesValue, sha256.GetString()!));
            }

            if (root.TryGetProperty("sceneCount", out var sceneCount)
                && (!sceneCount.TryGetInt32(out var count) || count != scenes.Count))
            {
                scenes.Clear();
                return false;
            }

            return scenes.Count > 0;
        }
        catch (JsonException)
        {
            scenes.Clear();
            return false;
        }
    }

    private static bool HasUniqueProperties(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseBytes(long sceneId, byte[] bytes, out LocalSceneFrame[]? frames)
    {
        frames = Array.Empty<LocalSceneFrame>();
        try
        {
            var json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            if (json.Length > 0 && json[0] == '\uFEFF')
            {
                json = json[1..];
            }

            return G4SceneFrameDocument.TryParse(sceneId, json, out frames);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private Uri? BuildCanonicalManifestUri()
    {
        if (_remoteBase is null)
        {
            return null;
        }

        return BuildCanonicalUri("manifest.json");
    }

    private Uri? BuildCanonicalSceneUri(long sceneId)
    {
        if (_remoteBase is null || sceneId <= 0)
        {
            return null;
        }

        return BuildCanonicalUri(
            $"scene/{sceneId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/scene.json");
    }

    private Uri? BuildCanonicalUri(string relative)
    {
        var candidate = new Uri(_remoteBase!, relative);
        var expectedPath = _remoteBase!.AbsolutePath.TrimEnd('/') + "/" + relative;
        return string.Equals(candidate.AbsolutePath, expectedPath, StringComparison.Ordinal)
            && string.IsNullOrEmpty(candidate.Query)
            && string.IsNullOrEmpty(candidate.Fragment)
                ? candidate
                : null;
    }

    private string LocalScenePath(long sceneId)
    {
        return Path.Combine(
            _localRoot,
            "scene",
            sceneId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "scene.json");
    }

    private static bool TryParseCanonicalId(string? value, out long sceneId)
    {
        sceneId = 0;
        return long.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out sceneId)
            && sceneId > 0
            && value == sceneId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!(character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct ManifestScene(long SceneId, int ByteCount, string Sha256);

    private readonly record struct FetchResult(byte[] Bytes, SceneFrameSourceStatus Status);
}
