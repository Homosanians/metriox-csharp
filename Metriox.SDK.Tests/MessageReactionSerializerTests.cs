using Metriox.SDK.Telegram.Mappers;
using System.Text.Json;
using Telegram.Bot.Types;
using Xunit;

namespace Metriox.SDK.Tests;

/// <summary>
/// Reaction props exist to answer "which reaction, and what changed?". This SDK previously answered with a
/// hardcoded <c>tg.reaction_change = "updated"</c> and a count, discarding <c>OldReaction</c> entirely, so
/// the answer was always "something, somehow".
///
/// <para>The wire shape and vocabulary are duplicated from the Metriox backend on purpose — an SDK cannot
/// reference backend code — so these tests pin the exact literals. Metriox's own
/// <c>ReactionCanonicalizationTests</c> pins the same ones from the other side; if either drifts, the two
/// producers silently stop agreeing about the same reaction.</para>
/// </summary>
public class MessageReactionSerializerTests
{
    private static ReactionType Emoji(string e) => new ReactionTypeEmoji { Emoji = e };
    private static ReactionType Custom(string id) => new ReactionTypeCustomEmoji { CustomEmojiId = id };

    // ---- Change direction ----

    [Fact]
    public void First_reaction_is_added()
        => Assert.Equal("added", MessageReactionSerializer.ChangeOf(
            MessageReactionSerializer.Map(Array.Empty<ReactionType>()),
            MessageReactionSerializer.Map(new[] { Emoji("👍") })));

    [Fact]
    public void Clearing_the_only_reaction_is_removed()
        => Assert.Equal("removed", MessageReactionSerializer.ChangeOf(
            MessageReactionSerializer.Map(new[] { Emoji("👍") }),
            MessageReactionSerializer.Map(Array.Empty<ReactionType>())));

    [Fact]
    public void Swapping_one_reaction_for_another_is_changed()
        => Assert.Equal("changed", MessageReactionSerializer.ChangeOf(
            MessageReactionSerializer.Map(new[] { Emoji("👍") }),
            MessageReactionSerializer.Map(new[] { Emoji("🔥") })));

    [Fact]
    public void An_identical_set_is_not_reported_as_a_change()
        => Assert.Equal("updated", MessageReactionSerializer.ChangeOf(
            MessageReactionSerializer.Map(new[] { Emoji("👍") }),
            MessageReactionSerializer.Map(new[] { Emoji("👍") })));

    [Fact]
    public void No_evidence_either_way_reports_updated_rather_than_guessing()
        => Assert.Equal("updated", MessageReactionSerializer.ChangeOf(null, null));

    // ---- Removed set ----

    [Fact]
    public void Removed_is_what_was_present_before_and_absent_after()
    {
        var removed = MessageReactionSerializer.Removed(
            MessageReactionSerializer.Map(new[] { Emoji("👍"), Emoji("🔥") }),
            MessageReactionSerializer.Map(new[] { Emoji("🔥") }));

        Assert.Equal("👍", Assert.Single(removed).Emoji);
    }

    // ---- The flat scalar ----

    [Fact]
    public void Primary_token_is_the_emoji_itself()
        => Assert.Equal("👍", MessageReactionSerializer.PrimaryToken(
            MessageReactionSerializer.Map(new[] { Emoji("👍") })));

    [Fact]
    public void Primary_token_for_a_custom_emoji_is_namespaced_not_null()
    {
        // Returning null here would make grouping by tg.reaction_emoji silently omit every custom
        // reaction, so the field would be unreliable exactly where it is most used.
        Assert.Equal("custom:5361675167", MessageReactionSerializer.PrimaryToken(
            MessageReactionSerializer.Map(new[] { Custom("5361675167") })));
    }

    [Fact]
    public void Primary_token_of_nothing_is_null()
        => Assert.Null(MessageReactionSerializer.PrimaryToken(
            MessageReactionSerializer.Map(Array.Empty<ReactionType>())));

    // ---- Wire shape ----

    [Fact]
    public void An_empty_list_serializes_to_null_so_no_empty_array_prop_is_sent()
        => Assert.Null(MessageReactionSerializer.Serialize(
            MessageReactionSerializer.Map(Array.Empty<ReactionType>())));

    /// <summary>
    /// The exact bytes the backend expects: single-letter keys, order kind → emoji → custom id → count,
    /// and non-ASCII escaped by <c>Utf8JsonWriter</c>'s default encoder. Pinned literally because a
    /// mismatch is a silent cross-producer divergence, not an error anyone would see — and because neither
    /// a round-trip test nor a parity test can detect a change of encoder (both sides would move together).
    /// </summary>
    [Fact]
    public void Wire_shape_matches_the_backend_codec_byte_for_byte()
    {
        var json = MessageReactionSerializer.Serialize(
            MessageReactionSerializer.Map(new[] { Emoji("👍"), Custom("5361675167") }));

        // Key names, key ORDER and structure, with the emoji's own bytes factored out: the encoder escapes
        // non-ASCII, and hardcoding the surrogate hex here would pin the escape's letter case rather than
        // anything meaningful. What must not drift is the shape.
        Assert.Equal(
            $$"""[{"t":"emoji","e":{{JsonSerializer.Serialize("👍")}}},{"t":"custom_emoji","i":"5361675167"}]""",
            json);
    }

    /// <summary>
    /// Pins that the encoder escapes rather than emitting raw UTF-8. Both are valid JSON and both decode
    /// identically, so only a test can hold the choice steady across producers — and it must, because the
    /// stored strings have to match byte for byte between this SDK and the MTProto worker.
    /// </summary>
    [Fact]
    public void Non_ascii_is_escaped_not_written_raw()
    {
        var json = MessageReactionSerializer.Serialize(
            MessageReactionSerializer.Map(new[] { Emoji("👍") }))!;

        Assert.DoesNotContain("👍", json);
        Assert.Contains(@"\u", json);
    }

    [Fact]
    public void Aggregate_totals_carry_each_reactions_user_count()
    {
        var json = MessageReactionSerializer.Serialize(MessageReactionSerializer.Map(new[]
        {
            new ReactionCount { Type = Emoji("🔥"), TotalCount = 11 },
        }));

        Assert.Equal(
            $$"""[{"t":"emoji","e":{{JsonSerializer.Serialize("🔥")}},"c":11}]""",
            json);
    }

    /// <summary>
    /// The escaping above is a storage detail, not a semantic one: what a consumer parses back must be the
    /// emoji itself. Without this, the byte-exact test alone could be satisfied by output that never
    /// decodes correctly.
    /// </summary>
    [Fact]
    public void The_stored_form_parses_back_to_the_original_emoji()
    {
        var json = MessageReactionSerializer.Serialize(
            MessageReactionSerializer.Map(new[] { Emoji("👍") }))!;

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("👍", doc.RootElement[0].GetProperty("e").GetString());
    }

    [Fact]
    public void Primary_token_picks_the_dominant_reaction_when_counts_are_present()
    {
        var totals = MessageReactionSerializer.Map(new[]
        {
            new ReactionCount { Type = Emoji("👍"), TotalCount = 2 },
            new ReactionCount { Type = Emoji("🔥"), TotalCount = 11 },
        });

        Assert.Equal("🔥", MessageReactionSerializer.PrimaryToken(totals));
    }

    [Fact]
    public void A_differing_count_on_the_same_reaction_is_not_a_change()
    {
        // Aggregate snapshots tick counts constantly; treating that as a change would make the field
        // meaningless on the very update type that carries counts.
        var before = MessageReactionSerializer.Map(new[] { new ReactionCount { Type = Emoji("👍"), TotalCount = 3 } });
        var after = MessageReactionSerializer.Map(new[] { new ReactionCount { Type = Emoji("👍"), TotalCount = 9 } });

        Assert.Equal("updated", MessageReactionSerializer.ChangeOf(before, after));
    }

    [Fact]
    public void Serialization_is_capped_so_a_malformed_update_cannot_grow_a_row_without_bound()
    {
        var many = Enumerable.Range(0, MessageReactionSerializer.MaxReactions + 20)
            .Select(i => Emoji($"e{i}"))
            .ToArray();

        var json = MessageReactionSerializer.Serialize(MessageReactionSerializer.Map(many))!;

        Assert.Equal(MessageReactionSerializer.MaxReactions, json.Split("{\"t\":").Length - 1);
    }

    // ---- Vocabulary pinned to the backend's ----

    [Theory]
    [InlineData("emoji")]
    [InlineData("custom_emoji")]
    [InlineData("paid")]
    public void Kind_vocabulary_is_the_shared_one(string kind)
        => Assert.Contains(kind, new[]
        {
            MessageReactionSerializer.KindEmoji,
            MessageReactionSerializer.KindCustomEmoji,
            MessageReactionSerializer.KindPaid,
        });

    [Theory]
    [InlineData("added")]
    [InlineData("removed")]
    [InlineData("changed")]
    [InlineData("updated")]
    public void Change_vocabulary_is_the_shared_one(string change)
        => Assert.Contains(change, new[]
        {
            MessageReactionSerializer.ChangeAdded,
            MessageReactionSerializer.ChangeRemoved,
            MessageReactionSerializer.ChangeChanged,
            MessageReactionSerializer.ChangeUpdated,
        });

    [Fact]
    public void The_reaction_cap_matches_the_backend()
        => Assert.Equal(32, MessageReactionSerializer.MaxReactions);
}
