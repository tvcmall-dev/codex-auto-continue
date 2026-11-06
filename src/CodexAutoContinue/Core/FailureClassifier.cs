using System;
using System.Text.RegularExpressions;

namespace CodexAutoContinue.Core
{
    public static class FailureClassifier
    {
        public static SessionState Classify(string code, string message, DateTime? retryAtUtc)
        {
            var normalized = (code ?? "").ToLowerInvariant();
            var details = (message ?? "").ToLowerInvariant();
            var combined = normalized + " " + details;
            if (Has(combined, "unauthorized", "authentication", "invalid_api_key", "insufficient_quota", "billing",
                "credit", "payment", "context_length", "contextwindow", "modelnotfound", "cyberpolicy", "badrequest") ||
                Regex.IsMatch(combined, @"(?<!\d)4(?!29)\d{2}(?!\d)")) return SessionState.Failed;

            if (Has(normalized, "usagelimit", "ratelimit", "toomanyfailedattempts") ||
                Regex.IsMatch(combined, @"(?<!\d)429(?!\d)"))
                return retryAtUtc.HasValue ? SessionState.QuotaWait : SessionState.Failed;

            if (Has(normalized, "network", "serveroverloaded", "streamdisconnected", "responsestreamdisconnected",
                "httpconnectionfailed", "connectionreset", "connectionrefused", "timeout", "temporarilyunavailable"))
                return SessionState.NetworkFailure;
            return SessionState.Failed;
        }

        private static bool Has(string value, params string[] needles)
        {
            foreach (var needle in needles) if (value.IndexOf(needle, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
