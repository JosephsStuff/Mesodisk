# third_party/CrystalDiskInfo

This folder holds the unmodified portable (Standard edition) build of **CrystalDiskInfo 9.9.1**:

- Author: hiyohiyo
- Website: https://crystalmark.info/
- Source: https://github.com/hiyohiyo/CrystalDiskInfo
- Licence: MIT (`CrystalDiskInfo/License/COPYRIGHT.txt`), with bundled component licences in the same folder

`build.ps1` zips this folder and embeds it in `Mesodisk.exe`. At runtime Mesodisk extracts it and runs `DiskInfo64.exe /CopyExit` to read S.M.A.R.T. data.

The portable build was trimmed to what `/CopyExit` needs:

- removed: the 32-bit exe, the e-mail alert tool (AlertMail, MailKit, MimeKit) and the voice files (opus)
- kept: language, themes, dialog and bridge DLL folders

`DiskInfo.ini` contains only general settings: English output and USB bridge support enabled.

## Updating CrystalDiskInfo

1. Download the latest portable ZIP from https://crystalmark.info/en/download/ (Standard edition).
2. Replace `DiskInfo64.exe`, `DiskInfoA64.exe`, `CdiResource\` and `License\` here. Keep `DiskInfo.ini`.
3. Update the version number in this file, commit, and tag a new Mesodisk release.
