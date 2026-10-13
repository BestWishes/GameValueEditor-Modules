using System.IO;
using System.Text.Json;
using GameValueEditor.Modules.PlayAgainExpedition;

// Isolated protocol/session-file regression. No game, screen or network access.
internal static class OutcomeRegressionTests
{
    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "gve-expedition-outcomes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
        void Throws(Action action, string name) { try { action(); } catch (InvalidOperationException) { checks++; return; } throw new Exception(name); }
        JsonElement Receipt(string name) => JsonSerializer.SerializeToElement(new { protocol = "gve-mutation-outcome-v1", outcome = "not-executed", requestName = name, reason = "READ_RECEIPT_SCOPE_CHANGED" });
        string Phase(string key) { using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, key + ".json"))); return doc.RootElement.GetProperty("phase").GetString()!; }
        try
        {
            foreach (var request in new[] { "materials-set", "wheel-configure", "wheel-reset", "drops-configure", "drops-reset", "fate-configure", "fate-reset", "equipment-configure", "equipment-reset" })
            {
                using (var gate = MutationGate.Acquire(root, request))
                {
                    gate.Claim(); Check(Phase(request) == "pending", request + " claimed");
                    try { MutationOutcome.ThrowIfRejected(Receipt(request), request, gate); throw new Exception("rejection reported as success"); }
                    catch (MutationRejectedException error) { Check(error.Message.Contains("本次未执行修改") && !error.Message.Contains("需要正常重启"), request + " rejection message"); }
                    Check(Phase(request) == "rejected", request + " rejected terminal state");
                }
                using var next = MutationGate.Acquire(root, request);
                next.Claim(); next.Confirm(); Check(Phase(request) == "confirmed", request + " fresh modification allowed");
            }
            var malformed = new[]
            {
        "{\"protocol\":\"future\",\"outcome\":\"not-executed\",\"requestName\":\"materials-set\",\"reason\":\"GAME_BUSY\"}",
        "{\"protocol\":\"gve-mutation-outcome-v1\",\"outcome\":\"confirmed\",\"requestName\":\"materials-set\",\"reason\":\"GAME_BUSY\"}",
        "{\"protocol\":\"gve-mutation-outcome-v1\",\"outcome\":\"not-executed\",\"requestName\":\"wheel-reset\",\"reason\":\"GAME_BUSY\"}",
        "{\"protocol\":\"gve-mutation-outcome-v1\",\"outcome\":\"not-executed\",\"requestName\":\"materials-set\",\"reason\":\"private/path\"}",
        "{\"protocol\":\"gve-mutation-outcome-v1\",\"outcome\":\"not-executed\",\"requestName\":\"materials-set\",\"reason\":null}",
        "{\"protocol\":\"gve-mutation-outcome-v1\",\"outcome\":\"not-executed\",\"requestName\":\"materials-set\",\"reason\":4}",
        "{\"protocol\":\"gve-mutation-outcome-v1\",\"outcome\":\"not-executed\",\"requestName\":\"materials-set\"}",
        "{\"protocol\":\"gve-mutation-outcome-v1\",\"outcome\":\"not-executed\",\"requestName\":\"materials-set\",\"reason\":\"GAME_BUSY\",\"extra\":true}",
        "{\"protocol\":\"gve-mutation-outcome-v1\",\"protocol\":\"gve-mutation-outcome-v1\",\"outcome\":\"not-executed\",\"requestName\":\"materials-set\",\"reason\":\"GAME_BUSY\"}"
    };
            for (var i = 0; i < malformed.Length; i++)
            {
                var key = "invalid-" + i;
                using (var gate = MutationGate.Acquire(root, key))
                {
                    gate.Claim(); using var doc = JsonDocument.Parse(malformed[i]);
                    try { MutationOutcome.ThrowIfRejected(doc.RootElement, "materials-set", gate); throw new Exception("malformed accepted"); }
                    catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException)
                    { Check(error is not MutationRejectedException, "malformed is not a definite rejection"); gate.Uncertain(); }
                    Check(Phase(key) == "uncertain", "malformed persisted uncertain");
                }
                Throws(() => { using var forbidden = MutationGate.Acquire(root, key); }, "uncertain unlocked");
            }
            foreach (var phase in new[] { "pending", "uncertain", "unknown" })
            {
                var key = "old-" + phase;
                File.WriteAllText(Path.Combine(root, key + ".json"), JsonSerializer.Serialize(new { phase }));
                Throws(() => { using var forbidden = MutationGate.Acquire(root, key); }, "old state unlocked");
                Check(Phase(key) == phase, "old state preserved");
            }
            using (var gate = MutationGate.Acquire(root, "plain-error"))
            {
                gate.Claim(); var ordinary = JsonSerializer.SerializeToElement(new { error = "READ_RECEIPT_SCOPE_CHANGED" });
                MutationOutcome.ThrowIfRejected(ordinary, "materials-set", gate);
                Check(Phase("plain-error") == "pending", "ordinary text cannot unlock"); gate.Uncertain();
            }
            Throws(() => { using var forbidden = MutationGate.Acquire(root, "plain-error"); }, "plain error unlocked");
            Throws(() => MutationOutcome.ThrowIfRejected(Receipt("materials-set"), "materials-set", null), "readonly accepted mutation receipt");
            using (var gate = MutationGate.Acquire(root, "exclusive"))
            {
                try { using var concurrent = MutationGate.Acquire(root, "exclusive"); throw new Exception("concurrent writer allowed"); }
                catch (IOException) { checks++; }
            }
            Throws(() => { using var invalid = MutationGate.Acquire(root, "../outside"); }, "invalid session key accepted");
            Console.WriteLine($"Expedition outcome/session regression: {checks} checks passed; no live game or network operations.");
        }
        finally
        {
            // Only this test-created GUID directory, never the real ModuleSessions root.
            Directory.Delete(root, recursive: true);
        }
        return checks;
    }
}
