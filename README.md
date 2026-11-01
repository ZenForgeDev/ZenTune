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

- **Measure:** records the FPS and frame times of a running game with PresentMon (Intel, MIT license; downloaded once and checked with a SHA-256 checksum), then compares two recordings, for example before and after a preset. It adds no overlay and nothing inside the game. Optionally it saves how busy the processor and graphics card were during the recording (from Pulse), and **Compare with AI** explains the difference in plain words (needs your own free Gemini key)
- **Pulse:** a live performance monitor in plain words, like Task Manager's Performance tab but easier to read: processor, graphics card, graphics card temperature, memory, video memory, disk and network with a graph of the last minute, every processor thread, and the programs that use the most processor, graphics card and memory. It has a small always-on-top popout window, a tray icon with a right-click menu (optional alerts when something is hot or full), and a log that saves the numbers as text files in `Documents\Pulse`. Optional **sensors** (off by default) add processor temperature, speed and power through the PawnIO driver and the LibreHardwareMonitor library
- **Handheld:** battery and charger status, Windows power mode, a graphics memory check for ROG Ally, Legion Go and Steam Deck, the Handheld preset for all games at once (40 FPS on battery, 60 on the charger) and AMD tips
- **Feedback:** collects a small zip file with hardware and settings info (nothing personal) that you can send to the developer
- **Advanced:** Windows tweaks from Chris Titus Tech's WinUtil (gaming, privacy, dark mode, Ultimate Performance), a restore point button, a PC cleaner and a one-click O&O ShutUp10++ download. Every tweak records the old values and can be undone

## Anti-cheat

ZenTune never injects anything into games and draws nothing in them. Pulse, Measure and the sensors only read numbers from Windows, but **none of this has been tested against every anti-cheat**. The sensors use a kernel driver (PawnIO) that some anti-cheat may not like. Before you play online games with strict anti-cheat, such as Call of Duty, keep the sensors off, close the Pulse popout and press **Remove sensor driver** on the Pulse page. ZenTune does not record Valorant or Delta Force.

## Windows SmartScreen

ZenTune is not code-signed and it will not be, so Windows SmartScreen may say "Windows protected your PC" and show an unknown publisher the first time you run it. That is expected for a free open-source app without a paid certificate. Press **More info → Run anyway** if you trust where you got it. The source code is open (GPL-3.0), so you can read it and build it yourself.

ZenTune keeps its data in `%LOCALAPPDATA%\ZenTune` (backups, the list of changes, crash log).

## Build

Requires the .NET 10 SDK on Windows 10/11 (x64).

```powershell
.\build.ps1 -Run
```

(or run `Build.bat`)

The app is placed in `out\ZenTune.exe`. It is self-contained and does not need the Microsoft Store.

## Roadmap

1. Hardware detection ✅
2. Find installed games (Steam, Epic), backup and undo ✅
3. Graphics presets ✅ (Unreal Engine games and Call of Duty; more games next)
4. Config writers for Modern Warfare 4, Warzone, CS2, The Outer Worlds 1 and 2, Final Fantasy VII Rebirth
5. Driver settings (G-Sync/FreeSync, V-Sync, Reflex/Anti-Lag)
6. Pulse: live monitor, popout, tray, logging, optional processor sensors, Measure + Pulse numbers ✅
7. Community mode with Gemini suggestions: **experimental AI best settings** for Call of Duty ✅ (bring your own free Gemini key; modes Ultra Performance, Performance, Balanced, Quality and Best FPS; backs up first, only writes values the game allows). Next: thumbs-up profiles and more games

## License

GPL-3.0

Pulse sensors use [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0) and, if you install it, the PawnIO driver by namazso. Measure uses [PresentMon](https://github.com/GameTechDev/PresentMon) (MIT).

## Credits

The Advanced tweaks are adapted from [WinUtil](https://github.com/ChrisTitusTech/winutil) by Chris Titus Tech (MIT license, Copyright (c) 2022 CT Tech Group LLC).
