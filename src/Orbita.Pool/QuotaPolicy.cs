using Orbita.Core;

namespace Orbita.Pool;

public sealed class QuotaPolicy : IQuotaPolicy
{
    public bool IsReady(QuotaAccount account, bool gemini, int reserve, DateTimeOffset now)
    {
        var at = now.ToUnixTimeMilliseconds();
        if (account.BlockedUntil > at || (gemini ? account.GeminiBlockedUntil : account.ClaudeBlockedUntil) > at) return false;
        var family = gemini ? account.Gemini : account.Claude;
        return Available(family.FiveHour, reserve, now) && Available(family.Weekly, reserve, now);
    }

    private static bool Available(QuotaWindow? window, int reserve, DateTimeOffset now) => window == null ||
        (window.ResetTime is { } reset && reset <= now) || window.Percent > reserve;
}
