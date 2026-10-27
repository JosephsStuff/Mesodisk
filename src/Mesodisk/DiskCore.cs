// Mesodisk - disk detection, safety gates, formatting and fallback health.
// Ported from SSD Checker (same author); logic kept, small additions marked "Mesodisk:".
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Mesodisk
{
    internal sealed class DiskInfo
    {
        public int Number;
        public string FriendlyName;
        public string SerialNumber;
        public string UniqueId;
        public ulong Size;
        public string BusType;
        public string PartitionStyle;
        public string HealthStatusText;
        public string OperationalStatusText;
        public bool IsBoot;
        public bool IsSystem;
        public bool IsReadOnly;
        public bool IsOffline;
        public string Source = ""; // Mesodisk: "MSFT" (Storage WMI) or "Win32" (fallback)
    }

    internal sealed class HealthReport
    {
        public string Source;
        public string Overall;
        public bool? Passed;
        public int? HealthPercent;
        public int? TemperatureC;
        public long? PowerOnHours;
        public int? WearPercentUsed;
        public List<string> Notes;

        public HealthReport()
        {
            Notes = new List<string>();
        }
    }

    internal sealed class SafetyResult
    {
        public bool Safe;
        public List<string> Messages;

        public SafetyResult()
        {
            Messages = new List<string>();
        }
    }

    internal sealed class FormatResult
    {
        public bool Success;
        public string FileSystem;
        public string Output;
        public bool FallbackUsed;
    }

    internal sealed class VolumeSummary
    {
        public bool Ready;
        public int VolumeCount;
        public string DriveLetter = "";
        public string FileSystem = "";
        public string Label = "";
        public ulong TotalBytes;
        public ulong FreeBytes;
    }

    internal static class DiskService
    {
        public static List<DiskInfo> EnumerateDisks()
        {
            try
            {
                List<DiskInfo> disks = EnumerateMsftDisks();
                if (disks.Count > 0)
                {
                    return disks;
                }
            }
            catch
            {
            }

            return EnumerateWin32Disks();
        }

        public static string GetDiskKey(DiskInfo disk)
        {
            if (!String.IsNullOrWhiteSpace(disk.UniqueId))
            {
                return "uid:" + disk.UniqueId.Trim();
            }

            if (!String.IsNullOrWhiteSpace(disk.SerialNumber))
            {
                return "serial:" + disk.SerialNumber.Trim();
            }

            return "number:" + disk.Number.ToString(CultureInfo.InvariantCulture) + ":" + disk.FriendlyName + ":" + disk.Size.ToString(CultureInfo.InvariantCulture);
        }

        public static string FormatBytes(ulong bytes)
        {
            double value = bytes;
            if (bytes >= 1099511627776UL)
            {
                return (value / 1099511627776d).ToString("N2", CultureInfo.InvariantCulture) + " TB";
            }

            if (bytes >= 1073741824UL)
            {
                return (value / 1073741824d).ToString("N2", CultureInfo.InvariantCulture) + " GB";
            }

            if (bytes >= 1048576UL)
            {
                return (value / 1048576d).ToString("N2", CultureInfo.InvariantCulture) + " MB";
            }

            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        public static SafetyResult TestDiskSafeForFormat(DiskInfo disk, Dictionary<string, bool> startupDiskKeys, Dictionary<string, bool> startupSerials, bool onlyRemovable)
        {
            SafetyResult result = new SafetyResult();
            bool blocking = false;
            string key = GetDiskKey(disk);

            if (startupDiskKeys.ContainsKey(key))
            {
                result.Messages.Add("Disk was already present when Mesodisk started.");
                blocking = true;
            }
            else if (!String.IsNullOrWhiteSpace(disk.SerialNumber) && startupSerials.ContainsKey(disk.SerialNumber.Trim()))
            {
                // Mesodisk: second guard in case Windows reports a different UniqueId for a startup disk.
                result.Messages.Add("Disk serial matches a disk that was present when Mesodisk started.");
                blocking = true;
            }

            if (disk.Size == 0)
            {
                result.Messages.Add("Disk reports no media (size 0).");
                blocking = true;
            }

            if (IsVirtualBus(disk.BusType))
            {
                result.Messages.Add("Disk is a virtual disk (" + disk.BusType + "), not a physical drive.");
                blocking = true;
            }

            if (disk.IsBoot)
            {
                result.Messages.Add("Disk is marked as the boot disk.");
                blocking = true;
            }

            if (disk.IsSystem)
            {
                result.Messages.Add("Disk is marked as a system disk.");
                blocking = true;
            }

            if (onlyRemovable && !IsAllowedRemovableBus(disk.BusType))
            {
                result.Messages.Add("Disk bus type is '" + disk.BusType + "', not USB/SD/MMC.");
                blocking = true;
            }

            result.Safe = !blocking;
            return result;
        }

        public static string GetPartitionStyleForDisk(DiskInfo disk, string selectedStyle)
        {
            if (String.Equals(selectedStyle, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                if (disk.Size > 2199023255552UL)
                {
                    return "GPT";
                }

                return "MBR";
            }

            if (String.Equals(selectedStyle, "GPT", StringComparison.OrdinalIgnoreCase))
            {
                return "GPT";
            }

            return "MBR";
        }

        // Mesodisk: NTFS labels may be up to 32 characters; FAT32/exFAT stay at 11.
        public static string SanitizeVolumeLabel(string input, string fileSystem)
        {
            string label = String.IsNullOrWhiteSpace(input) ? "BLANK" : input.Trim();
            label = Regex.Replace(label, "[\\\\/:*?\"<>|.,;\\[\\]+=]", "");
            label = Regex.Replace(label, "\\s+", " ");
            if (label.Length == 0)
            {
                label = "BLANK";
            }

            int max = String.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) ? 32 : 11;
            if (label.Length > max)
            {
                label = label.Substring(0, max).TrimEnd();
            }

            return label;
        }

        public static string SanitizeVolumeLabel(string input)
        {
            string label = String.IsNullOrWhiteSpace(input) ? "BLANK" : input.Trim();
            label = Regex.Replace(label, "[\\\\/:*?\"<>|.,;\\[\\]+=]", "");
            label = Regex.Replace(label, "\\s+", " ");
            if (label.Length == 0)
            {
                label = "BLANK";
            }

            if (label.Length > 11)
            {
                label = label.Substring(0, 11);
            }

            return label;
        }

        public static bool IsVirtualBus(string busType)
        {
            return String.Equals(busType, "Virtual", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(busType, "FileBackedVirtual", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(busType, "Spaces", StringComparison.OrdinalIgnoreCase);
        }

        public static DiskInfo FindDisk(int number)
        {
            foreach (DiskInfo disk in EnumerateDisks())
            {
                if (disk.Number == number)
                {
                    return disk;
                }
            }

            return null;
        }

        // Mesodisk: volume summary (letter, file system, label, capacity, free space) for a disk.
        public static VolumeSummary GetVolume(int diskNumber)
        {
            List<string> letters = new List<string>();
            try
            {
                ManagementScope scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                scope.Connect();
                string query = "SELECT DriveLetter, Size FROM MSFT_Partition WHERE DiskNumber = " + diskNumber.ToString(CultureInfo.InvariantCulture);
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query)))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        object value = GetPropertyValue(obj, "DriveLetter");
                        char letter = '\0';
                        if (value is char)
                        {
                            letter = (char)value;
                        }
                        else if (value != null)
                        {
                            try { letter = Convert.ToChar(Convert.ToUInt16(value, CultureInfo.InvariantCulture)); } catch { letter = '\0'; }
                        }

                        if (Char.IsLetter(letter))
                        {
                            letters.Add(Char.ToUpperInvariant(letter).ToString() + ":");
                        }
                    }
                }
            }
            catch
            {
            }

            if (letters.Count == 0)
            {
                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"\\.\root\cimv2", "SELECT * FROM Win32_DiskDrive WHERE Index = " + diskNumber.ToString(CultureInfo.InvariantCulture)))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            letters.AddRange(GetDriveLettersForDisk(obj));
                        }
                    }
                }
                catch
                {
                }
            }

            VolumeSummary summary = new VolumeSummary();
            summary.VolumeCount = letters.Count;
            foreach (string letter in letters)
            {
                try
                {
                    DriveInfo drive = new DriveInfo(letter + "\\");
                    summary.DriveLetter = letter;
                    if (drive.IsReady)
                    {
                        summary.FileSystem = drive.DriveFormat;
                        summary.Label = drive.VolumeLabel;
                        summary.TotalBytes = (ulong)drive.TotalSize;
                        summary.FreeBytes = (ulong)drive.AvailableFreeSpace;
                        summary.Ready = true;
                        return summary;
                    }
                }
                catch
                {
                }
            }

            return summary;
        }

        public static VolumeSummary WaitForVolume(int diskNumber, int timeoutSeconds)
        {
            VolumeSummary summary = null;
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                summary = GetVolume(diskNumber);
                if (summary.Ready)
                {
                    return summary;
                }

                Thread.Sleep(1000);
            }

            return summary ?? new VolumeSummary();
        }

        private static object GetPropertyValue(ManagementBaseObject obj, string name)
        {
            try
            {
                PropertyData property = obj.Properties[name];
                return property == null ? null : property.Value;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsAllowedRemovableBus(string busType)
        {
            return String.Equals(busType, "USB", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(busType, "SD", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(busType, "MMC", StringComparison.OrdinalIgnoreCase);
        }

        private static List<DiskInfo> EnumerateMsftDisks()
        {
            List<DiskInfo> disks = new List<DiskInfo>();
            ManagementScope scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_Disk")))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    DiskInfo disk = new DiskInfo();
                    disk.Source = "MSFT";
                    disk.Number = GetInt(obj, "Number", -1);
                    disk.FriendlyName = FirstNonEmpty(GetString(obj, "FriendlyName"), "Disk " + disk.Number.ToString(CultureInfo.InvariantCulture));
                    disk.SerialNumber = GetString(obj, "SerialNumber");
                    disk.UniqueId = GetString(obj, "UniqueId");
                    disk.Size = GetUInt64(obj, "Size", 0);
                    disk.BusType = MapBusType(GetInt(obj, "BusType", -1));
                    disk.PartitionStyle = MapPartitionStyle(GetInt(obj, "PartitionStyle", -1));
                    disk.HealthStatusText = MapHealthStatus(GetProperty(obj, "HealthStatus"));
                    disk.OperationalStatusText = MapOperationalStatus(GetProperty(obj, "OperationalStatus"));
                    disk.IsBoot = GetBool(obj, "IsBoot", false);
                    disk.IsSystem = GetBool(obj, "IsSystem", false);
                    disk.IsReadOnly = GetBool(obj, "IsReadOnly", false);
                    disk.IsOffline = GetBool(obj, "IsOffline", false);

                    if (disk.Number >= 0)
                    {
                        disks.Add(disk);
                    }
                }
            }

            return disks;
        }

        public static List<DiskInfo> EnumerateWin32Disks()
        {
            List<DiskInfo> disks = new List<DiskInfo>();
            string systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "").TrimEnd('\\');

            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"\\.\root\cimv2", "SELECT * FROM Win32_DiskDrive"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    DiskInfo disk = new DiskInfo();
                    disk.Source = "Win32";
                    disk.Number = GetInt(obj, "Index", -1);
                    disk.FriendlyName = FirstNonEmpty(GetString(obj, "Model"), "Disk " + disk.Number.ToString(CultureInfo.InvariantCulture));
                    disk.SerialNumber = GetString(obj, "SerialNumber");
                    disk.UniqueId = GetString(obj, "PNPDeviceID");
                    disk.Size = GetUInt64(obj, "Size", 0);
                    disk.BusType = InferWin32BusType(GetString(obj, "InterfaceType"), GetString(obj, "PNPDeviceID"));
                    disk.PartitionStyle = "Unknown";
                    disk.HealthStatusText = GetString(obj, "Status");
                    disk.OperationalStatusText = GetString(obj, "Status");

                    List<string> letters = GetDriveLettersForDisk(obj);
                    foreach (string letter in letters)
                    {
                        if (String.Equals(letter, systemDrive, StringComparison.OrdinalIgnoreCase))
                        {
                            disk.IsBoot = true;
                            disk.IsSystem = true;
                            break;
                        }
                    }

                    if (disk.Number >= 0)
                    {
                        disks.Add(disk);
                    }
                }
            }

            return disks;
        }

        private static List<string> GetDriveLettersForDisk(ManagementObject disk)
        {
            List<string> letters = new List<string>();
            string relPath = GetString(disk, "__RELPATH");
            if (String.IsNullOrEmpty(relPath))
            {
                relPath = disk.Path.RelativePath;
            }

            using (ManagementObjectSearcher partitionSearcher = new ManagementObjectSearcher("ASSOCIATORS OF {" + relPath + "} WHERE AssocClass=Win32_DiskDriveToDiskPartition"))
            {
                foreach (ManagementObject partition in partitionSearcher.Get())
                {
                    string partitionPath = GetString(partition, "__RELPATH");
                    if (String.IsNullOrEmpty(partitionPath))
                    {
                        partitionPath = partition.Path.RelativePath;
                    }

                    using (ManagementObjectSearcher logicalSearcher = new ManagementObjectSearcher("ASSOCIATORS OF {" + partitionPath + "} WHERE AssocClass=Win32_LogicalDiskToPartition"))
                    {
                        foreach (ManagementObject logicalDisk in logicalSearcher.Get())
                        {
                            string deviceId = GetString(logicalDisk, "DeviceID");
                            if (!String.IsNullOrEmpty(deviceId))
                            {
                                letters.Add(deviceId.TrimEnd('\\'));
                            }
                        }
                    }
                }
            }

            return letters;
        }

        private static string InferWin32BusType(string interfaceType, string pnpDeviceId)
        {
            if (!String.IsNullOrEmpty(pnpDeviceId) && pnpDeviceId.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
            {
                return "USB";
            }

            if (!String.IsNullOrEmpty(interfaceType))
            {
                return interfaceType;
            }

            return "Unknown";
        }

        private static object GetProperty(ManagementBaseObject obj, string name)
        {
            try
            {
                PropertyData property = obj.Properties[name];
                if (property == null)
                {
                    return null;
                }

                return property.Value;
            }
            catch
            {
                return null;
            }
        }

        internal static string GetString(ManagementBaseObject obj, string name)
        {
            object value = GetProperty(obj, name);
            if (value == null)
            {
                return "";
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
        }

        internal static int GetInt(ManagementBaseObject obj, string name, int fallback)
        {
            object value = GetProperty(obj, name);
            if (value == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        internal static ulong GetUInt64(ManagementBaseObject obj, string name, ulong fallback)
        {
            object value = GetProperty(obj, name);
            if (value == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private static bool GetBool(ManagementBaseObject obj, string name, bool fallback)
        {
            object value = GetProperty(obj, name);
            if (value == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private static string FirstNonEmpty(string first, string second)
        {
            return String.IsNullOrWhiteSpace(first) ? second : first;
        }

        private static string MapBusType(int value)
        {
            switch (value)
            {
                case 0: return "Unknown";
                case 1: return "SCSI";
                case 2: return "ATAPI";
                case 3: return "ATA";
                case 4: return "1394";
                case 5: return "SSA";
                case 6: return "Fibre";
                case 7: return "USB";
                case 8: return "RAID";
                case 9: return "iSCSI";
                case 10: return "SAS";
                case 11: return "SATA";
                case 12: return "SD";
                case 13: return "MMC";
                case 14: return "Virtual";
                case 15: return "FileBackedVirtual";
                case 16: return "Spaces";
                case 17: return "NVMe";
                case 18: return "SCM";
                case 19: return "UFS";
                default: return "Unknown";
            }
        }

        private static string MapPartitionStyle(int value)
        {
            switch (value)
            {
                case 0: return "RAW";
                case 1: return "MBR";
                case 2: return "GPT";
                default: return "Unknown";
            }
        }

        internal static string MapHealthStatus(object value)
        {
            if (value == null)
            {
                return "Unknown";
            }

            int numeric;
            if (Int32.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                switch (numeric)
                {
                    case 0: return "Healthy";
                    case 1: return "Warning";
                    case 2: return "Unhealthy";
                    case 5: return "Unknown";
                    default: return "Unknown";
                }
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string MapOperationalStatus(object value)
        {
            if (value == null)
            {
                return "Unknown";
            }

            Array array = value as Array;
            if (array != null)
            {
                List<string> parts = new List<string>();
                foreach (object item in array)
                {
                    parts.Add(MapOperationalStatusValue(Convert.ToInt32(item, CultureInfo.InvariantCulture)));
                }

                return String.Join(", ", parts.ToArray());
            }

            int numeric;
            if (Int32.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                return MapOperationalStatusValue(numeric);
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string MapOperationalStatusValue(int value)
        {
            switch (value)
            {
                case 0: return "Unknown";
                case 1: return "Other";
                case 2: return "OK";
                case 3: return "Degraded";
                case 4: return "Stressed";
                case 5: return "Predictive Failure";
                case 6: return "Error";
                case 7: return "Non-Recoverable Error";
                case 8: return "Starting";
                case 9: return "Stopping";
                case 10: return "Stopped";
                case 11: return "In Service";
                case 12: return "No Contact";
                case 13: return "Lost Communication";
                case 14: return "Aborted";
                case 15: return "Dormant";
                case 16: return "Supporting Entity in Error";
                case 17: return "Completed";
                case 18: return "Power Mode";
                case 19: return "Relocating";
                default: return "Unknown";
            }
        }
    }

    internal static class HealthService
    {
        // Mesodisk: smartctl is an optional fallback; also checked next to Mesodisk.exe.
        public static string FindSmartCtl(string appDirectory)
        {
            List<string> candidates = new List<string>();
            if (!String.IsNullOrEmpty(appDirectory))
            {
                candidates.Add(Path.Combine(appDirectory, "smartctl.exe"));
            }

            string pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string directory in pathValue.Split(Path.PathSeparator))
            {
                if (!String.IsNullOrWhiteSpace(directory))
                {
                    try { candidates.Add(Path.Combine(directory.Trim(), "smartctl.exe")); } catch { }
                }
            }

            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "smartmontools", "bin", "smartctl.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "smartmontools", "bin", "smartctl.exe"));

            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            return "";
        }

        public static HealthReport GetDiskHealth(DiskInfo disk, string smartCtlPath)
        {
            HealthReport smart = GetSmartCtlHealth(disk, smartCtlPath);
            if (smart != null)
            {
                return smart;
            }

            return GetNativeHealth(disk);
        }

        private static HealthReport GetSmartCtlHealth(DiskInfo disk, string smartCtlPath)
        {
            if (String.IsNullOrEmpty(smartCtlPath) || !File.Exists(smartCtlPath))
            {
                return null;
            }

            ProcessResult process = RunProcess(smartCtlPath, "-a -j " + QuoteArgument("/dev/physicaldrive" + disk.Number.ToString(CultureInfo.InvariantCulture)));
            string json = process.Output;
            if (String.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            Dictionary<string, object> root;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            }
            catch
            {
                return null;
            }

            if (root == null)
            {
                return null;
            }

            HealthReport report = new HealthReport();
            report.Source = "smartctl";
            report.Overall = "Unknown";

            Dictionary<string, object> smartStatus = GetDictionary(root, "smart_status");
            bool? passed = GetNullableBool(smartStatus, "passed");
            report.Passed = passed;
            if (passed.HasValue)
            {
                report.Overall = passed.Value ? "Passed" : "Failed";
            }

            Dictionary<string, object> nvme = GetDictionary(root, "nvme_smart_health_information_log");
            int? percentageUsed = GetNullableInt(nvme, "percentage_used");
            if (percentageUsed.HasValue)
            {
                report.WearPercentUsed = percentageUsed.Value;
                report.HealthPercent = ClampPercent(100 - percentageUsed.Value);
            }

            if (!report.HealthPercent.HasValue)
            {
                TryReadAtaHealth(root, report);
            }

            Dictionary<string, object> temperature = GetDictionary(root, "temperature");
            report.TemperatureC = GetNullableInt(temperature, "current");
            if (!report.TemperatureC.HasValue)
            {
                report.TemperatureC = GetNullableInt(nvme, "temperature");
            }

            Dictionary<string, object> powerOnTime = GetDictionary(root, "power_on_time");
            int? hours = GetNullableInt(powerOnTime, "hours");
            if (hours.HasValue)
            {
                report.PowerOnHours = hours.Value;
            }

            if (!report.HealthPercent.HasValue)
            {
                report.Notes.Add("smartctl reported SMART status but no known numeric life percentage.");
            }

            return report;
        }

        private static void TryReadAtaHealth(Dictionary<string, object> root, HealthReport report)
        {
            Dictionary<string, object> ata = GetDictionary(root, "ata_smart_attributes");
            object tableObject;
            if (ata == null || !ata.TryGetValue("table", out tableObject))
            {
                return;
            }

            object[] table = tableObject as object[];
            if (table == null)
            {
                return;
            }

            foreach (object item in table)
            {
                Dictionary<string, object> attr = item as Dictionary<string, object>;
                if (attr == null)
                {
                    continue;
                }

                string name = GetString(attr, "name");
                if (Regex.IsMatch(name, "Media_Wearout_Indicator|SSD_Life_Left|Percent_Lifetime_Remain|Remaining_Lifetime_Perc|Life_Left|SSD_Life", RegexOptions.IgnoreCase))
                {
                    int? value = GetNullableInt(attr, "value");
                    if (value.HasValue)
                    {
                        report.HealthPercent = ClampPercent(value.Value);
                        return;
                    }
                }

                if (Regex.IsMatch(name, "Percentage_Used|Percent_Lifetime_Used", RegexOptions.IgnoreCase))
                {
                    int? raw = null;
                    Dictionary<string, object> rawDictionary = GetDictionary(attr, "raw");
                    if (rawDictionary != null)
                    {
                        raw = GetNullableInt(rawDictionary, "value");
                    }

                    if (!raw.HasValue)
                    {
                        raw = GetNullableInt(attr, "value");
                    }

                    if (raw.HasValue)
                    {
                        report.WearPercentUsed = raw.Value;
                        report.HealthPercent = ClampPercent(100 - raw.Value);
                        return;
                    }
                }
            }
        }

        private static HealthReport GetNativeHealth(DiskInfo disk)
        {
            HealthReport report = new HealthReport();
            report.Source = "Windows Storage";
            report.Overall = String.IsNullOrEmpty(disk.HealthStatusText) ? "Unknown" : disk.HealthStatusText;
            if (String.Equals(report.Overall, "Healthy", StringComparison.OrdinalIgnoreCase) || String.Equals(report.Overall, "OK", StringComparison.OrdinalIgnoreCase))
            {
                report.Passed = true;
            }
            else if (!String.Equals(report.Overall, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                report.Passed = false;
            }

            try
            {
                ManagementObject physicalDisk = FindPhysicalDisk(disk);
                if (physicalDisk != null)
                {
                    string physicalHealth = DiskService.MapHealthStatus(GetProperty(physicalDisk, "HealthStatus"));
                    if (!String.IsNullOrEmpty(physicalHealth) && !String.Equals(physicalHealth, "Unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        report.Overall = physicalHealth;
                        report.Passed = String.Equals(physicalHealth, "Healthy", StringComparison.OrdinalIgnoreCase);
                    }

                    foreach (ManagementObject counter in physicalDisk.GetRelated("MSFT_StorageReliabilityCounter"))
                    {
                        int? wear = GetNullableManagementInt(counter, "Wear");
                        if (wear.HasValue)
                        {
                            report.WearPercentUsed = wear.Value;
                            report.HealthPercent = ClampPercent(100 - wear.Value);
                        }

                        report.TemperatureC = FirstNullable(report.TemperatureC, GetNullableManagementInt(counter, "Temperature"));
                        int? powerOnHours = GetNullableManagementInt(counter, "PowerOnHours");
                        if (powerOnHours.HasValue)
                        {
                            report.PowerOnHours = powerOnHours.Value;
                        }
                    }
                }
                else
                {
                    report.Notes.Add("No matching PhysicalDisk object was found.");
                }
            }
            catch
            {
                report.Notes.Add("Windows could not read StorageReliabilityCounter for this disk.");
            }

            if (!report.HealthPercent.HasValue)
            {
                report.Notes.Add("A numeric SMART health percentage was not exposed by Windows.");
            }

            return report;
        }

        private static ManagementObject FindPhysicalDisk(DiskInfo disk)
        {
            ManagementScope scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk")))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    string deviceId = DiskService.GetString(obj, "DeviceId");
                    string serial = DiskService.GetString(obj, "SerialNumber");
                    string uniqueId = DiskService.GetString(obj, "UniqueId");

                    if (String.Equals(deviceId, disk.Number.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase) ||
                        (!String.IsNullOrEmpty(serial) && String.Equals(serial.Trim(), disk.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                        (!String.IsNullOrEmpty(uniqueId) && String.Equals(uniqueId.Trim(), disk.UniqueId.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        return obj;
                    }
                }
            }

            return null;
        }

        private static int ClampPercent(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 100)
            {
                return 100;
            }

            return value;
        }

        private static int? FirstNullable(int? first, int? second)
        {
            return first.HasValue ? first : second;
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> root, string key)
        {
            if (root == null)
            {
                return null;
            }

            object value;
            if (!root.TryGetValue(key, out value))
            {
                return null;
            }

            return value as Dictionary<string, object>;
        }

        private static bool? GetNullableBool(Dictionary<string, object> root, string key)
        {
            if (root == null)
            {
                return null;
            }

            object value;
            if (!root.TryGetValue(key, out value) || value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static int? GetNullableInt(Dictionary<string, object> root, string key)
        {
            if (root == null)
            {
                return null;
            }

            object value;
            if (!root.TryGetValue(key, out value) || value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static string GetString(Dictionary<string, object> root, string key)
        {
            if (root == null)
            {
                return "";
            }

            object value;
            if (!root.TryGetValue(key, out value) || value == null)
            {
                return "";
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static object GetProperty(ManagementBaseObject obj, string name)
        {
            try
            {
                PropertyData property = obj.Properties[name];
                return property == null ? null : property.Value;
            }
            catch
            {
                return null;
            }
        }

        private static int? GetNullableManagementInt(ManagementBaseObject obj, string name)
        {
            object value = GetProperty(obj, name);
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static ProcessResult RunProcess(string fileName, string arguments)
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = fileName;
            info.Arguments = arguments;
            info.UseShellExecute = false;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.CreateNoWindow = true;

            using (Process process = Process.Start(info))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return new ProcessResult(process.ExitCode, output, error);
            }
        }

        private static string QuoteArgument(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }

    internal static class DiskFormatter
    {
        public static FormatResult FormatDisk(int diskNumber, string fileSystem, string partitionStyle, string label)
        {
            string script = BuildDiskPartScript(diskNumber, fileSystem, partitionStyle, label);
            ProcessResult process = RunDiskPart(script);

            FormatResult result = new FormatResult();
            result.FileSystem = fileSystem;
            result.Output = process.Output + process.Error;
            result.Success = process.ExitCode == 0 && !LooksLikeDiskPartError(result.Output);
            return result;
        }

        private static string BuildDiskPartScript(int diskNumber, string fileSystem, string partitionStyle, string label)
        {
            StringBuilder script = new StringBuilder();
            script.AppendLine("select disk " + diskNumber.ToString(CultureInfo.InvariantCulture));
            script.AppendLine("attributes disk clear readonly noerr");
            script.AppendLine("online disk noerr");
            script.AppendLine("clean");
            script.AppendLine("convert " + partitionStyle.ToLowerInvariant());
            script.AppendLine("create partition primary");
            script.AppendLine("format fs=" + fileSystem.ToLowerInvariant() + " quick label=\"" + label.Replace("\"", "") + "\"");
            script.AppendLine("assign");
            script.AppendLine("exit");
            return script.ToString();
        }

        private static ProcessResult RunDiskPart(string script)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "mesodisk-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(tempFile, script, Encoding.ASCII);

            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = Path.Combine(Environment.SystemDirectory, "diskpart.exe");
                info.Arguments = "/s " + QuoteArgument(tempFile);
                info.UseShellExecute = false;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.CreateNoWindow = true;

                using (Process process = Process.Start(info))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    return new ProcessResult(process.ExitCode, output, error);
                }
            }
            finally
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch
                {
                }
            }
        }

        private static bool LooksLikeDiskPartError(string output)
        {
            if (String.IsNullOrEmpty(output))
            {
                return false;
            }

            return output.IndexOf("DiskPart has encountered an error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("Virtual Disk Service error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("The parameter is incorrect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("is not allowed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }

    internal sealed class ProcessResult
    {
        public readonly int ExitCode;
        public readonly string Output;
        public readonly string Error;

        public ProcessResult(int exitCode, string output, string error)
        {
            ExitCode = exitCode;
            Output = output ?? "";
            Error = error ?? "";
        }
    }
}
