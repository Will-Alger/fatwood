using System.Text.Json;
using ResearchDiscovery.Application.Eval;

namespace ResearchDiscovery.Infrastructure.Eval;

/// <summary>
/// Load/save for the two eval artifacts. Files are the source of truth and
/// live in the repo (eval/), so every judgment and plan change is reviewable
/// in a diff — deliberately NOT database rows.
/// </summary>
public static class EvalFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static EvalQuerySet LoadQueries(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Eval query set not found at '{path}'. It is a versioned repo artifact — " +
                "check out eval/queries.json or pass --queries.", path);
        }

        return JsonSerializer.Deserialize<EvalQuerySet>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"'{path}' deserialized to null.");
    }

    public static void SaveQueries(string path, EvalQuerySet set) => Save(path, set);

    public static EvalJudgmentSet LoadJudgmentsOrEmpty(string path, int rubricVersion)
    {
        if (!File.Exists(path))
        {
            return new EvalJudgmentSet(1, rubricVersion, []);
        }

        return JsonSerializer.Deserialize<EvalJudgmentSet>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"'{path}' deserialized to null.");
    }

    public static void SaveJudgments(string path, EvalJudgmentSet set) => Save(path, set);

    /// <summary>Calibration reports are point-in-time audits; each save overwrites.</summary>
    public static void SaveCalibration<T>(string path, T report) => Save(path, report);

    /// <summary>Corpus fixtures gzip when the path ends in .gz (they're the one
    /// artifact big enough to care) and stay plain JSON otherwise.</summary>
    public static void SaveCorpus(string path, EvalRunner.CorpusFixture fixture)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.Create(path);
            using var gzip = new System.IO.Compression.GZipStream(
                file, System.IO.Compression.CompressionLevel.SmallestSize);
            JsonSerializer.Serialize(gzip, fixture, JsonOptions);
            return;
        }

        Save(path, fixture);
    }

    public static EvalRunner.CorpusFixture LoadCorpus(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Corpus fixture not found at '{path}'.", path);
        }

        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(path);
            using var gzip = new System.IO.Compression.GZipStream(
                file, System.IO.Compression.CompressionMode.Decompress);
            return JsonSerializer.Deserialize<EvalRunner.CorpusFixture>(gzip, JsonOptions)
                ?? throw new InvalidOperationException($"'{path}' deserialized to null.");
        }

        return JsonSerializer.Deserialize<EvalRunner.CorpusFixture>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"'{path}' deserialized to null.");
    }

    private static void Save<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }
}
