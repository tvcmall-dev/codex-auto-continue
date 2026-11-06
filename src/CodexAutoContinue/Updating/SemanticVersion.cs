using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexAutoContinue.Updating
{
    public sealed class SemanticVersion : IComparable<SemanticVersion>
    {
        private static readonly Regex Pattern = new Regex(@"\A[vV]?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z", RegexOptions.CultureInvariant);
        public int Major { get; private set; }
        public int Minor { get; private set; }
        public int Patch { get; private set; }
        public string Prerelease { get; private set; }
        public string BuildMetadata { get; private set; }
        public bool IsPrerelease { get { return Prerelease.Length > 0; } }
        private SemanticVersion() { }
        public static bool TryParse(string value, out SemanticVersion version)
        {
            version = null;
            if (String.IsNullOrEmpty(value) || value.Length > 200) return false;
            var match = Pattern.Match(value);
            int major, minor, patch;
            if (!match.Success || !Int32.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out major) ||
                !Int32.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out minor) ||
                !Int32.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out patch)) return false;
            var prerelease = match.Groups[4].Value;
            if (prerelease.Split('.').Any(part => part.Length > 1 && part[0] == '0' && part.All(IsDigit))) return false;
            version = new SemanticVersion { Major = major, Minor = minor, Patch = patch, Prerelease = prerelease, BuildMetadata = match.Groups[5].Value };
            return true;
        }
        public static SemanticVersion Parse(string value)
        {
            SemanticVersion version;
            if (!TryParse(value, out version)) throw new FormatException("版本号格式无效。");
            return version;
        }
        public int CompareTo(SemanticVersion other)
        {
            if (other == null) return 1;
            var result = Major.CompareTo(other.Major);
            if (result == 0) result = Minor.CompareTo(other.Minor);
            if (result == 0) result = Patch.CompareTo(other.Patch);
            if (result != 0) return result;
            if (!IsPrerelease || !other.IsPrerelease) return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
            var left = Prerelease.Split('.'); var right = other.Prerelease.Split('.');
            for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
            {
                var leftNumber = left[i].All(IsDigit); var rightNumber = right[i].All(IsDigit);
                if (leftNumber && rightNumber)
                {
                    result = left[i].Length.CompareTo(right[i].Length);
                    if (result == 0) result = String.CompareOrdinal(left[i], right[i]);
                }
                else result = leftNumber == rightNumber ? String.CompareOrdinal(left[i], right[i]) : leftNumber ? -1 : 1;
                if (result != 0) return result;
            }
            return left.Length.CompareTo(right.Length);
        }
        public override string ToString()
        {
            return Major.ToString(CultureInfo.InvariantCulture) + "." + Minor.ToString(CultureInfo.InvariantCulture) + "." + Patch.ToString(CultureInfo.InvariantCulture) +
                (IsPrerelease ? "-" + Prerelease : "") + (BuildMetadata.Length > 0 ? "+" + BuildMetadata : "");
        }
        private static bool IsDigit(char value) { return value >= '0' && value <= '9'; }
    }
}
