using System;
using System.IO;
using CodexAutoContinue.Updating;

namespace CodexAutoContinue.Tests
{
    public static class UpdateInstallerTests
    {
        public static void Run()
        {
            var root = Path.Combine(Path.GetTempPath(), "codex-update-test-" + Guid.NewGuid().ToString("N"));
            var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
            var target = Path.Combine(root, "CodexAutoContinue.exe");
            var payload = Path.Combine(stage, "payload.exe");
            var binary = typeof(UpdateVersion).Assembly.Location;
            try
            {
                File.Copy(binary, payload); File.WriteAllText(target, "original-installation");
                var manifest = new UpdateManifest { TargetPath = target, ExpectedVersion = UpdateVersion.Current, Sha256 = UpdateFiles.Sha256(payload) };
                UpdateFiles.ValidatePayload(payload, manifest.ExpectedVersion, manifest.Sha256);
                var result = UpdateInstaller.Apply(manifest, stage, () => false);
                Check(!result && File.ReadAllText(target) == "original-installation", "failed startup restores old executable");
                Check(File.Exists(payload), "payload retained for diagnostics");
                result = UpdateInstaller.Apply(manifest, stage, () => true);
                Check(result && UpdateFiles.Sha256(target) == manifest.Sha256, "verified update replaces only executable");
                Check(File.ReadAllText(target + ".previous") == "original-installation", "previous executable retained");
                File.WriteAllText(Path.Combine(root, "settings.json"), "persistent-settings");
                File.WriteAllText(Path.Combine(root, "monitor.db"), "pending-incident");
                manifest.Sha256 = new string('0', 64);
                try { UpdateInstaller.Apply(manifest, stage, () => true); throw new Exception("bad hash accepted"); } catch (InvalidDataException) { }
                Check(File.ReadAllText(Path.Combine(root, "settings.json")) == "persistent-settings" && File.ReadAllText(Path.Combine(root, "monitor.db")) == "pending-incident", "application data is untouched");
                manifest.Sha256 = UpdateFiles.Sha256(payload);
                manifest.ExpectedVersion = "99.0.0";
                try { UpdateFiles.ValidatePayload(payload, manifest.ExpectedVersion, manifest.Sha256); throw new Exception("wrong version accepted"); } catch (InvalidDataException) { }
            }
            finally { Directory.Delete(root, true); }
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
