using System.ComponentModel.DataAnnotations;

namespace OrderIngestion.Infrastructure.Persistence;

/// <summary>Bound from the "Database" config section.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>SQLite file. Relative paths resolve against the working directory (/app in the container).</summary>
    [Required]
    public string FilePath { get; init; } = string.Empty;
}
