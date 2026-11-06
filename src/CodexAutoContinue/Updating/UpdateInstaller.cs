using System;
using System.IO;

namespace CodexAutoContinue.Updating
{
    public static class UpdateInstaller
    {
        public static bool Apply(UpdateManifest manifest, string stageDirectory, Func<bool> startAndVerify)
        {
            var target = Path.GetFullPath(manifest.TargetPath);
            if (!string.Equals(Path.GetFileName(target), "CodexAutoContinue.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
                throw new InvalidDataException("升级目标无效，请使用原文件名 CodexAutoContinue.exe。");
            var payload = Path.Combine(stageDirectory, "payload.exe");
            UpdateFiles.ValidatePayload(payload, manifest.ExpectedVersion, manifest.Sha256);
            var replacement = target + ".update-" + Guid.NewGuid().ToString("N");
            var backup = target + ".previous";
            try
            {
                File.Copy(payload, replacement);
                File.Replace(replacement, target, backup, true);
                try { if (startAndVerify()) return true; }
                catch { }
                // Only the EXE is rolled back; user data is never part of the replacement.
                File.Replace(backup, target, null, true);
                return false;
            }
            finally { if (File.Exists(replacement)) File.Delete(replacement); }
        }
    }
}
