using System.Text;
using System.Text.Json;
using Telegram.Bot.Types;

namespace Metriox.SDK.Telegram.Mappers;

/// <summary>
/// Serializes reactions into the canonical Metriox wire shape, and derives the two fields that make a
/// reaction event useful: which reaction it was, and what changed.
///
/// <para>Telegram models a reaction as a tagged union — <see cref="ReactionTypeEmoji"/>,
/// <see cref="ReactionTypeCustomEmoji"/>, <see cref="ReactionTypePaid"/> — which Metriox's MTProto worker
/// receives under the MTProto spellings <c>ReactionEmoji</c>/<c>ReactionCustomEmoji</c>/<c>ReactionPaid</c>.
/// Same union, two spellings; this maps onto the shared vocabulary so a reaction looks identical whichever
/// producer captured it.</para>
///
/// <para>Emits both a flat scalar and the full array, deliberately. <c>tg.reaction_emoji</c> answers
/// "which reactions do users give?" as a plain group-by; <c>tg.reactions</c> keeps multi-reaction and
/// custom-emoji fidelity. Previously this SDK sent neither — only a hardcoded
/// <c>tg.reaction_change = "updated"</c> and a count, so a reaction event could not say what the reaction
/// was.</para>
///
/// <para>Mirrors <c>Application.Contracts.Builders.TelegramReactions</c> in the Metriox backend, the same
/// way <see cref="MessageEntitySerializer"/> mirrors its entity codec. An SDK on a customer's machine
/// cannot reference backend code, so the vocabulary and the wire keys are restated here and held together
/// by tests on both sides.</para>
/// </summary>
public static class MessageReactionSerializer
{
    public const string KindEmoji = "emoji";
    public const string KindCustomEmoji = "custom_emoji";
    public const string KindPaid = "paid";

    public const string ChangeAdded = "added";
    public const string ChangeRemoved = "removed";
    public const string ChangeChanged = "changed";

    /// <summary>
    /// The honest value when there is no previous state to diff — a per-message totals snapshot. Not a
    /// default: an actor's reaction change always resolves to added/removed/changed.
    /// </summary>
    public const string ChangeUpdated = "updated";

    /// <summary>Matches the backend cap. Telegram allows far fewer; the limit bounds a malformed update.</summary>
    public const int MaxReactions = 32;

    private const string CustomTokenPrefix = "custom:";

    /// <summary>
    /// The DEFAULT encoder, deliberately. It escapes non-ASCII, so 👍 is stored as the escape pair
    /// <c>👍</c> — verified, not assumed. Any JSON parser decodes it back to the emoji, so this
    /// is invisible to consumers, and keeping the default matches the entity codec (which stores URLs,
    /// where escaping <c>&lt;</c> and <c>&amp;</c> is worth having). What matters is that every producer
    /// uses the SAME encoder, or the stored strings stop matching byte for byte; the tests on both sides
    /// pin the escaped form so a change here cannot pass silently.
    /// </summary>
    private static readonly JsonWriterOptions WriterOptions = new();

    /// <param name="Kind">Canonical discriminator.</param>
    /// <param name="Emoji">Unicode emoji, for <c>emoji</c> reactions.</param>
    /// <param name="CustomEmojiId">Document id, for <c>custom_emoji</c> reactions.</param>
    /// <param name="Count">User count; only on aggregate snapshots.</param>
    public sealed record Reaction(
        string Kind,
        string? Emoji = null,
        string? CustomEmojiId = null,
        int? Count = null);

    /// <summary>An actor's reaction list (<c>OldReaction</c>/<c>NewReaction</c>).</summary>
    public static IReadOnlyList<Reaction> Map(ReactionType[]? reactions)
    {
        if (reactions is null || reactions.Length == 0) return Array.Empty<Reaction>();

        var result = new List<Reaction>(reactions.Length);
        foreach (var r in reactions)
            if (ToCanonical(r, count: null) is { } mapped) result.Add(mapped);

        return result;
    }

    /// <summary>A per-message totals snapshot (<c>MessageReactionCountUpdated.Reactions</c>).</summary>
    public static IReadOnlyList<Reaction> Map(ReactionCount[]? counts)
    {
        if (counts is null || counts.Length == 0) return Array.Empty<Reaction>();

        var result = new List<Reaction>(counts.Length);
        foreach (var c in counts)
        {
            if (c is null) continue;
            if (ToCanonical(c.Type, c.TotalCount) is { } mapped) result.Add(mapped);
        }

        return result;
    }

    /// <summary>
    /// Compact JSON — <c>[{ "type": kind, "emoji": …?, "custom_emoji_id": …?, "total_count": …? }]</c> —
    /// or <c>null</c> when there is nothing to send.
    ///
    /// <para>The keys are the Bot-API field names (<c>ReactionType.type</c>/<c>.emoji</c>/
    /// <c>.custom_emoji_id</c>, <c>ReactionCount.total_count</c>). They were single letters
    /// (<c>t</c>/<c>e</c>/<c>i</c>/<c>c</c>) before 2026-07-26 — Metriox still accepts that spelling on
    /// read, so an older SDK build keeps working, but new sends should use this one.</para>
    /// </summary>
    public static string? Serialize(IEnumerable<Reaction>? reactions)
    {
        if (reactions is null) return null;

        var kept = reactions.Where(r => !string.IsNullOrEmpty(r.Kind)).Take(MaxReactions).ToList();
        if (kept.Count == 0) return null;

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartArray();
            foreach (var r in kept)
            {
                w.WriteStartObject();
                w.WriteString("type", r.Kind);
                if (!string.IsNullOrEmpty(r.Emoji)) w.WriteString("emoji", r.Emoji);
                if (!string.IsNullOrEmpty(r.CustomEmojiId)) w.WriteString("custom_emoji_id", r.CustomEmojiId);
                if (r.Count is { } c) w.WriteNumber("total_count", c);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// The flat, groupable token: the emoji itself, else <c>custom:{document_id}</c>, else the kind.
    /// Highest-count wins, so an aggregate snapshot names its dominant reaction rather than an arbitrary
    /// one. A custom emoji has no unicode character, so returning null for those would make grouping by
    /// this field silently omit them — hence the namespaced token, which cannot collide with a real emoji.
    /// </summary>
    public static string? PrimaryToken(IEnumerable<Reaction>? reactions)
    {
        if (reactions is null) return null;

        Reaction? best = null;
        foreach (var r in reactions)
        {
            if (string.IsNullOrEmpty(r.Kind)) continue;
            if (best is null || (r.Count ?? 0) > (best.Count ?? 0)) best = r;
        }

        if (best is null) return null;
        if (!string.IsNullOrEmpty(best.Emoji)) return best.Emoji;
        if (!string.IsNullOrEmpty(best.CustomEmojiId)) return CustomTokenPrefix + best.CustomEmojiId;
        return best.Kind;
    }

    /// <summary>added / removed / changed, or updated when there is no evidence either way.</summary>
    public static string ChangeOf(IReadOnlyCollection<Reaction>? old, IReadOnlyCollection<Reaction>? current)
    {
        var hadAny = old is { Count: > 0 };
        var hasAny = current is { Count: > 0 };

        if (!hadAny && hasAny) return ChangeAdded;
        if (hadAny && !hasAny) return ChangeRemoved;
        if (hadAny && hasAny) return SameSet(old!, current!) ? ChangeUpdated : ChangeChanged;
        return ChangeUpdated;
    }

    /// <summary>What the actor took away: present before, absent after.</summary>
    public static IReadOnlyList<Reaction> Removed(
        IReadOnlyCollection<Reaction>? old, IReadOnlyCollection<Reaction>? current)
    {
        if (old is null || old.Count == 0) return Array.Empty<Reaction>();
        if (current is null || current.Count == 0) return old.ToList();
        return old.Where(o => !current.Any(c => Same(o, c))).ToList();
    }

    private static Reaction? ToCanonical(ReactionType? reaction, int? count) => reaction switch
    {
        null => null,
        ReactionTypeEmoji emoji => new Reaction(KindEmoji, Emoji: emoji.Emoji, Count: count),
        ReactionTypeCustomEmoji custom =>
            new Reaction(KindCustomEmoji, CustomEmojiId: custom.CustomEmojiId, Count: count),
        ReactionTypePaid => new Reaction(KindPaid, Count: count),
        // A reaction kind added after this SDK build still records THAT it happened, rather than vanishing,
        // so a Telegram addition does not need an SDK release to stop losing data.
        _ => new Reaction(ToKind(reaction.GetType().Name), Count: count),
    };

    private static string ToKind(string clrTypeName)
    {
        const string prefix = "ReactionType";
        var bare = clrTypeName.StartsWith(prefix, StringComparison.Ordinal)
            ? clrTypeName[prefix.Length..]
            : clrTypeName;

        return bare.ToLowerInvariant();
    }

    // Identity ignores Count: the same reaction with a different total is the same reaction. Comparing
    // counts would report every aggregate tick as a "change".
    private static bool Same(Reaction a, Reaction b)
        => string.Equals(a.Kind, b.Kind, StringComparison.Ordinal)
           && string.Equals(a.Emoji, b.Emoji, StringComparison.Ordinal)
           && string.Equals(a.CustomEmojiId, b.CustomEmojiId, StringComparison.Ordinal);

    private static bool SameSet(IReadOnlyCollection<Reaction> a, IReadOnlyCollection<Reaction> b)
        => a.Count == b.Count && a.All(x => b.Any(y => Same(x, y)));
}
