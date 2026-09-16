using System.Security.Cryptography;
using Benchmix.Engine.Audio;

namespace Benchmix.Engine.Stems;

/// <summary>A separation model: where to get it, how to check it, and how to drive it.</summary>
public sealed record StemModel(
    string Id,
    string DisplayName,
    string Url,
    long SizeBytes,
    string Sha256,
    MdxParams Parameters,
    string SecondaryStemName)
{
    public string FileName => Id + ".onnx";
}

/// <summary>
/// Finds, fetches and verifies the separation model.
///
/// The model is ~64 MB, so it is downloaded on first use rather than embedded — that keeps the
/// app's own download small, and means someone who never wants stems never pays for them. The
/// SHA-256 is pinned: MDX-Net weights are mirrored across community repos rather than one
/// authoritative host, so "it downloaded successfully" is not the same as "it is the right file".
/// A mismatch deletes the file and fails loudly.
/// </summary>
public static class StemModelStore
{
    /// <summary>
    /// UVR's vocal model, fine-tuned. Values verified against UVR's own model registry, which keys
    /// each model by the MD5 of its last 10 MB — so these are read from the source of truth rather
    /// than guessed from a blog post.
    /// </summary>
    public static readonly StemModel VocalsModel = new(
        Id: "UVR-MDX-NET-Voc_FT",
        DisplayName: "Vocals / instrumental (UVR MDX-Net Voc_FT)",
        Url: "https://github.com/TRvlvr/model_repo/releases/download/all_public_uvr_models/UVR-MDX-NET-Voc_FT.onnx",
        SizeBytes: 66_762_490,
        Sha256: "534b2070fcc7df514b13ef660dc8cbb328679c2374d04354a5c42bb14ecce111",
        Parameters: new MdxParams
        {
            NFft = 7680,
            DimF = 3072,
            DimT = 256,
            Hop = 1024,
            Compensate = 1.021,
            PrimaryStem = "Vocals",
        },
        SecondaryStemName: "Instrumental");

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Benchmix", "models");

    public static string PathFor(StemModel model) => Path.Combine(Directory, model.FileName);

    /// <summary>
    /// True when the model is on disk at the expected size. The full checksum is verified once, at
    /// download or install time — re-hashing 64 MB on every call would just be a stall.
    /// </summary>
    public static bool IsInstalled(StemModel model)
    {
        var path = PathFor(model);
        if (!File.Exists(path)) return false;
        return new FileInfo(path).Length == model.SizeBytes;
    }

    /// <summary>
    /// Ensures the model is on disk, downloading it if needed, and returns its path. Verifies the
    /// checksum after any download; a mismatch removes the file and throws.
    /// </summary>
    public static async Task<string> EnsureAsync(StemModel model, IProgress<double>? progress = null,
                                                 CancellationToken cancel = default)
    {
        var path = PathFor(model);
        if (IsInstalled(model)) return path;

        System.IO.Directory.CreateDirectory(Directory);
        var temp = path + ".part";

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
        using (var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancel)
                                        .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long? length = response.Content.Headers.ContentLength;

            await using var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[128 * 1024];
            long read = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
                read += n;
                if (length is > 0) progress?.Report(read / (double)length.Value);
            }
        }

        var actual = await Task.Run(() => Sha256Of(temp), cancel).ConfigureAwait(false);
        if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(temp); } catch { }
            throw new InvalidDataException(
                $"The downloaded model does not match its expected checksum.\nExpected {model.Sha256}\nGot      {actual}\n" +
                "The file was discarded rather than used.");
        }

        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
        return path;
    }

    /// <summary>Adopts an already-downloaded copy, verifying it first.</summary>
    public static void InstallFrom(StemModel model, string existingFile)
    {
        var actual = Sha256Of(existingFile);
        if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"That file is not {model.Id} (checksum {actual}).");

        System.IO.Directory.CreateDirectory(Directory);
        File.Copy(existingFile, PathFor(model), overwrite: true);
    }

    public static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}

public sealed record StemResult(AudioClip Primary, AudioClip Secondary, string ExecutionProvider,
                                double Seconds, double BandLimitHz);

/// <summary>
/// Splits a song into two stems, in this process, on the GPU.
///
/// This is offered <b>alongside</b> the EQ, not instead of it — killing a band with the LOW knob
/// is instant and lossless, while separation takes about twenty seconds and invents a little. Use
/// the EQ when it is enough; use this when you need the voice on its own.
/// </summary>
public static class StemSeparator
{
    public static bool IsModelInstalled => StemModelStore.IsInstalled(StemModelStore.VocalsModel);

    /// <summary>
    /// Separates a clip into vocals and instrumental. Progress runs 0→1 over the whole job, with
    /// the model download (first run only) taking the first slice of it.
    /// </summary>
    public static async Task<StemResult> SeparateAsync(AudioClip clip, bool useGpu = true,
                                                       IProgress<double>? progress = null,
                                                       CancellationToken cancel = default)
    {
        var model = StemModelStore.VocalsModel;
        bool needsDownload = !StemModelStore.IsInstalled(model);

        var download = needsDownload
            ? new Progress<double>(p => progress?.Report(p * 0.15))
            : null;

        var path = await StemModelStore.EnsureAsync(model, download, cancel).ConfigureAwait(false);

        double floor = needsDownload ? 0.15 : 0.0;
        var work = new Progress<double>(p => progress?.Report(floor + p * (1 - floor)));

        return await Task.Run(() =>
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            using var separator = new MdxSeparator(path, model.Parameters, useGpu);
            var (primary, secondary) = separator.Separate(clip.Samples, work, cancel);
            started.Stop();

            string baseName = Path.GetFileNameWithoutExtension(clip.SourcePath);
            return new StemResult(
                AudioClip.FromSamples($"{baseName} [{model.Parameters.PrimaryStem}]", primary, clip.SampleRate),
                AudioClip.FromSamples($"{baseName} [{model.SecondaryStemName}]", secondary, clip.SampleRate),
                separator.ExecutionProvider,
                started.Elapsed.TotalSeconds,
                model.Parameters.BandLimitHz(clip.SampleRate));
        }, cancel).ConfigureAwait(false);
    }
}
