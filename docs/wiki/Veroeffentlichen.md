# Veröffentlichen in GHCR

Plugins werden als OCI-Paket in GitHub Packages (`ghcr.io`) abgelegt. Öffentliche Pakete lassen sich ohne Konto laden.

## Mit oras

```powershell
$env:GHCR_TOKEN | oras login ghcr.io -u DEIN-NAME --password-stdin
oras push ghcr.io/dein-name/mein-plugin:1.0.0 `
  --artifact-type application/vnd.fastenvsetter.plugin.v1 `
  mein-plugin.fesplugin:application/vnd.fastenvsetter.plugin.layer.v1+zip
```

Der Medientyp der Schicht muss exakt stimmen, und der Repository-Pfad muss kleingeschrieben sein. Das Token braucht `write:packages`.

Ein neues Paket ist zunächst privat. Stelle es unter **Package settings → Change visibility** auf **Public**.

## Laden

Im Launcher unter **Plugins** die Adresse `ghcr.io/dein-name/mein-plugin:1.0.0` eintragen. Mit `@sha256:…` statt eines Tags ist genau eine Version festgelegt.

## In diesem Repository

Lege dein Style-Plugin unter `plugins/<name>/` ab und setze einen Tag `plugin-<name>-v<version>`. `plugins.yml` veröffentlicht es dann. Für den Katalog ergänzt du `plugins/catalog.json` per Pull Request. Code-Plugins veröffentlichst du aus deinem eigenen Repository.
