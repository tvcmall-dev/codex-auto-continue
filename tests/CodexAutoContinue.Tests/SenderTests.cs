using System;
using CodexAutoContinue.Integration;

namespace CodexAutoContinue.Tests
{
    public static class SenderTests
    {
        public static void Run()
        {
            Check(CliSender.Quote("abc") == "\"abc\"", "simple quote");
            Check(CliSender.Quote("C:\\path\\") == "\"C:\\path\\\\\"", "trailing slash quote");
            Check(CliSender.Quote("a\"b") == "\"a\\\"b\"", "embedded quote");
            Check(CliSender.BuildArguments("00000000-0000-0000-0000-000000000001", "继续 \"验证\" & echo private").Contains("--resume"), "structured direct process arguments");
            try { CliSender.BuildArguments("--evil", "继续"); throw new Exception("bad ID accepted"); } catch (ArgumentException) { }
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
