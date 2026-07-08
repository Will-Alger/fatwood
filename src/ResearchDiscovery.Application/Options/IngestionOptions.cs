using System.ComponentModel.DataAnnotations;

namespace ResearchDiscovery.Application.Options;

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    [Required]
    public BackfillOptions Backfill { get; set; } = new();

    [Required]
    public ScheduleOptions Schedule { get; set; } = new();

    /// <summary>A held ingestion lease older than this is considered abandoned (crashed process).</summary>
    [Range(5, 24 * 60)]
    public int LockStaleAfterMinutes { get; set; } = 120;

    public sealed class BackfillOptions
    {
        [Range(1, 3650)]
        public int WindowDays { get; set; } = 90;

        [Range(1, 100_000)]
        public int MaxPapersPerCategory { get; set; } = 10_000;
    }

    public sealed class ScheduleOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>Daily run time in UTC, "HH:mm".</summary>
        [Required]
        [RegularExpression("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
        public string TimeUtc { get; set; } = "06:30";
    }
}
