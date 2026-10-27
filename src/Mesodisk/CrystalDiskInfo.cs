// Mesodisk - CrystalDiskInfo integration.
// CrystalDiskInfo (C) 2008-2026 hiyohiyo, MIT License - https://github.com/hiyohiyo/CrystalDiskInfo
// Mesodisk runs the unmodified portable DiskInfo64.exe with /CopyExit, which writes DiskInfo.txt
// next to the executable, then parses that report.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Mesodisk
{
    internal sealed class SmartAttribute
    {
        public string Id = "";
        public string Name = "";
        public string Current = "";
        public string Worst = "";
        public string Threshold = "";
        public string RawHex = "";
        public ulong? RawDecimal;
        public AttributeLevel Level = AttributeLevel.Normal;
    }

    internal enum AttributeLevel
    {
        Normal,
        Caution,
        Bad
    }

    internal sealed class CdiDisk
    {
        public int Index;
        public int PhysicalDriveId = -1;
        public string Title = "";
        public readonly List<KeyValuePair<string, string>> Fields = new List<KeyValuePair<string, string>>();
        public readonly List<SmartAttribute> Attributes = new List<SmartAttribute>();
        public string SmartColumns = "";
        public string RawSection = "";
        public bool IsNvme;

        public string Field(string name)
        {
            foreach (KeyValuePair<string, string> pair in Fields)
            {
                if (String.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }

            return "";
        }

        // "Good", "Caution", "Bad" or "Unknown"
        public string HealthStatus
        {
            get
            {
                Match match = Regex.Match(Field("Health Status"), @"^\s*([A-Za-z]+)");
                return match.Success ? match.Groups[1].Value : "Unknown";
            }
        }

        public int? HealthPercent
        {
            get
            {
                Match match = Regex.Match(Field("Health Status"), @"\((\d+)\s*%\)");
                if (match.Success)
                {
                    return Int32.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                }

                return null;
            }
        }
    }

    internal sealed class CdiReport
    {
        public string Header = "";
        public string RawText = "";
        public readonly List<CdiDisk> Disks = new List<CdiDisk>();

        public CdiDisk FindDisk(DiskInfo disk, string driveLetter)
        {
            if (disk == null)
            {
                return null;
            }

            foreach (CdiDisk candidate in Disks)
            {
                if (candidate.PhysicalDriveId == disk.Number)
                {
                    return candidate;
                }
            }

            if (!String.IsNullOrEmpty(driveLetter))
            {
                foreach (CdiDisk candidate in Disks)
                {
                    string letters = candidate.Field("Drive Letter");
                    if (letters.IndexOf(driveLetter, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return candidate;
                    }
                }
            }

            if (!String.IsNullOrWhiteSpace(disk.SerialNumber))
            {
                foreach (CdiDisk candidate in Disks)
                {
                    if (String.Equals(candidate.Field("Serial Number").Trim(), disk.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }
    }

    internal static class CdiParser
    {
        private static readonly Regex DashLine = new Regex(@"^-{60,}\s*$");
        private static readonly Regex DiskTitle = new Regex(@"^ \((\d{2})\) (.*)$");
        private static readonly Regex DiskListLine = new Regex(@"^ \((\d{2})\) .*? : [\d.]+ [GMT]B \[(\d+|X)/");
        private static readonly Regex FieldLine = new Regex(@"^\s*([A-Za-z#][A-Za-z0-9 #./()]*?) : (.*)$");
        private static readonly Regex SmallValue = new Regex(@"^[_0-9]{1,3}$");
        private static readonly Regex HexRaw = new Regex(@"^[0-9A-Fa-f]{12,16}$");

        public static CdiReport Parse(string text)
        {
            CdiReport report = new CdiReport();
            report.RawText = text ?? "";
            string[] lines = report.RawText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            Dictionary<int, int> physicalIds = new Dictionary<int, int>();
            List<int> sectionStarts = new List<int>();

            for (int i = 0; i < lines.Length; i++)
            {
                Match list = DiskListLine.Match(lines[i]);
                if (list.Success && sectionStarts.Count == 0)
                {
                    int index = Int32.Parse(list.Groups[1].Value, CultureInfo.InvariantCulture);
                    int physical;
                    if (Int32.TryParse(list.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out physical))
                    {
                        physicalIds[index] = physical;
                    }
                }

                if (i + 2 < lines.Length && DashLine.IsMatch(lines[i]) && DiskTitle.IsMatch(lines[i + 1]) && DashLine.IsMatch(lines[i + 2]))
                {
                    sectionStarts.Add(i);
                }
            }

            int headerEnd = sectionStarts.Count > 0 ? sectionStarts[0] : lines.Length;
            report.Header = String.Join("\r\n", lines, 0, headerEnd).TrimEnd();

            for (int s = 0; s < sectionStarts.Count; s++)
            {
                int start = sectionStarts[s];
                int end = s + 1 < sectionStarts.Count ? sectionStarts[s + 1] : lines.Length;
                CdiDisk disk = ParseSection(lines, start, end);
                int physical;
                if (physicalIds.TryGetValue(disk.Index, out physical))
                {
                    disk.PhysicalDriveId = physical;
                }

                report.Disks.Add(disk);
            }

            return report;
        }

        private static CdiDisk ParseSection(string[] lines, int start, int end)
        {
            CdiDisk disk = new CdiDisk();
            Match title = DiskTitle.Match(lines[start + 1]);
            disk.Index = Int32.Parse(title.Groups[1].Value, CultureInfo.InvariantCulture);
            disk.Title = title.Groups[2].Value.Trim();

            StringBuilder raw = new StringBuilder();
            int i = start + 3;
            for (int k = start; k < start + 3; k++)
            {
                raw.Append(lines[k]).Append("\r\n");
            }

            // Info fields until the first "-- " block or blank line.
            for (; i < end; i++)
            {
                string line = lines[i];
                if (line.StartsWith("-- ", StringComparison.Ordinal))
                {
                    break;
                }

                raw.Append(line).Append("\r\n");
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                Match field = FieldLine.Match(line);
                if (field.Success)
                {
                    disk.Fields.Add(new KeyValuePair<string, string>(field.Groups[1].Value.Trim(), field.Groups[2].Value.Trim()));
                }
            }

            disk.IsNvme = disk.Field("Interface").IndexOf("NVM", StringComparison.OrdinalIgnoreCase) >= 0;

            // Blocks: S.M.A.R.T., then optional hex dumps.
            for (; i < end; i++)
            {
                string line = lines[i];
                raw.Append(line).Append("\r\n");

                if (!line.StartsWith("-- S.M.A.R.T.", StringComparison.Ordinal))
                {
                    continue;
                }

                if (i + 1 < end)
                {
                    i++;
                    disk.SmartColumns = lines[i].Trim();
                    raw.Append(lines[i]).Append("\r\n");
                    if (disk.SmartColumns.StartsWith("ID RawValues(6)", StringComparison.Ordinal))
                    {
                        disk.IsNvme = true;
                    }
                }

                for (i = i + 1; i < end; i++)
                {
                    string row = lines[i];
                    raw.Append(row).Append("\r\n");
                    if (row.Trim().Length == 0)
                    {
                        break;
                    }

                    SmartAttribute attribute = ParseAttribute(row, disk.IsNvme);
                    if (attribute != null)
                    {
                        disk.Attributes.Add(attribute);
                    }
                }
            }

            disk.RawSection = raw.ToString().TrimEnd();
            return disk;
        }

        internal static SmartAttribute ParseAttribute(string row, bool nvme)
        {
            string[] tokens = row.Split(' ');
            if (tokens.Length < 3 || !Regex.IsMatch(tokens[0], "^[0-9A-Fa-f]{2}$"))
            {
                return null;
            }

            SmartAttribute attribute = new SmartAttribute();
            attribute.Id = tokens[0].ToUpperInvariant();

            List<string> values = new List<string>();
            int t = 1;
            while (t < tokens.Length && values.Count < 3 && !HexRaw.IsMatch(tokens[t]))
            {
                string token = tokens[t];
                if (token.Length == 0 || token == "---" || SmallValue.IsMatch(token))
                {
                    values.Add(token == "---" ? "---" : token.Replace("_", "").Trim());
                    t++;
                    continue;
                }

                break;
            }

            if (t >= tokens.Length || !HexRaw.IsMatch(tokens[t]))
            {
                return null;
            }

            attribute.RawHex = tokens[t].ToUpperInvariant();
            t++;
            attribute.Name = String.Join(" ", tokens, t, tokens.Length - t).Trim();

            if (values.Count > 0) attribute.Current = values[0];
            if (values.Count > 1) attribute.Worst = values[1];
            if (values.Count > 2) attribute.Threshold = values[2];

            ulong rawValue;
            if (UInt64.TryParse(attribute.RawHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rawValue))
            {
                attribute.RawDecimal = rawValue;
            }

            attribute.Level = Classify(attribute, nvme);
            return attribute;
        }

        private static AttributeLevel Classify(SmartAttribute a, bool nvme)
        {
            ulong raw = a.RawDecimal.HasValue ? a.RawDecimal.Value : 0UL;

            if (nvme)
            {
                if (a.Id == "01" && raw != 0)
                {
                    return AttributeLevel.Bad;      // Critical Warning
                }

                if (a.Id == "0E" && raw != 0)
                {
                    return AttributeLevel.Caution;  // Media and Data Integrity Errors
                }

                return AttributeLevel.Normal;
            }

            int current, threshold;
            if (Int32.TryParse(a.Current, NumberStyles.Integer, CultureInfo.InvariantCulture, out current) &&
                Int32.TryParse(a.Threshold, NumberStyles.Integer, CultureInfo.InvariantCulture, out threshold) &&
                threshold > 0 && current > 0 && current <= threshold)
            {
                return AttributeLevel.Bad;
            }

            if (raw != 0 && Regex.IsMatch(a.Name, "Reallocat|Pending|Uncorrectable", RegexOptions.IgnoreCase))
            {
                return AttributeLevel.Caution;
            }

            return AttributeLevel.Normal;
        }
    }

    internal static class CdiRunner
    {
        public const string EmbeddedResourceName = "Mesodisk.CrystalDiskInfo.zip";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WritePrivateProfileString(string section, string key, string value, string fileName);

        private static string cachedDirectory;

        public static string Version = "";

        // Finds a writable CrystalDiskInfo folder: next to Mesodisk.exe, or extracted from the embedded copy.
        private static readonly object resolveLock = new object();

        public static string ResolveDirectory(out string message)
        {
            lock (resolveLock)
            {
                return ResolveDirectoryCore(out message);
            }
        }

        private static string ResolveDirectoryCore(out string message)
        {
            message = "";
            if (!String.IsNullOrEmpty(cachedDirectory) && File.Exists(Path.Combine(cachedDirectory, ExecutableName())))
            {
                return cachedDirectory;
            }

            string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mesodisk");

            try
            {
                string sideBySide = Path.Combine(appDirectory, "CrystalDiskInfo");
                if (File.Exists(Path.Combine(sideBySide, ExecutableName())))
                {
                    if (IsWritable(sideBySide))
                    {
                        cachedDirectory = sideBySide;
                        message = "Using CrystalDiskInfo next to Mesodisk.exe.";
                        return cachedDirectory;
                    }

                    string copyTarget = Path.Combine(localRoot, "CrystalDiskInfo");
                    CopyDirectory(sideBySide, copyTarget);
                    cachedDirectory = copyTarget;
                    message = "Copied CrystalDiskInfo to " + copyTarget + " (app folder is read-only).";
                    return cachedDirectory;
                }

                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream(EmbeddedResourceName))
                {
                    if (stream == null)
                    {
                        message = "CrystalDiskInfo not found (no CrystalDiskInfo folder next to Mesodisk.exe and none embedded).";
                        return "";
                    }

                    string stamp = stream.Length.ToString(CultureInfo.InvariantCulture) + "-" + File.GetLastWriteTimeUtc(assembly.Location).Ticks.ToString(CultureInfo.InvariantCulture);
                    string target = Path.Combine(localRoot, "CrystalDiskInfo");
                    string stampFile = Path.Combine(target, ".mesodisk-stamp");

                    if (!File.Exists(stampFile) || File.ReadAllText(stampFile).Trim() != stamp || !File.Exists(Path.Combine(target, ExecutableName())))
                    {
                        ExtractZip(stream, target);
                        File.WriteAllText(stampFile, stamp);
                        message = "Extracted embedded CrystalDiskInfo to " + target + ".";
                    }
                    else
                    {
                        message = "Using embedded CrystalDiskInfo at " + target + ".";
                    }

                    cachedDirectory = target;
                    return cachedDirectory;
                }
            }
            catch (Exception ex)
            {
                message = "Could not prepare CrystalDiskInfo: " + ex.Message;
                return "";
            }
        }

        public static string ExecutableName()
        {
            string arch = (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "").ToUpperInvariant();
            if (arch == "ARM64")
            {
                return "DiskInfoA64.exe";
            }

            return Environment.Is64BitOperatingSystem ? "DiskInfo64.exe" : "DiskInfo32.exe";
        }

        // Runs CrystalDiskInfo once and returns the parsed report (all disks).
        public static CdiReport Run(string directory, int timeoutSeconds, out string error)
        {
            error = "";
            string exe = Path.Combine(directory, ExecutableName());
            if (!File.Exists(exe))
            {
                error = "CrystalDiskInfo executable missing: " + exe;
                return null;
            }

            try
            {
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(exe);
                Version = version.ProductVersion ?? version.FileVersion ?? "";
            }
            catch
            {
            }

            string ini = Path.Combine(directory, "DiskInfo.ini");
            PrepareIni(ini);

            string txt = Path.Combine(directory, "DiskInfo.txt");
            try
            {
                if (File.Exists(txt))
                {
                    File.Delete(txt);
                }
            }
            catch
            {
            }

            ProcessStartInfo info = new ProcessStartInfo(exe, "/CopyExit");
            info.WorkingDirectory = directory;
            info.UseShellExecute = false;
            info.WindowStyle = ProcessWindowStyle.Minimized;

            using (Process process = Process.Start(info))
            {
                if (!process.WaitForExit(timeoutSeconds * 1000))
                {
                    try { process.Kill(); } catch { }
                    error = "CrystalDiskInfo did not finish within " + timeoutSeconds.ToString(CultureInfo.InvariantCulture) + " seconds.";
                    return null;
                }
            }

            if (!File.Exists(txt))
            {
                error = "CrystalDiskInfo finished but did not write DiskInfo.txt.";
                return null;
            }

            string text = File.ReadAllText(txt, Encoding.UTF8);
            return CdiParser.Parse(text);
        }

        private static void PrepareIni(string ini)
        {
            // Only the keys Mesodisk needs; everything else in DiskInfo.ini (e.g. PowerOnUnit fixes) is preserved.
            Set(ini, "Setting", "Language", "English");
            Set(ini, "Setting", "SmartEnglish", "1");
            Set(ini, "Setting", "HideNoSmartDisk", "0");
            Set(ini, "Setting", "HideSerialNumber", "0");
            Set(ini, "Setting", "DumpIdentifyDevice", "0");
            Set(ini, "Setting", "DumpSmartReadData", "0");
            Set(ini, "Setting", "DumpSmartReadThreshold", "0");
            Set(ini, "Setting", "AutoRefresh", "0");
            Set(ini, "Setting", "Resident", "0");
            Set(ini, "Setting", "AlertSound", "0");
            Set(ini, "Setting", "AlertMail", "0");
            Set(ini, "Setting", "EventLog", "0");
            Set(ini, "Setting", "StartupVoice", "0");
            Set(ini, "USB", "SAT", "1");
            Set(ini, "USB", "UsbMemory", "1");
        }

        private static void Set(string ini, string section, string key, string value)
        {
            try
            {
                WritePrivateProfileString(section, key, "\"" + value + "\"", ini);
            }
            catch
            {
            }
        }

        private static bool IsWritable(string directory)
        {
            try
            {
                string probe = Path.Combine(directory, ".mesodisk-write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(directory.Replace(source, target));
            }

            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string destination = file.Replace(source, target);
                if (!File.Exists(destination) || Path.GetFileName(file) != "DiskInfo.ini")
                {
                    File.Copy(file, destination, true);
                }
            }
        }

        private static void ExtractZip(Stream stream, string target)
        {
            Directory.CreateDirectory(target);
            string root = Path.GetFullPath(target).TrimEnd('\\') + "\\";
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string relative = entry.FullName.Replace('/', '\\');
                    string destination = Path.GetFullPath(Path.Combine(target, relative));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // never write outside the target folder
                    }

                    if (relative.EndsWith("\\", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    // Keep the user's DiskInfo.ini once it exists so CrystalDiskInfo settings survive updates.
                    if (String.Equals(Path.GetFileName(destination), "DiskInfo.ini", StringComparison.OrdinalIgnoreCase) && File.Exists(destination))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (Stream input = entry.Open())
                    using (FileStream output = File.Create(destination))
                    {
                        input.CopyTo(output);
                    }
                }
            }
        }
    }
}
