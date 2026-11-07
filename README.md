# ZenTune

A free, open-source game settings optimizer for Windows. ZenTune looks at your hardware and your screen and picks the settings that give you the best mix of FPS, image quality and low input lag, and explains why.

## Status

Version 0.5 (beta). Expect bugs, and please report them (Feedback page → Collect info).

- **Your PC:** graphics card, processor, memory, PC maker, and every display (resolution, refresh rate, HDR, which GPU it is plugged into)
- **System check:** finds settings that cost FPS (refresh rate, screen on the motherboard port, Game Mode, GPU scheduling, windowed optimizations, background recording, power plan, mouse acceleration, memory, and the GameInput version that can stop Call of Duty from launching). One-click fixes, and every fix can be undone
- **Games:** finds 33 games on Steam and Epic: Counter-Strike 2, Call of Duty: Modern Warfare 4, Call of Duty: Black Ops 7 / Warzone, Battlefield 6 / REDSEC, ARC Raiders, Apex Legends, Rainbow Six Siege, PUBG, Marvel Rivals, Helldivers 2, Fallout 76, Hogwarts Legacy, Avowed, Cyberpunk 2077, Elden Ring, The Outer Worlds 1 and 2, Final Fantasy VII Rebirth, The Finals, Dead by Daylight, Black Myth: Wukong, S.T.A.L.K.E.R. 2, Satisfactory, WARDOGS, Fortnite, The Blood of Dawnwalker, Tiny Tina's Wonderlands, DOOM: The Dark Ages, Avatar: Frontiers of Pandora, Control, The Witcher 3, Dota 2 and Deadlock
  - Back up and restore settings files (saves are never touched)
  - Reset shaders per game, and clear the driver shader cache
  - Old files: delete shader caches and game folders left by uninstalled Steam games
  - Graphics presets (Competitive, Balanced, Quality, Ultra, Handheld) tuned to your graphics card, video memory and screen, for Unreal Engine games and Call of Duty. Every preset shows what changes and backs up your settings first

- **Saves:** back up and restore the save games of any game. ZenTune finds saves with the open [Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest) (MIT license, built from PCGamingWiki and downloaded once), and you can add a game by hand with its save folder. Backups are kept in a `ZenTune-backup` folder with one folder per PC (you choose where). Restore puts the files back where they came from, asks first, and makes a safety backup of the current save before it overwrites anything. Optional, all off by default: back up automatically when a game closes (ZenTune only reads the list of running programs), back up all games on a schedule, and a Windows notification when it backs up by itself
- **Cloud:** upload your backups to OneDrive, Google Drive, Dropbox or Elkjøp Cloud, to one or several accounts at once, with optional encryption. It uses [rclone](https://rclone.org) (MIT license), which ZenTune downloads once and checks with a SHA-256 checksum. ZenTune only copies files up; it never deletes anything in your cloud. Your login stays in a file on your own PC
- **Measure:** records the FPS and frame times of a running game with PresentMon (Intel, MIT license; downloaded once and checked with a SHA-256 checksum), then compares two recordings, for example before and after a preset. You get a one-line verdict, four key numbers and a graph of the frame times, so you can see when the stutters happen. It adds no overlay and nothing inside the game. Optionally it saves how busy the processor and graphics card were during the recording (from Pulse), and **Compare with AI** explains the difference in plain words (needs your own free Gemini key)
- **Pulse:** a live performance monitor in plain words, like Task Manager's Performance tab but easier to read: processor, graphics card, graphics card temperature, memory, video memory, disk and network with a graph of the last minute, every processor thread, and the programs that use the most processor, graphics card and memory. It has a small always-on-top popout window, a tray icon with a right-click menu (optional alerts when something is hot or full), and a log that saves the numbers as text files in `Documents\Pulse`. Optional **sensors** (off by default) add processor temperature, speed and power through the PawnIO driver and the LibreHardwareMonitor library
- **Handheld:** battery and charger status, Windows power mode, a graphics memory check for ROG Ally, Legion Go and Steam Deck, the Handheld preset for all games at once (40 FPS on battery, 60 on the charger) and AMD tips
- **Feedback:** collects a small zip file with hardware and settings info (nothing personal) that you can send to the developer
- **Settings:** a color theme for the whole app (Default, Gamer, Vibrant, Ocean, Sunset, Crimson), start ZenTune with Windows and keep it in the tray, and your Gemini key and model
- **Advanced:** Windows tweaks from Chris Titus Tech's WinUtil (gaming, privacy, dark mode, Ultimate Performance), a restore point button, faster startup apps, a PC cleaner and a one-click O&O ShutUp10++ download. Every tweak records the old values and can be undone

## Anti-cheat

ZenTune never injects anything into games and draws nothing in them. Pulse, Measure and the sensors only read numbers from Windows, but **none of this has been tested against every anti-cheat**. The sensors use a kernel driver (PawnIO) that some anti-cheat may not like. Before you play online games with strict anti-cheat, such as Call of Duty, keep the sensors off, close the Pulse popout and press **Remove sensor driver** on the Pulse page. ZenTune does not record Valorant or Delta Force.

## Windows SmartScreen

ZenTune is not code-signed and it will not be, so Windows SmartScreen may say "Windows protected your PC" and show an unknown publisher the first time you run it. That is expected for a free open-source app without a paid certificate. Press **More info → Run anyway** if you trust where you got it. The source code is open (GPL-3.0), so you can read it and build it yourself.

ZenTune keeps its data in `%LOCALAPPDATA%\ZenTune` (settings backups, the list of changes, crash log). Save game backups are in the `ZenTune-backup` folder you chose (by default in Documents).

## Build

Requires the .NET 10 SDK on Windows 10/11 (x64).

```powershell
.\build.ps1 -Run
```

(or run `Build.bat`)

The app is placed in `out\ZenTune.exe`. It is self-contained and does not need the Microsoft Store.

## Roadmap

Done:

1. Hardware detection ✅
2. Find installed games (Steam, Epic), backup and undo ✅
3. Graphics presets ✅ (Unreal Engine games and Call of Duty)
4. Config writers for Modern Warfare 4 / Warzone, CS2, The Outer Worlds 1 and 2, Final Fantasy VII Rebirth ✅
5. Pulse: live monitor, popout, tray, logging, optional processor sensors ✅
6. Experimental AI best settings for Call of Duty ✅ (bring your own free Gemini key; backs up first, only writes values the game allows)
7. Saves: backup and restore for any game, one folder per PC, optional auto-backup, schedule and notifications ✅
8. Cloud backup to OneDrive, Google Drive, Dropbox and Elkjøp Cloud, with optional encryption ✅
9. Measure: verdict, key numbers and a frame time graph for before/after recordings ✅
10. Settings and color themes ✅

Next:

- Measure linked to presets and AI profiles: record before and after in one click
- Thumbs-up profiles and AI settings for more games
- More games with their own settings writers
- Driver settings (G-Sync/FreeSync, V-Sync, Reflex/Anti-Lag)
- Backup checks and exporting a single backup as a zip
- Testing on more PCs (AMD, laptops, handhelds)

## License

GPL-3.0

Pulse sensors use [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0) and, if you install it, the PawnIO driver by namazso. Measure uses [PresentMon](https://github.com/GameTechDev/PresentMon) (MIT). Saves uses the [Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest) (MIT) and [YamlDotNet](https://github.com/aaubry/YamlDotNet) (MIT). Cloud uses [rclone](https://github.com/rclone/rclone) (MIT).

## Credits

The Advanced tweaks are adapted from [WinUtil](https://github.com/ChrisTitusTech/winutil) by Chris Titus Tech (MIT license, Copyright (c) 2022 CT Tech Group LLC).
# ZenTune

A free, open-source game settings optimizer for Windows. ZenTune looks at your hardware and your screen and picks the settings that give you the best mix of FPS, image quality and low input lag, and explains why.

## Status

Version 0.5 (beta). Expect bugs, and please report them (Feedback page → Collect info).

- **Your PC:** graphics card, processor, memory, PC maker, and every display (resolution, refresh rate, HDR, which GPU it is plugged into)
- **System check:** finds settings that cost FPS (refresh rate, screen on the motherboard port, Game Mode, GPU scheduling, windowed optimizations, background recording, power plan, mouse acceleration, memory, and the GameInput version that can stop Call of Duty from launching). One-click fixes, and every fix can be undone
- **Games:** finds 33 games on Steam and Epic: Counter-Strike 2, Call of Duty: Modern Warfare 4, Call of Duty: Black Ops 7 / Warzone, Battlefield 6 / REDSEC, ARC Raiders, Apex Legends, Rainbow Six Siege, PUBG, Marvel Rivals, Helldivers 2, Fallout 76, Hogwarts Legacy, Avowed, Cyberpunk 2077, Elden Ring, The Outer Worlds 1 and 2, Final Fantasy VII Rebirth, The Finals, Dead by Daylight, Black Myth: Wukong, S.T.A.L.K.E.R. 2, Satisfactory, WARDOGS, Fortnite, The Blood of Dawnwalker, Tiny Tina's Wonderlands, DOOM: The Dark Ages, Avatar: Frontiers of Pandora, Control, The Witcher 3, Dota 2 and Deadlock
  - Back up and restore settings files (saves are never touched)
  - Reset shaders per game, and clear the driver shader cache
  - Old files: delete shader caches and game folders left by uninstalled Steam games
  - Graphics presets (Competitive, Balanced, Quality, Ultra, Handheld) tuned to your graphics card, video memory and screen, for Unreal Engine games and Call of Duty. Every preset shows what changes and backs up your settings first

- **Saves:** back up and restore the save games of any game. ZenTune finds saves with the open [Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest) (MIT license, built from PCGamingWiki and downloaded once), and you can add a game by hand with its save folder. Backups are kept in a `ZenTune-backup` folder with one folder per PC (you choose where). Restore puts the files back where they came from, asks first, and makes a safety backup of the current save before it overwrites anything. Optional, all off by default: back up automatically when a game closes (ZenTune only reads the list of running programs), back up all games on a schedule, and a Windows notification when it backs up by itself
- **Cloud:** upload your backups to OneDrive, Google Drive, Dropbox or Elkjøp Cloud, to one or several accounts at once, with optional encryption. It uses [rclone](https://rclone.org) (MIT license), which ZenTune downloads once and checks with a SHA-256 checksum. ZenTune only copies files up; it never deletes anything in your cloud. Your login stays in a file on your own PC
- **Measure:** records the FPS and frame times of a running game with PresentMon (Intel, MIT license; downloaded once and checked with a SHA-256 checksum), then compares two recordings, for example before and after a preset. You get a one-line verdict, four key numbers and a graph of the frame times, so you can see when the stutters happen. It adds no overlay and nothing inside the game. Optionally it saves how busy the processor and graphics card were during the recording (from Pulse), and **Compare with AI** explains the difference in plain words (needs your own free Gemini key)
- **Pulse:** a live performance monitor in plain words, like Task Manager's Performance tab but easier to read: processor, graphics card, graphics card temperature, memory, video memory, disk and network with a graph of the last minute, every processor thread, and the programs that use the most processor, graphics card and memory. It has a small always-on-top popout window, a tray icon with a right-click menu (optional alerts when something is hot or full), and a log that saves the numbers as text files in `Documents\Pulse`. Optional **sensors** (off by default) add processor temperature, speed and power through the PawnIO driver and the LibreHardwareMonitor library
- **Handheld:** battery and charger status, Windows power mode, a graphics memory check for ROG Ally, Legion Go and Steam Deck, the Handheld preset for all games at once (40 FPS on battery, 60 on the charger) and AMD tips
- **Feedback:** collects a small zip file with hardware and settings info (nothing personal) that you can send to the developer
- **Settings:** a color theme for the whole app (Default, Gamer, Vibrant, Ocean, Sunset, Crimson), start ZenTune with Windows and keep it in the tray, and your Gemini key and model
- **Advanced:** Windows tweaks from Chris Titus Tech's WinUtil (gaming, privacy, dark mode, Ultimate Performance), a restore point button, faster startup apps, a PC cleaner and a one-click O&O ShutUp10++ download. Every tweak records the old values and can be undone

## Anti-cheat

ZenTune never injects anything into games and draws nothing in them. Pulse, Measure and the sensors only read numbers from Windows, but **none of this has been tested against every anti-cheat**. The sensors use a kernel driver (PawnIO) that some anti-cheat may not like. Before you play online games with strict anti-cheat, such as Call of Duty, keep the sensors off, close the Pulse popout and press **Remove sensor driver** on the Pulse page. ZenTune does not record Valorant or Delta Force.

## Windows SmartScreen

ZenTune is not code-signed and it will not be, so Windows SmartScreen may say "Windows protected your PC" and show an unknown publisher the first time you run it. That is expected for a free open-source app without a paid certificate. Press **More info → Run anyway** if you trust where you got it. The source code is open (GPL-3.0), so you can read it and build it yourself.

ZenTune keeps its data in `%LOCALAPPDATA%\ZenTune` (settings backups, the list of changes, crash log). Save game backups are in the `ZenTune-backup` folder you chose (by default in Documents).

## Build

Requires the .NET 10 SDK on Windows 10/11 (x64).

```powershell
.\build.ps1 -Run
```

(or run `Build.bat`)

The app is placed in `out\ZenTune.exe`. It is self-contained and does not need the Microsoft Store.

## Roadmap

Roadmappet i README er allerede på engelsk. Jeg skrev det på engelsk da jeg oppdaterte det, siden resten av README og appen er på engelsk. Slik står det nå:

> **Done:**
 1. Hardware detection ✅
 2. Find installed games (Steam, Epic), backup and undo ✅
 3. Graphics presets ✅ (Unreal Engine games and Call of Duty)
 4. Config writers for Modern Warfare 4 / Warzone, CS2, The Outer Worlds 1 and 2, Final Fantasy VII Rebirth ✅
 5. Pulse: live monitor, popout, tray, logging, optional processor sensors ✅
 6. Experimental AI best settings for Call of Duty ✅
 7. Saves: backup and restore for any game, one folder per PC, optional auto-backup, schedule and notifications ✅
 8. Cloud backup to OneDrive, Google Drive, Dropbox and Elkjøp Cloud, with optional encryption ✅
 9. Measure: verdict, key numbers and a frame time graph for before/after recordings ✅
 10. Settings and color themes ✅

 **Next:** Measure linked to presets and AI profiles, thumbs-up profiles and AI for more games, more games with their own settings writers, driver settings, backup checks and zip export, testing on more PCs.

## License

GPL-3.0

Pulse sensors use [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0) and, if you install it, the PawnIO driver by namazso. Measure uses [PresentMon](https://github.com/GameTechDev/PresentMon) (MIT). Saves uses the [Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest) (MIT) and [YamlDotNet](https://github.com/aaubry/YamlDotNet) (MIT). Cloud uses [rclone](https://github.com/rclone/rclone) (MIT).

## Credits

The Advanced tweaks are adapted from [WinUtil](https://github.com/ChrisTitusTech/winutil) by Chris Titus Tech (MIT license, Copyright (c) 2022 CT Tech Group LLC).
