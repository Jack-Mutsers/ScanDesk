# ScanDesk

A simple Windows 11 scanner app: scan several pages from any WIA scanner (Epson, Canon, HP, Brother, …) and save them as one PDF, separate JPEG/PNG images or a multi-page TIFF, wherever you choose.

## Features

- Finds scanners automatically (USB and network); pick a default scanner and hide ones you don't use
- Color, grayscale or black & white; 100–1200 dpi; A4, Letter, A5, photo or the whole glass
- Image correction like Epson Scan 2: deep blacks, white paper, sharper text (Document / Photo / Off)
- Straightens crooked pages automatically
- Crop, rotate, reorder and delete pages, with undo
- Fit to format: enlarge or shrink a page (an A5 scan, a cropped part, a photo) to A3, A4, A5, A6, Letter, Legal or 10 × 15 cm, with sharp high-quality resizing instead of blurry stretching
- Add existing images by dragging them into the window
- Menu bar with keyboard shortcuts, and a Settings window (language, automatic updates, scanners)
- Dutch (default) and English

## Branches and testing

- `develop` is where new features go. Every push builds a test installer (`ScanDesk-Setup-x.y.z-test.N.exe`), attached to the run on the **Actions** tab under *Test build*. Test builds never reach users.
- `main` is protected: changes arrive only through a pull request from `develop`, which can be merged once its test build passes and the maintainer has tested it. Merging into `main` publishes a new release, which the app offers as an update.

## Download

Get the latest `ScanDesk-Setup-x.y.z.exe` from [Releases](https://github.com/Jack-Mutsers/ScanDesk/releases). It includes everything it needs; no separate .NET install required.

From version 1.0.3 on, ScanDesk checks for new releases when it starts and every 2 hours while it is open (or via **Help → Check for updates**). **Install update** downloads the installer, verifies its SHA-256 checksum against the one GitHub publishes, installs it silently and reopens ScanDesk.

## Building

Requires the .NET 10 SDK and [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
./build-installer.ps1                  # dist\ScanDesk-Setup-0.0.0-local.exe
./build-installer.ps1 -Version 1.2.3   # dist\ScanDesk-Setup-1.2.3.exe
```

## Releases

Every push to `main` builds the installer and publishes it as a GitHub release. The first release is `v1.0.0`; each later push bumps the patch number (`v1.0.1`, `v1.0.2`, …). To start a new minor or major version, push a tag such as `v1.1.0` and the next push continues from there.

## Privacy

ScanDesk works entirely on your own PC: no accounts, no advertising, no usage tracking, and your scans never leave your computer. Its only internet connection is to GitHub, to check for and download updates, and the automatic check can be turned off under **Extra → Settings**. See [PRIVACY.md](PRIVACY.md) for the details.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

> **Status:** the application to SignPath Foundation is pending. Until it is approved, releases are not yet signed.

- **What is signed:** `ScanDesk-Setup-x.y.z.exe` and the `ScanDesk.exe` it installs, built by GitHub Actions directly from this repository. Nothing built elsewhere is signed.
- **Committers and reviewers:** [Jack-Mutsers](https://github.com/Jack-Mutsers)
- **Approvers:** [Jack-Mutsers](https://github.com/Jack-Mutsers). Every release is approved by hand before it is signed.
- **Privacy:** ScanDesk does not send any information to other systems, except for the update check and download described in [PRIVACY.md](PRIVACY.md), which can be turned off.

## License

Copyright © 2026 Jack Mutsers

ScanDesk is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, version 3 of the License. Modified versions must be shared under the same license, with their source code.

ScanDesk is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the [LICENSE](LICENSE) file for the full text.
