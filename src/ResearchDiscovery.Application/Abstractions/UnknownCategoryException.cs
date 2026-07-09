namespace ResearchDiscovery.Application.Abstractions;

/// <summary>Thrown when an analysis run targets a category code not present in the database.</summary>
public class UnknownCategoryException(string categoryCode)
    : Exception($"Unknown category code '{categoryCode}'.")
{
    public string CategoryCode { get; } = categoryCode;
}
