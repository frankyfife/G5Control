# G5Control

Kleine Windows-App, die einen LG G5 OLED (webOS 26) mit vier festen Bild-Presets steuert, direkt aus dem Tray.

| Preset | Was passiert |
| --- | --- |
| **Gaming** | VRR an, Game Optimizer an (HDMI1), Eingangsoptimierung Boost, Helligkeit 100 |
| **Desktop Tag** | VRR an, Bildmodus Spiel, Helligkeit 90 |
| **Desktop Nacht** | VRR an, Bildmodus Spiel, Helligkeit 45 |
| **Minimal** | VRR aus, Spitzenhelligkeit aus, Energiesparen max, Dark Room Stufe 2, Helligkeit 0 |

Nach jedem Preset liest die App Bildmodus, Helligkeit und Energiesparen zurück und wiederholt Schritte, die der Fernseher während eines HDMI-Resyncs verschluckt hat.

## Bedienung

- Linksklick aufs Tray-Icon öffnet das Fenster, Rechtsklick zeigt die Presets direkt.
- „Mit Windows starten“ legt einen Autostart-Eintrag an (startet nur im Tray).
- Beim ersten Verbinden fragt der Fernseher einmal nach der Kopplung. Der Schlüssel landet in `%APPDATA%\G5Control\clientkey.txt`.

## Voraussetzungen

- Windows 10/11 mit [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- LG-Fernseher mit webOS 26 im selben Netz. Die IP steht fest in `LgTv.cs` (`Ip`), ebenso der Eingang HDMI1 in `Presets.cs`.

## Presets anpassen

Die Presets sind fest einprogrammiert, eine Einstellungsdatei gibt es nicht. Sie stehen in `Presets.cs` in der Liste `Presets.All`. Nach einer Änderung neu bauen (siehe unten).

Jedes Preset besteht aus Name, Beschreibung, Symbol (Zeichen aus der Schrift *Segoe Fluent Icons*) und einer Folge von Schritten. Ein Schritt schreibt eine TV-Einstellung:

```csharp
new("Desktop Abend", "Helligkeit 70, VRR an", "",
[
    Vrr("on"),
    .. Restore,              // Bildmodus Spiel, Energiesparen und Dark Room aus
    new("backlight", "70"),  // Helligkeit 0–100
]),
```

Brauchbare Einstellungen:

| Schlüssel | Kategorie | Werte |
| --- | --- | --- |
| `backlight` | picture | `0`–`100` |
| `pictureMode` | picture | `game`, `eco`, … |
| `energySaving` | picture | `off`, `auto`, `min`, `med`, `max` |
| `peakBrightness` | picture | `off` (genutzt in „Minimal“) |
| `darkMode` (Dark Room) | other | `off`, `level1`, `level2` |
| `gameOptimization` (VRR) | other | `on`, `off`, besser über `Vrr(...)` |
| `gameMode_hdmi1` (Game Optimizer) | other | `on`, `off` |
| `inputOptimization` | other | `auto`, `on`, `standard`, `boost` |

Hinweise:

- Schritte, die ein HDMI-Resync auslösen (VRR, Game Optimizer, Eingangsoptimierung), gehören an den Anfang und bekommen `Resyncs: true` sowie eine Pause (`DelayMs`). Sonst landen die folgenden Werte im falschen Bildmodus.
- Für einen anderen HDMI-Eingang `gameMode_hdmi1` entsprechend ändern (z. B. `gameMode_hdmi2`).
- Den Bildmodus `eco` besser nicht setzen: Der Eingang merkt ihn sich dauerhaft.

## Bauen

```bash
dotnet publish -c Release -o publish
```

Das Icon wird mit `make-icon.ps1` erzeugt (`-Preview pfad.png` rendert eine Vorschau).

## Technik

Die App spricht das SSAP-Protokoll über `wss://<tv>:3001`. Einstellungen werden über `luna://com.webos.settingsservice/setSystemSettings` geschrieben, verpackt in eine kurz geöffnete Systemmeldung. Das Registrierungs-Manifest in `register.json` stammt aus [ColorControl](https://github.com/Maassoft/ColorControl), weil webOS 26 ältere Manifeste ablehnt.

## Lizenz

Copyright (C) 2026 frankyfife

G5Control steht unter der [GNU General Public License v3.0](LICENSE), wie ColorControl, aus dem `register.json` übernommen ist.
