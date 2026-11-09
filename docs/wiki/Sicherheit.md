# Sicherheit

**Code-Plugins laufen nicht in einer Sandbox.** Sie haben dieselben Rechte wie du und können Dateien lesen, Prozesse starten und auf entschlüsselte Secrets zugreifen.

## Für Nutzer

- Installiere Code-Plugins nur von Herausgebern, denen du vertraust. Der angezeigte Name stammt vom Autor und ist nicht verifiziert.
- Der Launcher zeigt vor der Installation Quelle und Prüfsumme und prüft vor jedem Laden, ob die Dateien unverändert sind. Das schützt vor nachträglichen Änderungen, nicht vor einem bösartigen Plugin.
- Style-Plugins werden auf reine Datendateien geprüft und enthalten keinen Code.

## Für Autoren

- Protokolliere keine Variablenwerte und übergib sie nicht als Kommandozeilenargumente.
- Lege im Feld `Permissions` offen, was dein Plugin tut.
- Veröffentliche den Quellcode, damit Nutzer ihn prüfen können.

Sicherheitslücken in der App meldest du wie in `SECURITY.md` beschrieben.
