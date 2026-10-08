using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Memory;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Memory;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Memory;

public sealed class GeneralMemoryStore(AssistantDbContext db, ICurrentFamily family, IClock clock)
    : IGeneralMemoryStore
{
    private IQueryable<GeneralMemoryFact> Facts(GeneralMemoryScope s) => db.Set<GeneralMemoryFact>()
        .Where(x => x.FamilyId == s.FamilyId && x.BotId == s.BotId && x.ChatId == s.ChatId && x.TopicId == s.TopicId);
    private IQueryable<GeneralMemoryState> States(GeneralMemoryScope s) => db.Set<GeneralMemoryState>()
        .Where(x => x.FamilyId == s.FamilyId && x.BotId == s.BotId && x.ChatId == s.ChatId && x.TopicId == s.TopicId);
    private IQueryable<StoredMessage> Messages(GeneralMemoryScope s) => db.Messages.AsNoTracking()
        .Where(x => x.FamilyId == s.FamilyId && x.BotId == s.BotId && x.ChatId == s.ChatId
            && x.TopicId == s.TopicId && x.Kind == MessageKind.Text && x.Text != null
            && x.Text.Trim() != "" && !x.Text.TrimStart().StartsWith("/"));

    private async Task AuthorizeAsync(GeneralMemoryScope s, CancellationToken ct)
    {
        if (family.FamilyId != s.FamilyId || s.FamilyId <= 0)
            throw new InvalidOperationException("Memory scope is unavailable.");
        var bot = await db.Bots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == s.BotDbId
            && x.TelegramBotId == s.BotId && x.FamilyId == s.FamilyId && x.Status == BotStatus.Active
            && x.Role == "general", ct);
        var member = await db.FamilyMembers.AsNoTracking().AnyAsync(x => x.FamilyId == s.FamilyId
            && x.TelegramUserId == s.ActorUserId && x.Status == FamilyMemberStatus.Approved, ct);
        var place = s.ChatType == "private"
            ? s.ChatId == s.ActorUserId && s.TopicId == null
            : s.ChatType is "group" or "supergroup" && await db.Places.AsNoTracking().AnyAsync(
                x => x.BotId == s.BotDbId && x.ChatId == s.ChatId && x.TopicId == s.TopicId
                    && x.Status == PlaceStatus.Approved, ct);
        if (bot is null || !member || !place)
            throw new InvalidOperationException("Memory scope is unavailable.");
    }

    private Task LockAsync(long botId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({botId})", ct);
    private async Task<long> CutoffAsync(GeneralMemoryScope s, CancellationToken ct) =>
        await db.ChatSettings.AsNoTracking().Where(x => x.FamilyId == s.FamilyId && x.BotId == s.BotId
            && x.ChatId == s.ChatId && x.TopicId == s.TopicId)
            .Select(x => x.ContextStartMessageId ?? 0).SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<GeneralSearchHit>> SearchAsync(GeneralMemoryScope s, string query, CancellationToken ct)
    {
        await AuthorizeAsync(s, ct);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200 || !query.Any(char.IsLetterOrDigit))
            return [];
        var cutoff = await CutoffAsync(s, ct);
        return await Messages(s).Where(x => x.Id > cutoff && x.Direction == MessageDirection.In
            && (EF.Functions.ToTsVector("russian", x.Text!).Matches(EF.Functions.PlainToTsQuery("russian", query))
                || EF.Functions.ToTsVector("simple", x.Text!).Matches(EF.Functions.PlainToTsQuery("simple", query))))
            .OrderByDescending(x => EF.Functions.ToTsVector("russian", x.Text!).Rank(EF.Functions.PlainToTsQuery("russian", query))
                + EF.Functions.ToTsVector("simple", x.Text!).Rank(EF.Functions.PlainToTsQuery("simple", query)))
            .ThenByDescending(x => x.Id).Take(10)
            .Select(x => new GeneralSearchHit(x.Id, x.TelegramMessageId,
                x.Text!.Length > 400 ? x.Text.Substring(0, 400) : x.Text, x.SentAt)).ToListAsync(ct);
    }

    public async Task<GeneralRememberResult> RememberAsync(GeneralMemoryScope s, long sourceMessageId, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 500) return new(null, "invalid");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(s.BotId, ct);
        await AuthorizeAsync(s, ct);
        var source = await db.Messages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceMessageId
            && x.FamilyId == s.FamilyId && x.BotId == s.BotId && x.ChatId == s.ChatId
            && x.TopicId == s.TopicId && x.UserId == s.ActorUserId && x.Direction == MessageDirection.In
            && x.Kind == MessageKind.Text, ct);
        var botUsername = await db.Bots.AsNoTracking().Where(x => x.Id == s.BotDbId).Select(x => x.Username).SingleAsync(ct);
        if (source?.Text is not { } command || CommandParser.Parse(command, botUsername) != "remember"
            || CommandParser.ParseArgs(command) != text) return new(null, "source");
        var existing = await Facts(s).AsNoTracking().SingleOrDefaultAsync(x => x.SourceMessageId == sourceMessageId, ct);
        if (existing is not null)
            return existing.Text == text && existing.ActorUserId == s.ActorUserId
                ? new(existing.Id, null, existing.RetiredAt != null) : new(null, "mismatch");
        if (await Facts(s).CountAsync(x => x.RetiredAt == null, ct) >= 50) return new(null, "full");
        var fact = new GeneralMemoryFact
        {
            FamilyId = s.FamilyId, BotId = s.BotId, ChatId = s.ChatId, TopicId = s.TopicId,
            SourceMessageId = sourceMessageId, ActorUserId = s.ActorUserId, Text = text,
            CreatedAt = clock.UtcNow
        };
        db.Set<GeneralMemoryFact>().Add(fact);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(fact.Id, null);
        }
        finally { db.Entry(fact).State = EntityState.Detached; }
    }

    public async Task<bool> ForgetAsync(GeneralMemoryScope s, long factId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(s.BotId, ct);
        await AuthorizeAsync(s, ct);
        var fact = await Facts(s).SingleOrDefaultAsync(x => x.Id == factId, ct);
        if (fact is null) return false;
        try
        {
            if (fact.RetiredAt == null)
            {
                fact.RetiredAt = clock.UtcNow;
                fact.RetiredByUserId = s.ActorUserId;
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return true;
        }
        finally { db.Entry(fact).State = EntityState.Detached; }
    }

    public async Task<GeneralMemorySnapshot> ReadAsync(GeneralMemoryScope s, CancellationToken ct)
    {
        await AuthorizeAsync(s, ct);
        var cutoff = await CutoffAsync(s, ct);
        var facts = await Facts(s).AsNoTracking().Where(x => x.RetiredAt == null)
            .OrderByDescending(x => x.Id).Take(50).Select(x => new GeneralFactInfo(x.Id, x.Text)).ToListAsync(ct);
        var state = await States(s).AsNoTracking().SingleOrDefaultAsync(ct);
        var summary = state is not null && state.ResetCutoff == cutoff && state.SummaryText.Length > 0
            ? new GeneralSummaryInfo(state.SummaryText, state.ThroughMessageId) : null;
        return new(facts, summary);
    }

    public async Task<GeneralSummaryFold?> PrepareFoldAsync(GeneralMemoryScope s, long beforeMessageId,
        int recentCount, CancellationToken ct)
    {
        if (recentCount <= 0 || beforeMessageId <= 0) return null;
        GeneralMemoryState? state = null;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockAsync(s.BotId, ct);
            await AuthorizeAsync(s, ct);
            var cutoff = await CutoffAsync(s, ct);
            state = await States(s).SingleOrDefaultAsync(ct);
            if (state is null)
            {
                state = new GeneralMemoryState
                {
                    FamilyId = s.FamilyId, BotId = s.BotId, ChatId = s.ChatId, TopicId = s.TopicId,
                    ResetCutoff = cutoff, ThroughMessageId = cutoff
                };
                db.Set<GeneralMemoryState>().Add(state);
            }
            else if (state.ResetCutoff != cutoff)
            {
                state.ResetCutoff = cutoff;
                state.ThroughMessageId = cutoff;
                state.SourceVersion++;
                state.SummaryText = "";
                state.SourceFingerprint = "";
                state.GeneratedAt = null;
                state.ModelName = null;
            }
            await db.SaveChangesAsync(ct);
            var recentStart = await Messages(s).Where(x => x.Id > cutoff && x.Id < beforeMessageId)
                .OrderByDescending(x => x.Id).Take(recentCount).Select(x => (long?)x.Id).MinAsync(ct);
            if (recentStart is null) return null;
            var eligible = Messages(s).Where(x => x.Id > state.ThroughMessageId && x.Id < recentStart.Value);
            var rows = await eligible.OrderBy(x => x.Id).Take(50)
                .Select(x => new GeneralSummarySource(x.Id, x.Direction, x.Text!, x.EditedAt, x.SentAt, x.Username, x.UserId)).ToListAsync(ct);
            if (rows.Count < 20) return null;
            var selected = new List<GeneralSummarySource>();
            var chars = 0;
            foreach (var row in rows)
            {
                var length = Math.Min(row.Text.Length, 4000);
                if (chars + length > 12000) break;
                selected.Add(row);
                chars += length;
            }
            if (selected.Count == 0) return null;
            var fold = new GeneralSummaryFold(cutoff, state.ThroughMessageId, state.SourceVersion,
                state.SummaryText, selected);
            await tx.CommitAsync(ct);
            return fold;
        }
        finally
        {
            if (state is not null) db.Entry(state).State = EntityState.Detached;
        }
    }

    public async Task<bool> CommitFoldAsync(GeneralMemoryScope s, GeneralSummaryFold fold, string text,
        string modelName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 3000 || fold.Sources.Count == 0
            || modelName.Length > 256) return false;
        GeneralMemoryState? state = null;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockAsync(s.BotId, ct);
            await AuthorizeAsync(s, ct);
            var cutoff = await CutoffAsync(s, ct);
            state = await States(s).SingleOrDefaultAsync(ct);
            if (state is null || cutoff != fold.ResetCutoff || state.ResetCutoff != fold.ResetCutoff
                || state.ThroughMessageId != fold.ThroughMessageId || state.SourceVersion != fold.SourceVersion)
                return false;
            var ids = fold.Sources.Select(x => x.Id).ToArray();
            var current = await Messages(s).Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id)
                .Select(x => new GeneralSummarySource(x.Id, x.Direction, x.Text!, x.EditedAt, x.SentAt, x.Username, x.UserId)).ToListAsync(ct);
            if (new GeneralSummaryFold(fold.ResetCutoff, fold.ThroughMessageId, fold.SourceVersion,
                fold.PreviousText, current).Fingerprint != fold.Fingerprint) return false;
            state.SummaryText = text;
            state.ThroughMessageId = fold.Sources[^1].Id;
            state.SourceVersion++;
            state.SourceFingerprint = fold.Fingerprint;
            state.ModelName = modelName;
            state.GeneratedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        finally
        {
            if (state is not null) db.Entry(state).State = EntityState.Detached;
        }
    }
}
