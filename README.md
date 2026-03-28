# 🔐 PinBubble

A secure, lightweight Windows desktop application for storing and quickly accessing encrypted text snippets with Windows Hello biometric authentication support.

![.NET Core Desktop](https://github.com/niravp-0x/PinBubble/workflows/.NET%20Core%20Desktop/badge.svg)

## ✨ Features

### 🔒 Security First
- **AES-256-GCM Encryption** - Military-grade encryption for all your snippets
- **Master Password Protection** - Single password to rule them all
- **Windows Hello Biometric Authentication** - Fingerprint unlock support for compatible devices
- **Secure Storage** - All data encrypted at rest in `%AppData%\PinBubble\snippets.bin`
- **Zero Cloud Dependency** - Everything stays on your machine

### ⚡ Quick Access
- **Always-on-Top Bubble** - Stays accessible on your screen edge
- **One-Click Copy** - Click any snippet to copy it to clipboard
- **Expandable Interface** - Compact 64x64 bubble when not in use, expands on demand
- **System Tray Integration** - Minimize to tray for stealth mode
- **Multi-Monitor Support** - Remembers position per monitor

### 📝 Snippet Management
- **Visual Editor** - Modern DataGridView-based editor with dark/light theme support
- **Encryption Toggle** - Show/hide individual snippets with eye icon
- **LED Status Indicator** - Visual feedback for encryption state:
  - 🟢 Green: All encrypted (secure)
  - 🔴 Red: Some visible (warning)
- **Expiry Tracking** - Track creation, modification, and expiry dates for each snippet
- **Smart Expiry Alerts** - Color-coded clock icons warn you of expiring snippets:
  - 🔴 Red: < 7 days remaining
  - 🟡 Yellow: 7-14 days remaining
  - 🔵 Blue: 15+ days (hover-only visibility)
- **Auto-Save with Live Preview** - Changes saved automatically on edit

### 🎨 User Experience
- **Dark/Light Theme** - Toggle between modern dark and light modes
- **Adjustable Transparency** - 20%, 50%, or 80% backdrop opacity
- **Drag & Snap** - Drag the bubble anywhere, automatically snaps to nearest screen edge
- **Pin/Unpin** - Toggle always-on-top behavior
- **Auto-Encryption** - Edited entries automatically re-encrypt after 150ms typing pause
- **Rich Tooltips** - Hover over clock icons to see full timestamp details
- **Taskbar Toggle** - Show/hide from Windows taskbar on demand

## 📋 Requirements

- **Windows 10/11** (version 1809 or higher recommended)
- **.NET 8.0 Runtime** ([Download](https://dotnet.microsoft.com/download/dotnet/8.0))
- **Windows Hello** (optional, for biometric authentication)

## 🚀 Installation

### Download Pre-built Release
1. Go to [Releases](https://github.com/niravp-0x/PinBubble/releases)
2. Download the latest `PinBubble.zip`
3. Extract and run `PinBubble.exe`
4. Set your master password on first launch
5. (Optional) Enable fingerprint unlock when prompted

### Build from Source
```powershell
git clone https://github.com/niravp-0x/PinBubble.git
cd PinBubble
dotnet restore
dotnet build --configuration Release
dotnet run
```

## 🎯 Usage

### First Time Setup
1. Launch PinBubble
2. Enter a master password (minimum 4 characters)
3. **⚠️ CRITICAL**: Your password encrypts all data - **don't lose it!**
4. If you have Windows Hello, you'll be prompted to enable fingerprint unlock

### Biometric Authentication
- **First Launch**: If Windows Hello is available, PinBubble offers to enable fingerprint unlock
- **Subsequent Launches**: Touch your fingerprint sensor to unlock without typing password
- **Disable Biometric**: Right-click bubble → Settings → Biometric Settings

### Adding/Editing Snippets
1. Right-click the bubble → **Edit Snippets**
2. Add rows with:
   - **Clock Column**: Hover to see dates, click to set expiry
   - **Label**: Display name (uppercase letters/numbers shown on bubble, max 6 chars)
   - **Value**: The actual snippet text (shows `••••••••` when encrypted)
   - **Eye Icon**: Click to toggle encryption visibility
3. Each new snippet automatically gets **30 days expiry** by default
4. Changes auto-save on edit
5. Click **Save** or close dialog to finalize

### Using Snippets
1. **Click the green bubble** to expand and show all snippet buttons
2. **Click any snippet button** to copy to clipboard
3. Bubble auto-collapses after copying
4. **Drag the bubble** to reposition - it snaps to nearest screen edge
5. Press **ESC** to collapse manually

### Managing Expiry Dates
- **Hover** over the clock column to see:
  - Created date
  - Last modified date
  - Expiry date
  - Days remaining (color-coded)
- **Click** the clock icon to open date picker and update expiry
- Watch for color-coded alerts as expiry approaches

### Security Features
- **Eye Icon**: Click to toggle encryption/visibility for individual snippets
- **Show All Button**: Hold **Ctrl** key to reveal button, then toggle all snippets at once
- **LED Indicator**: Visual status at top of editor
  - Green glow = All encrypted (secure)
  - Red glow = Some snippets visible (security warning)
- **Auto-Encrypt**: Visible snippets automatically re-encrypt 150ms after you stop typing

### Settings & Customization
Right-click the bubble to access:
- **Edit Snippets**: Open the snippet editor
- **Dark Theme**: Toggle dark/light mode
- **Backdrop Opacity**: Choose 20%, 50%, or 80% transparency
- **Pin to Top**: Toggle always-on-top behavior
- **Show in Taskbar**: Toggle taskbar visibility
- **Biometric Settings**: Manage fingerprint unlock
- **Exit**: Close application

## 🗂️ Data Storage

### File Locations
- **Snippets**: `%AppData%\PinBubble\snippets.bin` (encrypted)
- **Settings**: `%AppData%\PinBubble\settings.json` (UI preferences)
- **Legacy Migration**: Old `text.text` files automatically migrated on first run

### File Format (Encrypted JSON)
```json
[
  {
    "label": "GitHub Token",
    "value": "ghp_YourActualTokenHere",
    "created": "2024-01-15T10:30:00Z",
    "modified": "2024-03-20T14:45:00Z",
    "expiryDate": "2024-04-20T14:45:00Z"
  }
]
```

**Backward Compatible**: 
- Old CSV format `Label,Value` automatically upgraded
- Old files gain current timestamps and 30-day default expiry on first load

## 🏗️ Built With

- **.NET 8.0** - Target framework (C# 12)
- **WPF** - Main window and bubble UI
- **Windows Forms** - DataGridView editor and custom dialogs
- **System.Security.Cryptography** - AES-256-GCM encryption
- **PBKDF2** - Password-based key derivation (100,000 iterations)
- **Windows Hello API** - Biometric authentication integration
- **System.Text.Json** - Fast JSON serialization

## 🎨 UI Highlights

- **Circular Bubble Design** - Modern, minimalist 64x64px interface
- **Glossy LED Indicator** - Polished visual feedback with gradient glow effects
- **Custom GDI+ Cell Painting** - Hand-drawn clock and eye icons with anti-aliasing
- **Smooth Animations** - Button hover effects and transitions
- **Responsive Layout** - Adapts to snippet count (max 5 per row, auto-expands)
- **Snap-to-Edge** - Intelligent edge detection across multiple monitors
- **Custom Dialogs** - Sleek borderless windows with modern styling

## 🔐 Security Details

### Encryption Specifications
- **Algorithm**: AES-256-GCM (Galois/Counter Mode)
- **Key Derivation**: PBKDF2-HMAC-SHA256 (100,000 iterations)
- **Salt**: 32 bytes, randomly generated per file
- **Nonce**: 12 bytes, randomly generated per encryption
- **Authentication Tag**: 16 bytes for tamper detection

### Security Guarantees
- Master password **never stored** anywhere
- Only derived keys kept in memory during session
- Each file has unique random salt
- Each encryption operation has unique nonce
- **No telemetry, no network access, no cloud storage**
- **Data stays on your machine**

### Biometric Security
- Master password encrypted using Windows Hello when stored
- Requires Windows Security prompt for each unlock
- Biometric data never leaves Windows credential store
- Can be disabled at any time from settings

## 🤝 Contributing

Contributions are welcome! Please:
1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## ⚠️ Disclaimer

This software is provided **as-is without any warranty**. While PinBubble uses industry-standard encryption, always keep backups of important data. The developers are not responsible for any data loss, security breaches, or other damages.

## 💡 Pro Tips

- **Bubble Labels**: Only uppercase A-Z and 0-9 characters appear on bubbles (auto-extracts from full label, max 6 chars)
- **Full Label View**: Hover over any bubble to see the complete label name in tooltip
- **Pin Feature**: Click the pin icon in corner to toggle always-on-top behavior (stays accessible even when other windows are active)
- **Quick Edit**: Right-click the bubble for fast access to editor
- **Backup Strategy**: Regularly copy `%AppData%\PinBubble\snippets.bin` - it contains ALL your encrypted data
- **Multi-Monitor**: PinBubble remembers which monitor you last used it on
- **Keyboard Shortcut**: Press **ESC** to quickly collapse the expanded bubble

## 🐛 Known Issues

- DateTimePicker dropdown may not fully inherit dark theme on some Windows versions
- Window may briefly lose topmost status when certain apps force focus (reactivates on deactivation)
- Biometric unlock requires Windows Hello to be configured in Windows Settings

## 🔮 Roadmap

- [ ] Custom expiry presets (7d, 30d, 90d, 365d, never)
- [ ] Search/filter snippets in editor
- [ ] Export/import functionality
- [ ] Custom bubble colors per snippet
- [ ] Keyboard shortcuts for common actions
- [ ] Multiple snippet groups/categories

## � Contributors

<a href="https://github.com/niravp-0x/PinBubble/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=niravp-0x/PinBubble" />
</a>

- **Mike Green** — [@biggrocer](https://github.com/biggrocer)
- **Nirav Panchal** — [@niravp-0x](https://github.com/niravp-0x)

Contributions are welcome! See [Contributing](#-contributing) for details.

## 📞 Support

For issues and feature requests, please use the [GitHub Issues](https://github.com/niravp-0x/PinBubble/issues) page.

Found a security vulnerability? Please email security reports privately.

---

[⭐ Star this repo](https://github.com/niravp-0x/PinBubble) if you find it useful!

---

> **Proudly and fully vibecoded with GitHub Copilot** 🤖
