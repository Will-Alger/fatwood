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
