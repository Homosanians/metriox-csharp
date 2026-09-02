using Metriox.SDK.Telegram;
using Metriox.SDK.Telegram.Mappers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace Metriox.SDK.Tests;

/// <summary>
/// Two fixes whose absence was invisible in the data.
///
/// <para>Reactions were losing rows outright: <c>message_reaction</c> is a PER-REACTOR update, but it was
/// keyed per message, so every reactor on one message minted the same deterministic event_id and the
/// ReplacingMergeTree kept exactly one. Nothing failed, nothing logged — a post with fifty reactions
/// simply reported one, and no query could tell that anyone was missing.</para>
///
/// <para>Poll votes were being double-billed for a bot connected both by token and through this SDK: the
/// two producers already agreed on the key, but the server derives it from <c>$tg</c> and this side never
/// sent the voter's chat.</para>
/// </summary>
public class ReactionAndPollIdentityTests
{
    private static MessageReactionUpdated Reaction(long userId, long chatId = -1001234567890, int messageId = 42)
        => new()
        {
            Chat = new Chat { Id = chatId, Type = ChatType.Supergroup },
            MessageId = messageId,
            User = new User { Id = userId, FirstName = "R" },
            NewReaction = [],
            OldReaction = [],
        };

    [Fact]
    public void TwoReactorsOnOneMessage_GetDistinctIds()
    {
        // THE data-loss regression. If these ever collide again, one of the two reactions is discarded
        // downstream and there is no signal anywhere that it happened.
        var first = TelegramEventIdentity.ForUpdate(new Update { MessageReaction = Reaction(userId: 1001) });
        var second = TelegramEventIdentity.ForUpdate(new Update { MessageReaction = Reaction(userId: 1002) });

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void AReactorsIdentity_IsStableForTheSamePerson()
    {
        // Distinctness must not be achieved with anything volatile: the same person reacting to the same
        // message has to mint the same id on a retry, or retries duplicate instead of deduplicating.
        var once = TelegramEventIdentity.ForUpdate(new Update { MessageReaction = Reaction(userId: 1001) });
        var twice = TelegramEventIdentity.ForUpdate(new Update { MessageReaction = Reaction(userId: 1001) });

        Assert.Equal(once, twice);
    }

    [Fact]
    public void AReactorsKey_UsesThePerReactorFamily_NotThePerMessageOne()
    {
        // Pinned as a literal because it is a CROSS-REPO contract: the MTProto worker mints
        // tg:botreact:{chat}:{message}:{reactor} for the same event. A rename on either side ends dedup
        // silently, so the string itself is the assertion.
        var key = TelegramEventIdentity.ForUpdate(new Update { MessageReaction = Reaction(userId: 1001) });

        Assert.Equal("tg:botreact:-1001234567890:42:1001", key);
    }

    [Fact]
    public void AnAnonymousChannelReaction_KeysOnTheActingChat()
    {
        // An anonymous reaction reports ActorChat instead of User. It still identifies the acting party,
        // so it is a usable coordinate rather than a reason to fall back.
        var update = new Update
        {
            MessageReaction = new MessageReactionUpdated
            {
                Chat = new Chat { Id = -100999, Type = ChatType.Channel },
                MessageId = 7,
                ActorChat = new Chat { Id = -100555, Type = ChatType.Channel },
                NewReaction = [],
                OldReaction = [],
            },
        };

        Assert.Equal("tg:botreact:-100999:7:-100555", TelegramEventIdentity.ForUpdate(update));
    }

    [Fact]
    public void AReactionWithNoActor_FallsBackRatherThanSharingAKey()
    {
        // With neither User nor ActorChat there is no reactor to key on. Returning null hands the caller
        // its update-id fallback, which is producer-scoped and therefore honest; inventing a shared key
        // would resurrect the collapse this class exists to prevent.
        var update = new Update
        {
            MessageReaction = new MessageReactionUpdated
            {
                Chat = new Chat { Id = -100999, Type = ChatType.Channel },
                MessageId = 7,
                NewReaction = [],
                OldReaction = [],
            },
        };

        Assert.Null(TelegramEventIdentity.ForUpdate(update));
    }

    [Fact]
    public void APollAnswer_EmitsTheVotersChat()
    {
        var mapped = new TelegramUpdateToBotEventMapper("bot").ToBotEvent(new Update
        {
            PollAnswer = new PollAnswer
            {
                PollId = "poll-1",
                User = new User { Id = 4242, FirstName = "V" },
                OptionIds = [0],
            },
        });

        Assert.NotNull(mapped);
        Assert.Equal(4242, mapped!.PropsLong!["tg.chat_id"]);
    }

    [Fact]
    public void AnAnonymousPollAnswer_PrefersVoterChatOverUser()
    {
        // VoterChat is not merely a fallback: for an anonymous channel vote it is the coordinate the
        // worker uses, so preferring it is what makes the two producers agree rather than both simply
        // emitting something.
        var mapped = new TelegramUpdateToBotEventMapper("bot").ToBotEvent(new Update
        {
            PollAnswer = new PollAnswer
            {
                PollId = "poll-2",
                VoterChat = new Chat { Id = -100777, Type = ChatType.Channel },
                OptionIds = [1],
            },
        });

        Assert.NotNull(mapped);
        Assert.Equal(-100777, mapped!.PropsLong!["tg.chat_id"]);
    }
}
