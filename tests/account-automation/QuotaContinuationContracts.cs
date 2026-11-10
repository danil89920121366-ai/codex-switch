using CodexAccountDesk;

internal static class QuotaContinuationContracts
{
    internal static void Run(Action<string, bool> check)
    {
        var chat = new SwitchChat { Id = "chat", OriginalTurnId = "quota-turn", RestartAfterQuotaFailure = true };
        var thread = new DesktopThreadState("chat", "owner", "idle", false, null, "Test",
            "resumed", "quota-turn", "failed", null);
        check("An idle chat may resume a verified quota failure", SwitchContinuation.CanResumeQuotaFailure(thread) &&
            SwitchContinuation.Decide(chat, thread) == null);
        check("Paused, blocked and waiting chats are excluded", !SwitchContinuation.CanResumeQuotaFailure(thread with { Waiting = true }) &&
            !SwitchContinuation.CanResumeQuotaFailure(thread with { Goal = "paused" }) &&
            !SwitchContinuation.CanResumeQuotaFailure(thread with { Goal = "blocked" }));
        check("An active or newer turn prevents a duplicate Continue",
            SwitchContinuation.Decide(chat, thread with { Status = "active" }) == ChatResumeState.AlreadyRunning &&
            SwitchContinuation.Decide(chat, thread with { LastTurnId = "newer" }) == ChatResumeState.Completed);
        var restored = System.Text.Json.JsonSerializer.Deserialize<SwitchChat>(System.Text.Json.JsonSerializer.Serialize(chat))!;
        check("Quota continuation identity survives recovery", restored.RestartAfterQuotaFailure &&
            restored.ContinueMessageId == chat.ContinueMessageId);
        chat.ResumeState = ChatResumeState.Unconfirmed;
        check("A quota continuation with unknown delivery cannot be resent",
            SwitchContinuation.Decide(chat, thread) == ChatResumeState.Unconfirmed);
    }
}
