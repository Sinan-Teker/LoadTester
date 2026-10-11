using System.Globalization;
using LoadTester.Core.Scenarios;
using LoadTester.Core.Serialization;
using LoadTester.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace LoadTester.Web.Services;

public sealed record ScenarioSummary(int Id, string Name, string? Description, ScenarioDefinition Definition, LastRun? LastRun);

public sealed record LastRun(int Id, RunStatus Status, DateTime StartedAt);

public sealed class ScenarioStore(IDbContextFactory<AppDbContext> dbFactory)
{
    private const int MaxNameLength = 200;
    private static readonly StringComparer NameOrder = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true);

    public static ScenarioDefinition ParseDefinition(string json) => LoadTesterJson.Deserialize<ScenarioDefinition>(json);

    public async Task<List<ScenarioSummary>> ListAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var scenarios = await db.Scenarios.AsNoTracking().ToListAsync();
        var lastRuns = await db.Runs.AsNoTracking()
            .Where(r => r.Id == db.Runs.Where(x => x.ScenarioId == r.ScenarioId).Max(x => x.Id))
            .Select(r => new { r.ScenarioId, r.Id, r.Status, r.StartedAt })
            .ToDictionaryAsync(r => r.ScenarioId, r => new LastRun(r.Id, r.Status, r.StartedAt));

        return scenarios
            .OrderBy(s => s.Name, NameOrder)
            .Select(s => new ScenarioSummary(s.Id, s.Name, s.Description, ParseDefinition(s.DefinitionJson), lastRuns.GetValueOrDefault(s.Id)))
            .ToList();
    }

    public async Task<ScenarioEntity?> FindAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Scenarios.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
    }

    /// <summary>Creates the scenario when <paramref name="id"/> is null, otherwise updates it. Returns its id.</summary>
    public async Task<int> SaveAsync(int? id, string name, string? description, ScenarioDefinition definition)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;

        ScenarioEntity entity;
        if (id is null)
        {
            entity = new ScenarioEntity { CreatedAt = now };
            db.Scenarios.Add(entity);
        }
        else
        {
            entity = await db.Scenarios.FirstOrDefaultAsync(s => s.Id == id)
                ?? throw new InvalidOperationException("Senaryo bulunamadı; silinmiş olabilir.");
        }

        entity.Name = Truncate(name);
        entity.Description = description;
        entity.DefinitionJson = LoadTesterJson.Serialize(definition);
        entity.UpdatedAt = now;
        await db.SaveChangesAsync();
        return entity.Id;
    }

    /// <returns>The id of the copy, or null when the source no longer exists.</returns>
    public async Task<int?> DuplicateAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var source = await db.Scenarios.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (source is null)
            return null;

        var now = DateTime.UtcNow;
        var copy = new ScenarioEntity
        {
            Name = Truncate(source.Name + " (kopya)"),
            Description = source.Description,
            DefinitionJson = source.DefinitionJson,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Scenarios.Add(copy);
        await db.SaveChangesAsync();
        return copy.Id;
    }

    /// <summary>Deletes the scenario; its past runs stay in the history.</summary>
    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Scenarios.Where(s => s.Id == id).ExecuteDeleteAsync();
    }

    private static string Truncate(string name) => name.Length <= MaxNameLength ? name : name[..MaxNameLength];
}
