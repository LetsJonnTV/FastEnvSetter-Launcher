# Style-Plugin schreiben

Ein Style besteht nur aus Daten. XAML ist bewusst nicht erlaubt, weil es Code auslösen kann. Erlaubte Dateitypen sind `.json`, `.png`, `.md` und `.txt`.

## 1. Dateien anlegen

`plugin.json` mit `"Kind": "style"` (siehe [Paketformat](Home)) und `theme.json`:

```json
{
  "Base": "Dark",
  "Accent": "#4DA3FF",
  "Background": "#0B1B33",
  "Card": "#14294A",
  "CardStroke": "#25426E",
  "TextPrimary": "#EAF2FF",
  "TextSecondary": "#B5C7E3"
}
```

| Feld | Pflicht | Bedeutung |
|---|---|---|
| `Base` | ja | `Dark` oder `Light` |
| `Accent` | ja | Akzentfarbe |
| `Background` | nein | Fensterhintergrund; ohne Angabe bleibt der Mica-Effekt |
| `Card`, `CardStroke` | nein | Karten und ihr Rand |
| `TextPrimary`, `TextSecondary` | nein | Textfarben |

Farben sind `#RRGGBB` oder `#AARRGGBB`. Ein vollständiges Beispiel liegt in `plugins/blue-style`.

## 2. Packen

```powershell
pwsh scripts/pack-plugin.ps1 -Path plugins/mein-style -Output mein-style.fesplugin
```

## 3. Testen

Im Launcher unter **Plugins → Aus Datei** die Datei wählen und unter **Einstellungen → Darstellung** den Style auswählen.
