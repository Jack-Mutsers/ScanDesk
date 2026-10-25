# ScanDesk

A simple Windows 11 scanner app: scan several pages from any WIA scanner (Epson, Canon, HP, Brother, …) and save them as one PDF, separate JPEG/PNG images or a multi-page TIFF, wherever you choose.

## Features

- Finds scanners automatically (USB and network); pick a default scanner and hide ones you don't use
- Color, grayscale or black & white; 100–1200 dpi; A4, Letter, A5, photo or the whole glass
- Image correction like Epson Scan 2: deep blacks, white paper, sharper text (Document / Photo / Off)
- Straightens crooked pages automatically
- Crop, rotate, reorder and delete pages, with undo
- Fit to page: enlarge an A5 scan or a cropped part to fill an A4 or Letter sheet, with sharp high-quality resizing instead of blurry stretching
- Add existing images by dragging them into the window

## Download

Get the latest `ScanDesk-Setup-x.y.z.exe` from [Releases](https://github.com/Jack-Mutsers/ScanDesk/releases). It includes everything it needs; no separate .NET install required.

From version 1.0.3 on, ScanDesk checks for new releases when it starts (or via **Check for updates** in the bottom-left corner). **Install update** downloads the installer, verifies its SHA-256 checksum against the one GitHub publishes, installs it silently and reopens ScanDesk.

## Building

Requires the .NET 10 SDK and [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
./build-installer.ps1                  # dist\ScanDesk-Setup-0.0.0-local.exe
./build-installer.ps1 -Version 1.2.3   # dist\ScanDesk-Setup-1.2.3.exe
```

## Releases

Every push to `main` builds the installer and publishes it as a GitHub release. The first release is `v1.0.0`; each later push bumps the patch number (`v1.0.1`, `v1.0.2`, …). To start a new minor or major version, push a tag such as `v1.1.0` and the next push continues from there.
