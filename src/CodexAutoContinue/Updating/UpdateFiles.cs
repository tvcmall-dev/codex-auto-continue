using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace CodexAutoContinue.Updating
{
    public static class UpdateFiles
    {
        public const long MaximumSize = 30L * 1024 * 1024;
        public static string Sha256(string path)
        {
            using (var file = File.OpenRead(path))
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }
        public static void ValidatePayload(string path, string version, string expectedHash)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < 512 || file.Length > MaximumSize || string.IsNullOrWhiteSpace(expectedHash) || expectedHash.Length != 64 || !string.Equals(Sha256(path), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("升级文件校验失败，原程序未替换。");
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                if (reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("升级文件不是 Windows EXE。");
                reader.BaseStream.Position = 0x3c;
                var offset = reader.ReadInt32();
                if (offset < 64 || offset > file.Length - 24) throw new InvalidDataException("升级文件格式无效。");
                reader.BaseStream.Position = offset;
                if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664) throw new InvalidDataException("升级文件不是受支持的 Windows x64 程序。");
            }
            SemanticVersion expected, actual;
            var information = FileVersionInfo.GetVersionInfo(path);
            if (!SemanticVersion.TryParse(version, out expected) || !SemanticVersion.TryParse(information.ProductVersion, out actual) || actual.CompareTo(expected) != 0 || information.InternalName != "CodexAutoContinue.exe")
                throw new InvalidDataException("升级文件的应用标识或版本与发布信息不符。");
        }
    }
}
