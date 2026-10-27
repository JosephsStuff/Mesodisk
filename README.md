# Mesodisk

Mesodisk is a portable Windows app that formats every new disk you plug in, then shows that disk's full S.M.A.R.T. health report from [CrystalDiskInfo](https://github.com/hiyohiyo/CrystalDiskInfo). It's built for working through a pile of drives one after another.

**[⬇ Download the latest release](../../releases/latest)**: get `Mesodisk.exe`, a single file with nothing to install.

## How it works

1. Run `Mesodisk.exe` and accept the Administrator prompt.
2. Every disk connected **at this moment** is protected. Mesodisk will never format it, even if it is unplugged and plugged back in.
3. Pick the **file system** (FAT32 / exFAT / NTFS), the **label** and the partition style, then press **ARM** and confirm.
4. Insert a disk. Mesodisk:
   - erases it, creates one partition and quick-formats it with your settings
   - runs CrystalDiskInfo and shows the full S.M.A.R.T. report
   - shows the new file system, label, drive letter, capacity and free space
5. The results stay on screen until the **next new disk** is inserted. Then the cycle starts again. Disks are handled one at a time, and any extras inserted together wait in a queue.

### Disarming

- Press **DISARM** at any time.
- If no new disk arrives for **15 minutes**, Mesodisk disarms itself.
- When disarmed, a newly inserted disk gets the **S.M.A.R.T. check only**. Nothing is formatted. If you do want that disk formatted, press **Format this disk now** (you'll be asked to confirm) or re-arm and re-insert it.

### Safety rules

A disk is never formatted if:

- it was connected when Mesodisk started (matched by Windows disk ID and serial number)
- Windows reports it as the boot or system disk
- it is a virtual disk (VHD/VHDX, Storage Spaces)
- it reports no media (for example an empty card reader)
- **Only USB / SD / MMC disks** is ticked and the disk is on another bus

It also refuses to format if it never managed to read the disk list at startup, or if the disk at that number changed between detection and formatting. Settings are locked while armed.

## Screen

- **S.M.A.R.T. health**: CrystalDiskInfo's status (Good / Caution / Bad) and health %, plus temperature, power-on hours, power-on count and host writes.
- **Format summary**: whether it worked, file system, label, drive letter, capacity, available space and partition style.
- **Drive**: model, serial, firmware, interface, transfer mode, USB enclosure chip and features.
- **S.M.A.R.T. attributes**: every attribute with current, worst, threshold and raw values. Attributes at or below their threshold are shown in red. Non-zero reallocated, pending or uncorrectable counts are shown in amber.
- **Full CrystalDiskInfo report**: CrystalDiskInfo's text report for that disk, plus the diskpart output.
- **Activity log**: everything that happened this session.

## Building from source

The only requirement is Windows 10 or 11. Mesodisk compiles with the C# compiler that ships with Windows (.NET Framework 4.x), so there's no Visual Studio or SDK to install.

```powershell
git clone <this repo>
cd Mesodisk
.\Build-Mesodisk.cmd        # or: powershell -ExecutionPolicy Bypass -File build.ps1
```

Output:

| File | What it is |
|---|---|
| `dist\Mesodisk.exe` | the portable app, with CrystalDiskInfo embedded |
| `dist\Mesodisk-<version>-Windows-x64.zip` | the exe plus README and licence notices |
| `dist\SHA256SUMS.txt` | checksums |

### Project layout

```
src/Mesodisk/Program.cs           UI, arm/disarm, detection loop, job queue
src/Mesodisk/DiskCore.cs          disk enumeration, safety gates, diskpart formatter, fallback health
src/Mesodisk/CrystalDiskInfo.cs   runs DiskInfo64.exe /CopyExit and parses its report
src/Mesodisk/app.manifest         requests Administrator
third_party/CrystalDiskInfo/      unmodified portable CrystalDiskInfo (embedded at build time)
build.ps1                         build + package script (used locally and by CI)
.github/workflows/build.yml       CI build and GitHub Releases
```

## Releasing

Every push to `main` is built by GitHub Actions, and the exe is attached to the run as an artifact. To publish a release, use either method:

- **From the website:** go to **Actions → Build → Run workflow**, type a version such as `1.1.0`, and press **Run workflow**.
- **From git:** `git tag v1.1.0` then `git push origin v1.1.0`.

The workflow builds that version, stamps it into the exe, tags the commit, and creates a GitHub Release with `Mesodisk.exe`, the zip and the checksums. Versions with a suffix (for example `1.1.0-beta1`) are published as pre-releases.

## Notes

- FAT32 cannot be created by diskpart on volumes above 32 GB. With **Fall back to exFAT** ticked, Mesodisk retries as exFAT and says so.
- Partition style **Auto** means MBR up to 2 TB and GPT above.
- How CrystalDiskInfo is run:
  - On first use, the embedded copy is extracted to `%LOCALAPPDATA%\Mesodisk\CrystalDiskInfo`. A `CrystalDiskInfo` folder next to `Mesodisk.exe` takes priority.
  - Mesodisk runs `DiskInfo64.exe /CopyExit` (briefly, minimised).
  - Your `DiskInfo.ini` settings are kept. Mesodisk only forces English output and turns off sounds, alerts and hex dumps.
- If CrystalDiskInfo can't read a disk (some USB bridges block S.M.A.R.T.), Mesodisk falls back to `smartctl` if installed, otherwise to Windows' storage health data.
- The exe is not code-signed, so Windows SmartScreen may say the publisher is unknown.

## Licence

Mesodisk is released under the [MIT License](LICENSE).

It embeds CrystalDiskInfo, (C) 2008-2026 hiyohiyo, which is also MIT-licensed. See [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) and [third_party/README.md](third_party/README.md).
